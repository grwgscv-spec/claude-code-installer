namespace ClaudeCodeInstaller.Core;

public class InstallOptions
{
    public required string ApiKey { get; init; }
    public required string Model { get; init; }
    public bool InstallCcSwitch { get; init; } = true;
}

public enum InstallStepId { Node, Claude, CcSwitch, Config, Verify }

public sealed class InstallationEngine
{
    private readonly INodeInstaller _node;
    private readonly IClaudeInstaller _claude;
    private readonly ICcSwitchInstaller _ccSwitch;
    private readonly IConfigWriter _config;
    private readonly string _userProfileDir;
    private readonly IProcessRunner _verifyRunner;

    public event Action<InstallStepId, string>? StepStarted;
    public event Action<string>? Log;
    public event Action<int>? Progress;
    public event Action<string, bool>? Finished;

    public InstallationEngine(INodeInstaller node, IClaudeInstaller claude, ICcSwitchInstaller ccSwitch,
        IConfigWriter config, string userProfileDir, IProcessRunner? verifyRunner = null)
    {
        _node = node;
        _claude = claude;
        _ccSwitch = ccSwitch;
        _config = config;
        _userProfileDir = userProfileDir;
        _verifyRunner = verifyRunner ?? new ProcessRunner();
    }

    public async Task RunAsync(InstallOptions options, CancellationToken ct = default)
    {
        var log = new Progress<string>(s => Log?.Invoke(s));
        var ccSwitchMessage = "已跳过 cc-switch。";
        try
        {
            // 1. Node（0–30）
            StepStarted?.Invoke(InstallStepId.Node, "检查 / 安装 Node.js");
            Progress?.Invoke(5);
            var node = await _node.EnsureNodeAsync(_userProfileDir, log, ct);

            // 2. Claude（30–50）
            StepStarted?.Invoke(InstallStepId.Claude, "安装 Claude CLI");
            Progress?.Invoke(35);
            var claude = await _claude.EnsureClaudeAsync(node.NpmCmd, log, ct);

            // 3. cc-switch（50–70），可选，失败不阻塞
            if (options.InstallCcSwitch)
            {
                StepStarted?.Invoke(InstallStepId.CcSwitch, "安装 cc-switch");
                Progress?.Invoke(55);
                try
                {
                    await _ccSwitch.EnsureCcSwitchAsync(log, ct);
                    ccSwitchMessage = "已安装 cc-switch。";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ccSwitchMessage = "cc-switch 安装失败（已跳过，不影响使用）: " + ex.Message;
                    Log?.Invoke(ccSwitchMessage);
                }
            }

            // 4. 写配置（70–90）
            StepStarted?.Invoke(InstallStepId.Config, "写入 DeepSeek 配置");
            Progress?.Invoke(75);
            var cfg = await _config.WriteDeepSeekConfigAsync(_userProfileDir, options.ApiKey, options.Model, ct);
            if (cfg.SettingsBackupPath is not null)
                Log?.Invoke($"已备份原 settings.json → {cfg.SettingsBackupPath}");
            if (!cfg.CcSwitchSeeded)
                Log?.Invoke(cfg.CcSwitchSeedMessage);

            // 5. 验证（90–100）
            StepStarted?.Invoke(InstallStepId.Verify, "验证安装");
            Progress?.Invoke(95);
            var claudeVersion = await VerifyAsync(claude.ClaudeCmd, ct);

            Progress?.Invoke(100);
            var summary = string.Join("\n",
                "安装完成 ✔",
                $"Claude CLI: {claude.ClaudeCmd}",
                string.IsNullOrEmpty(claudeVersion) ? "版本检查未通过" : $"版本: {claudeVersion}",
                ccSwitchMessage);
            Finished?.Invoke(summary, true);
        }
        catch (OperationCanceledException)
        {
            Finished?.Invoke("已取消安装。", false);
        }
        catch (Exception ex)
        {
            Finished?.Invoke("安装失败：" + ex.Message, false);
        }
    }

    private async Task<string> VerifyAsync(string claudeCmd, CancellationToken ct)
    {
        try
        {
            var r = await _verifyRunner.RunAsync(claudeCmd, new[] { "--version" }, null, null, ct);
            return r.ExitCode == 0 ? r.StandardOutput.Trim() : "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }
    }
}
