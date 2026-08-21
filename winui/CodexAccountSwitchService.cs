using System.Diagnostics;
using System.Text.Json;

namespace FluentAgentBar;

internal static class CodexAccountSwitchService
{
    private static readonly TimeSpan ProcessCommandTimeout = TimeSpan.FromSeconds(8);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string GetDefaultCodexHome()
    {
        string? codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            return Environment.ExpandEnvironmentVariables(codexHome);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    internal static string ProfileAuthPath(ProfileConfig profile)
    {
        return Path.Combine(Environment.ExpandEnvironmentVariables(profile.Home), "auth.json");
    }

    internal static bool HasProfileAuth(ProfileConfig profile)
    {
        return AppConfigStore.IsProvider(profile, "codex") &&
            File.Exists(ProfileAuthPath(profile));
    }

    internal static bool IsActiveProfile(ProfileConfig profile)
    {
        return IsActiveProfile(profile, GetDefaultCodexHome());
    }

    internal static bool IsActiveProfile(ProfileConfig profile, string codexHome)
    {
        if (!AppConfigStore.IsProvider(profile, "codex"))
        {
            return false;
        }

        string sourceAuthPath = ProfileAuthPath(profile);
        string targetAuthPath = Path.Combine(codexHome, "auth.json");
        if (!File.Exists(sourceAuthPath) || !File.Exists(targetAuthPath))
        {
            return false;
        }

        AuthIdentity? sourceIdentity = TryReadAuthIdentity(sourceAuthPath);
        AuthIdentity? targetIdentity = TryReadAuthIdentity(targetAuthPath);
        if (sourceIdentity is not null && targetIdentity is not null)
        {
            return sourceIdentity == targetIdentity;
        }

        return FilesEqual(sourceAuthPath, targetAuthPath);
    }

    internal static void CopyProfileAuthToCodexHome(ProfileConfig profile, string codexHome)
    {
        if (!AppConfigStore.IsProvider(profile, "codex"))
        {
            throw new InvalidOperationException("Only Codex profiles can be switched.");
        }

        string sourceAuthPath = ProfileAuthPath(profile);
        if (!File.Exists(sourceAuthPath))
        {
            throw new FileNotFoundException(
                $"Profile \"{profile.Label}\" is not signed in yet. Use Login first.",
                sourceAuthPath);
        }

        Directory.CreateDirectory(codexHome);
        string targetAuthPath = Path.Combine(codexHome, "auth.json");

        if (Path.GetFullPath(sourceAuthPath).Equals(Path.GetFullPath(targetAuthPath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CopyAuthFile(sourceAuthPath, targetAuthPath);
    }

    internal static bool TrySynchronizeProfileAuthFromCodexHome(ProfileConfig profile, string codexHome)
    {
        try
        {
            if (!AppConfigStore.IsProvider(profile, "codex"))
            {
                return false;
            }

            string profileAuthPath = ProfileAuthPath(profile);
            string activeAuthPath = Path.Combine(codexHome, "auth.json");
            if (!File.Exists(profileAuthPath) || !File.Exists(activeAuthPath))
            {
                return false;
            }

            if (Path.GetFullPath(profileAuthPath).Equals(
                    Path.GetFullPath(activeAuthPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            AuthIdentity? profileIdentity = TryReadAuthIdentity(profileAuthPath);
            AuthIdentity? activeIdentity = TryReadAuthIdentity(activeAuthPath);
            if (profileIdentity is null || activeIdentity is null || profileIdentity != activeIdentity)
            {
                return false;
            }

            if (File.GetLastWriteTimeUtc(activeAuthPath) <= File.GetLastWriteTimeUtc(profileAuthPath) ||
                FilesEqual(profileAuthPath, activeAuthPath))
            {
                return false;
            }

            CopyAuthFile(activeAuthPath, profileAuthPath);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    internal static async Task<CodexAccountSwitchResult> SwitchToProfileAsync(
        ProfileConfig profile,
        CancellationToken cancellationToken = default)
    {
        if (!AppConfigStore.IsProvider(profile, "codex"))
        {
            throw new InvalidOperationException("Only Codex profiles can be switched.");
        }

        if (!HasProfileAuth(profile))
        {
            throw new InvalidOperationException($"Profile \"{profile.Label}\" is not signed in yet. Use Login first.");
        }

        CodexProcessCloseResult closeResult = await ForceCloseCodexProcessesAsync(cancellationToken);
        if (closeResult.FailedPids.Count > 0)
        {
            throw new InvalidOperationException(
                "Could not close Codex process " + string.Join(", ", closeResult.FailedPids) + ".");
        }

        CopyProfileAuthToCodexHome(profile, GetDefaultCodexHome());
        CodexOpenResult openResult = await OpenCodexAsync(cancellationToken);

        return new CodexAccountSwitchResult(
            profile.Label,
            closeResult.TargetedCount,
            closeResult.KilledPids.Count,
            openResult.Opened,
            openResult.ErrorMessage);
    }

    private static async Task<CodexProcessCloseResult> ForceCloseCodexProcessesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<int> pids = await FindActiveCodexProcessIdsAsync(cancellationToken);
        List<int> killed = [];
        List<int> failed = [];

        foreach (int pid in pids)
        {
            if (await KillProcessTreeAsync(pid, cancellationToken))
            {
                killed.Add(pid);
            }
            else
            {
                failed.Add(pid);
            }
        }

        return new CodexProcessCloseResult(pids.Count, killed, failed);
    }

    private static async Task<IReadOnlyList<int>> FindActiveCodexProcessIdsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<WindowsCodexProcess> processes = await QueryWindowsCodexProcessesAsync(cancellationToken);
        List<int> activePids = [];

        foreach (WindowsCodexProcess process in processes.Where(IsWindowsCodexRootProcess))
        {
            string command = process.CommandLine.ToLowerInvariant();
            if (IsIdePluginProcess(command))
            {
                continue;
            }

            bool hasWindow = !string.IsNullOrWhiteSpace(process.MainWindowTitle);
            bool hasRenderer = WindowsHasDescendantMatching(process.ProcessId, processes, child =>
                child.CommandLine.Contains("--type=renderer", StringComparison.OrdinalIgnoreCase));
            bool hasAppServer = WindowsHasDescendantMatching(process.ProcessId, processes, child =>
            {
                string childCommand = child.CommandLine.Replace('/', '\\').ToLowerInvariant();
                return childCommand.Contains("resources\\codex.exe", StringComparison.Ordinal) &&
                    childCommand.Contains("app-server", StringComparison.Ordinal);
            });

            if (hasWindow || hasRenderer || hasAppServer)
            {
                activePids.Add(process.ProcessId);
            }
        }

        activePids.Sort();
        return activePids.Distinct().ToList();
    }

    private static async Task<IReadOnlyList<WindowsCodexProcess>> QueryWindowsCodexProcessesAsync(CancellationToken cancellationToken)
    {
        const string script = """
$windowTitles = @{}
Get-Process -Name Codex -ErrorAction SilentlyContinue | ForEach-Object {
  $windowTitles[[int]$_.Id] = $_.MainWindowTitle
}

Get-CimInstance Win32_Process |
  Where-Object { $_.Name -ieq 'Codex.exe' -or $_.Name -ieq 'codex.exe' } |
  ForEach-Object {
    [PSCustomObject]@{
      Name = $_.Name
      ProcessId = [int]$_.ProcessId
      ParentProcessId = [int]$_.ParentProcessId
      CommandLine = if ($_.CommandLine) { $_.CommandLine } else { '' }
      MainWindowTitle = if ($windowTitles.ContainsKey([int]$_.ProcessId)) {
        [string]$windowTitles[[int]$_.ProcessId]
      } else {
        ''
      }
    }
  } |
  ConvertTo-Json -Compress
""";

        ProcessResult result = await RunHiddenProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", script],
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Could not query Codex processes: " + result.Error.Trim());
        }

        string json = result.Output.Trim();
        if (json.Length == 0)
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            return document.RootElement
                .EnumerateArray()
                .Select(element => element.Deserialize<WindowsCodexProcess>(JsonOptions))
                .OfType<WindowsCodexProcess>()
                .ToList();
        }

        WindowsCodexProcess? process = document.RootElement.Deserialize<WindowsCodexProcess>(JsonOptions);
        return process is null ? [] : [process];
    }

    private static bool IsWindowsCodexRootProcess(WindowsCodexProcess process)
    {
        string name = process.Name.ToLowerInvariant();
        string command = process.CommandLine.Replace('/', '\\').ToLowerInvariant();

        return name == "codex.exe" &&
            !command.Contains("fluentagentbar", StringComparison.Ordinal) &&
            !command.Contains("codex-switcher", StringComparison.Ordinal) &&
            !command.Contains("--type=", StringComparison.Ordinal) &&
            !command.Contains("app-server", StringComparison.Ordinal) &&
            !command.Contains("resources\\codex.exe", StringComparison.Ordinal);
    }

    private static bool IsIdePluginProcess(string command)
    {
        return command.Contains(".antigravity", StringComparison.Ordinal) ||
            command.Contains("openai.chatgpt", StringComparison.Ordinal) ||
            command.Contains(".vscode", StringComparison.Ordinal);
    }

    private static bool WindowsHasDescendantMatching(
        int rootPid,
        IReadOnlyList<WindowsCodexProcess> processes,
        Func<WindowsCodexProcess, bool> predicate)
    {
        Queue<int> queue = new();
        HashSet<int> visited = [];
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            int parentPid = queue.Dequeue();
            foreach (WindowsCodexProcess child in processes.Where(process => process.ParentProcessId == parentPid))
            {
                if (!visited.Add(child.ProcessId))
                {
                    continue;
                }

                if (predicate(child))
                {
                    return true;
                }

                queue.Enqueue(child.ProcessId);
            }
        }

        return false;
    }

    private static async Task<bool> KillProcessTreeAsync(int pid, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunHiddenProcessAsync(
            "taskkill.exe",
            ["/F", "/T", "/PID", pid.ToString()],
            cancellationToken);

        return result.ExitCode == 0 || !ProcessExists(pid);
    }

    private static bool ProcessExists(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<CodexOpenResult> OpenCodexAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await OpenRegisteredCodexAppAsync(cancellationToken))
            {
                return new CodexOpenResult(true, null);
            }

            if (TryOpenCodexExecutable())
            {
                return new CodexOpenResult(true, null);
            }

            if (TryOpenCodexShortcut())
            {
                return new CodexOpenResult(true, null);
            }

            return new CodexOpenResult(false, "Codex app was not found.");
        }
        catch (Exception ex)
        {
            return new CodexOpenResult(false, ex.Message);
        }
    }

    private static async Task<bool> OpenRegisteredCodexAppAsync(CancellationToken cancellationToken)
    {
        const string script = """
$app = Get-StartApps |
  Where-Object {
    ($_.Name -eq 'Codex' -or
     $_.Name -like 'OpenAI Codex*' -or
     $_.AppID -like '*OpenAI.Codex*' -or
     $_.AppID -like '*OpenAI*Codex*') -and
     $_.Name -notlike '*Switcher*'
  } |
  Select-Object -First 1
if ($null -eq $app) { exit 1 }
Start-Process ("shell:AppsFolder\" + $app.AppID)
""";

        ProcessResult result = await RunHiddenProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", script],
            cancellationToken);
        return result.ExitCode == 0;
    }

    private static bool TryOpenCodexExecutable()
    {
        foreach (string candidate in FindCodexExecutableCandidates())
        {
            if (!File.Exists(candidate) || !LooksLikeCodexDesktopApp(candidate))
            {
                continue;
            }

            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = candidate,
                    WorkingDirectory = Path.GetDirectoryName(candidate) ?? string.Empty,
                    UseShellExecute = true
                };
                Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        return false;
    }

    private static IEnumerable<string> FindCodexExecutableCandidates()
    {
        List<string> candidates = [];
        foreach (string key in new[] { "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)" })
        {
            string? basePath = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(basePath))
            {
                continue;
            }

            candidates.Add(Path.Combine(basePath, "Programs", "Codex", "Codex.exe"));
            candidates.Add(Path.Combine(basePath, "Programs", "codex", "Codex.exe"));
            candidates.Add(Path.Combine(basePath, "Codex", "Codex.exe"));
            candidates.Add(Path.Combine(basePath, "OpenAI", "Codex", "Codex.exe"));
            candidates.Add(Path.Combine(basePath, "OpenAI", "Codex", "bin", "codex.exe"));
            candidates.Add(Path.Combine(basePath, "OpenAI Codex", "Codex.exe"));
            candidates.Add(Path.Combine(basePath, "Codex Desktop", "Codex.exe"));
        }

        string? localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            string programs = Path.Combine(localAppData, "Programs");
            candidates.AddRange(CollectFiles(programs, "Codex.exe", maxDepth: 2));
            string packages = Path.Combine(localAppData, "Packages");
            candidates.AddRange(FindPackagedCodexExecutables(packages));
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> FindPackagedCodexExecutables(string packagesRoot)
    {
        if (!Directory.Exists(packagesRoot))
        {
            yield break;
        }

        foreach (string directory in Directory.EnumerateDirectories(packagesRoot))
        {
            string name = Path.GetFileName(directory);
            if (!name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return Path.Combine(
                directory,
                "LocalCache",
                "Local",
                "OpenAI",
                "Codex",
                "bin",
                "codex.exe");
        }
    }

    private static bool LooksLikeCodexDesktopApp(string path)
    {
        string normalized = path.Replace('/', '\\').ToLowerInvariant();
        if (normalized.Contains("\\openai\\codex\\bin\\codex.exe", StringComparison.Ordinal))
        {
            return true;
        }

        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(parent, "resources")) ||
            File.Exists(Path.Combine(parent, "resources", "app.asar")) ||
            Directory.Exists(Path.Combine(parent, "resources", "app"));
    }

    private static bool TryOpenCodexShortcut()
    {
        foreach (string shortcut in FindCodexShortcuts())
        {
            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = "cmd.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("/C");
                startInfo.ArgumentList.Add("start");
                startInfo.ArgumentList.Add("");
                startInfo.ArgumentList.Add(shortcut);
                Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        return false;
    }

    private static IEnumerable<string> FindCodexShortcuts()
    {
        foreach (string key in new[] { "APPDATA", "ProgramData" })
        {
            string? basePath = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(basePath))
            {
                continue;
            }

            string programs = Path.Combine(basePath, "Microsoft", "Windows", "Start Menu", "Programs");
            foreach (string shortcut in CollectFiles(programs, "*.lnk", maxDepth: 3)
                         .Where(path => Path.GetFileName(path).Contains("codex", StringComparison.OrdinalIgnoreCase)))
            {
                yield return shortcut;
            }
        }
    }

    private static IEnumerable<string> CollectFiles(string root, string pattern, int maxDepth)
    {
        if (!Directory.Exists(root) || maxDepth < 0)
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, pattern);
        }
        catch
        {
            yield break;
        }

