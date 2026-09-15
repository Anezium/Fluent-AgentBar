using System.Diagnostics;

namespace FluentAgentBar;

internal static class ProfileLoginService
{
    // Cursor has no CLI login: usage is read from the session the desktop app
    // stores, so the best we can do is open the account page.
    internal const string CursorDashboardUrl = "https://cursor.com/dashboard";

    internal const string GrokCliDocsUrl = "https://x.ai/cli";

    internal const string GeminiCliDocsUrl = "https://github.com/google-gemini/gemini-cli";

    public static bool StartLogin(ProfileConfig profile, out string errorMessage)
    {
        errorMessage = string.Empty;

        try
        {
            string provider = AppConfigStore.NormalizeProvider(profile.Provider);
            if (string.Equals(provider, "cursor", StringComparison.Ordinal))
            {
                return TryStart(
                    new ProcessStartInfo { FileName = CursorDashboardUrl, UseShellExecute = true },
                    out errorMessage);
            }

            if (string.Equals(provider, "gemini", StringComparison.Ordinal) && !IsOnPath("gemini"))
            {
                errorMessage =
                    "The Gemini CLI was not found on PATH. Install it (npm install -g @google/gemini-cli), " +
                    $"see {GeminiCliDocsUrl}, then try again.";
                return false;
            }

            if (string.Equals(provider, "grok", StringComparison.Ordinal) && !IsOnPath("grok"))
            {
                errorMessage =
                    "The Grok CLI was not found on PATH. The shell installer published by xAI is macOS/Linux " +
                    $"only; follow the Windows instructions at {GrokCliDocsUrl}, then try again.";
                return false;
            }

            string home = Environment.ExpandEnvironmentVariables(profile.Home);
            if (string.IsNullOrWhiteSpace(home))
            {
                errorMessage = "Profile home is empty.";
                return false;
            }

            Directory.CreateDirectory(home);
            ProcessStartInfo startInfo = provider switch
            {
                "claude" => CreateClaudeLoginStartInfo(home),
                "gemini" => CreateGeminiLoginStartInfo(home),
                "grok" => CreateGrokLoginStartInfo(home),
                _ => CreateCodexLoginStartInfo(home)
            };

            return TryStart(startInfo, out errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool TryStart(ProcessStartInfo startInfo, out string errorMessage)
    {
        errorMessage = string.Empty;
        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            errorMessage = "The login process did not start.";
            return false;
        }

        return true;
    }

    private static ProcessStartInfo CreateCodexLoginStartInfo(string codexHome)
    {
        ProcessStartInfo startInfo = CreateCmdStartInfo(createNoWindow: true);
        startInfo.ArgumentList.Add("/D");
        startInfo.ArgumentList.Add("/S");
        startInfo.ArgumentList.Add("/C");
        startInfo.ArgumentList.Add("codex login");
        startInfo.Environment["CODEX_HOME"] = codexHome;
        return startInfo;
    }

    internal static ProcessStartInfo CreateClaudeLoginStartInfo(string claudeConfigDir)
    {
        ProcessStartInfo startInfo = CreateCmdStartInfo(createNoWindow: false);
        startInfo.ArgumentList.Add("/D");
        startInfo.ArgumentList.Add("/K");
        startInfo.ArgumentList.Add("claude /login");
        // A setup-token is inference-only by default and takes precedence over
        // the profile credentials in Claude Code. Clear it for this child so
        // the Login action always creates a full user:profile session.
        startInfo.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = string.Empty;
        startInfo.Environment["CLAUDE_CODE_OAUTH_SCOPES"] = string.Empty;
        startInfo.Environment["CLAUDE_CONFIG_DIR"] = claudeConfigDir;
        startInfo.WorkingDirectory = claudeConfigDir;
        return startInfo;
    }

    // The Gemini CLI has no dedicated login verb: it runs the OAuth flow on
    // first start, so the console has to stay open for the prompt.
    internal static ProcessStartInfo CreateGeminiLoginStartInfo(string geminiHome)
    {
        ProcessStartInfo startInfo = CreateCmdStartInfo(createNoWindow: false);
        startInfo.ArgumentList.Add("/D");
        startInfo.ArgumentList.Add("/K");
        startInfo.ArgumentList.Add("gemini");
        startInfo.WorkingDirectory = geminiHome;
        return startInfo;
    }

    internal static ProcessStartInfo CreateGrokLoginStartInfo(string grokHome)
    {
        ProcessStartInfo startInfo = CreateCmdStartInfo(createNoWindow: false);
        startInfo.ArgumentList.Add("/D");
        startInfo.ArgumentList.Add("/K");
        startInfo.ArgumentList.Add("grok login");
        startInfo.WorkingDirectory = grokHome;
        return startInfo;
    }

    private static ProcessStartInfo CreateCmdStartInfo(bool createNoWindow)
    {
        string cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return new ProcessStartInfo
        {
            FileName = File.Exists(cmdPath) ? cmdPath : "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = createNoWindow
        };
    }

    internal static bool IsOnPath(string command)
    {
        return ResolveOnPath(
            command,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT")) is not null;
    }

    // Exposed for tests: resolves a bare command name the way cmd.exe would.
    internal static string? ResolveOnPath(string command, string? path, string? pathExt)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string[] extensions = (string.IsNullOrWhiteSpace(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string directory in (path ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(directory.Trim('"'), command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
