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
        var whereFound = await FindClaudePathAsync(ct);
        var alreadyInstalled = whereFound is not null;

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
                    // 权威路径：npm 实际装到哪就用哪（便携 Node 的全局前缀不是 %AppData%\npm）。
                    // 安装后重新 `where claude`，其次取安装前 found，最后回退默认 AppData 路径。
                    var finalPath = await FindClaudePathAsync(ct) ?? whereFound ?? DefaultClaudeCmd();
                    return new ClaudeInstallResult(alreadyInstalled, finalPath);
                }
                lastError = new InvalidOperationException($"npm 退出码 {result.ExitCode}: {result.StandardError}");
            }
            catch (Exception ex) { lastError = ex; }
        }
        throw lastError ?? new InvalidOperationException("npm 安装失败。");
    }

    private async Task<string?> FindClaudePathAsync(CancellationToken ct)
    {
        var where = await _runner.RunAsync("where.exe", new[] { "claude" }, null, null, ct);
        if (where.ExitCode == 0 && !string.IsNullOrWhiteSpace(where.StandardOutput))
            return where.StandardOutput.Trim().Split('\n')[0].Trim();
        return null;
    }

    private static string DefaultClaudeCmd() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");
}