        foreach (string file in files)
        {
            yield return file;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root);
        }
        catch
        {
            yield break;
        }

        foreach (string directory in directories)
        {
            foreach (string file in CollectFiles(directory, pattern, maxDepth - 1))
            {
                yield return file;
            }
        }
    }

    private static async Task<ProcessResult> RunHiddenProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(ProcessCommandTimeout, cancellationToken);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static AuthIdentity? TryReadAuthIdentity(string authPath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(authPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryGetString(root, "OPENAI_API_KEY", out string? apiKey))
            {
                return new AuthIdentity("api", apiKey);
            }

            if (TryGetProperty(root, "tokens", out JsonElement tokens) &&
                tokens.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in new[] { "account_id", "refresh_token", "id_token", "access_token" })
                {
                    if (TryGetString(tokens, key, out string? tokenValue))
                    {
                        return new AuthIdentity(key, tokenValue);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        return null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (TryGetProperty(element, propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool FilesEqual(string leftPath, string rightPath)
    {
        try
        {
            byte[] left = File.ReadAllBytes(leftPath);
            byte[] right = File.ReadAllBytes(rightPath);
            return left.AsSpan().SequenceEqual(right);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    private static void CopyAuthFile(string sourceAuthPath, string targetAuthPath)
    {
        string targetDirectory = Path.GetDirectoryName(targetAuthPath)
            ?? throw new InvalidOperationException("The Codex auth destination has no parent directory.");
        Directory.CreateDirectory(targetDirectory);

        string tempPath = Path.Combine(targetDirectory, $"auth.json.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourceAuthPath, tempPath, overwrite: true);
            File.Move(tempPath, targetAuthPath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private sealed record AuthIdentity(string Kind, string Value);

    private sealed record WindowsCodexProcess
    {
        public string Name { get; init; } = string.Empty;
        public int ProcessId { get; init; }
        public int ParentProcessId { get; init; }
        public string CommandLine { get; init; } = string.Empty;
        public string MainWindowTitle { get; init; } = string.Empty;
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed record CodexOpenResult(bool Opened, string? ErrorMessage);

    private sealed record CodexProcessCloseResult(
        int TargetedCount,
        IReadOnlyList<int> KilledPids,
        IReadOnlyList<int> FailedPids);
}

internal sealed record CodexAccountSwitchResult(
    string ProfileLabel,
    int TargetedProcessCount,
    int ClosedProcessCount,
    bool CodexOpened,
    string? OpenError);
