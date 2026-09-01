namespace ClaudeCodeInstaller.Core;

public record ClaudeInstallResult(bool AlreadyInstalled, string ClaudeCmd);

public interface IClaudeInstaller
{
    Task<ClaudeInstallResult> EnsureClaudeAsync(string npmCmd, IProgress<string>? log, CancellationToken ct);
}

public sealed class ClaudeInstaller : IClaudeInstaller
{
    private readonly IProcessRunner _runner;

    public ClaudeInstaller(IProcessRunner runner) => _runner = runner;

    public async Task<ClaudeInstallResult> EnsureClaudeAsync(string npmCmd, IProgress<string>? log, CancellationToken ct)
    {
        var claudeCmd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");

        var where = await _runner.RunAsync("where.exe", new[] { "claude" }, null, null, ct);
        var alreadyInstalled = where.ExitCode == 0 && !string.IsNullOrWhiteSpace(where.StandardOutput);
        if (alreadyInstalled && File.Exists(claudeCmd))
        {
            claudeCmd = where.StandardOutput.Trim().Split('\n')[0].Trim();
        }

        Exception? lastError = null;
        foreach (var registry in VersionInfo.NpmRegistries)
        {
            try
            {
                log?.Report($"正在安装 Claude CLI（registry: {registry}）…");
                var result = await _runner.RunAsync(npmCmd, new[]
                {
                    "install", "-g", VersionInfo.ClaudePackage,
                    "--registry=" + registry, "--no-fund", "--no-audit",
                }, null, log, ct);
                if (result.ExitCode == 0)
                {
                    log?.Report("Claude CLI 安装完成。");
                    return new ClaudeInstallResult(alreadyInstalled, claudeCmd);
                }
                lastError = new InvalidOperationException($"npm 退出码 {result.ExitCode}: {result.StandardError}");
            }
            catch (Exception ex) { lastError = ex; }
        }
        throw lastError ?? new InvalidOperationException("npm 安装失败。");
    }
}
