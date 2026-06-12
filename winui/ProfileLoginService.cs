using System.Diagnostics;

namespace FluentAgentBar;

internal static class ProfileLoginService
{
    public static bool StartLogin(ProfileConfig profile, out string errorMessage)
    {
        errorMessage = string.Empty;

        try
        {
            string provider = AppConfigStore.NormalizeProvider(profile.Provider);
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
                _ => CreateCodexLoginStartInfo(home)
            };

            Process? process = Process.Start(startInfo);
            if (process is null)
            {
                errorMessage = "The login process did not start.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
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

    private static ProcessStartInfo CreateClaudeLoginStartInfo(string claudeConfigDir)
    {
        ProcessStartInfo startInfo = CreateCmdStartInfo(createNoWindow: false);
        startInfo.ArgumentList.Add("/D");
        startInfo.ArgumentList.Add("/K");
        startInfo.ArgumentList.Add("claude /login");
        startInfo.Environment["CLAUDE_CONFIG_DIR"] = claudeConfigDir;
        startInfo.WorkingDirectory = claudeConfigDir;
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
}
