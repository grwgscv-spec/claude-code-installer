using ClaudeCodeInstaller.Core;
using Xunit;

namespace ClaudeCodeInstaller.Tests;

public class InstallationEngineTests
{
    private sealed class FailingNode : INodeInstaller
    {
        public Task<NodeInstallResult> EnsureNodeAsync(string userProfileDir, IProgress<string>? log, CancellationToken ct) =>
            Task.FromResult(new NodeInstallResult(true, "C:\\nodejs", "C:\\nodejs\\npm.cmd"));
    }

    private sealed class FakeClaude : IClaudeInstaller
    {
        public bool Throw { get; set; }
        public Task<ClaudeInstallResult> EnsureClaudeAsync(string npmCmd, IProgress<string>? log, CancellationToken ct)
        {
            if (Throw) throw new InvalidOperationException("npm 网络失败");
            return Task.FromResult(new ClaudeInstallResult(false, "C:\\npm\\claude.cmd"));
        }
    }

    private sealed class FakeCcSwitch : ICcSwitchInstaller
    {
        public bool Throw { get; set; }
        public int Calls { get; private set; }
        public Task<CcSwitchInstallResult> EnsureCcSwitchAsync(IProgress<string>? log, CancellationToken ct)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("cc-switch 下载失败");
            return Task.FromResult(new CcSwitchInstallResult(true, "C:\\cc.exe"));
        }
    }

    private sealed class FakeConfig : IConfigWriter
    {
        public Task<ConfigWriteResult> WriteDeepSeekConfigAsync(string userProfileDir, string apiKey,
            string model, CancellationToken ct = default) =>
            Task.FromResult(new ConfigWriteResult(null, true, null, true, "ok"));
    }

    private static InstallationEngine BuildEngine(FakeClaude? claude = null, FakeCcSwitch? cc = null) =>
        new(new FailingNode(), claude ?? new FakeClaude(), cc ?? new FakeCcSwitch(),
            new FakeConfig(), Path.GetTempPath());

    [Fact]
    public async Task AllStepsRunInOrder_Success()
    {
        var engine = BuildEngine();
        var steps = new List<InstallStepId>();
        engine.StepStarted += (s, _) => steps.Add(s);
        engine.Finished += (_, success) => Assert.True(success);

        await engine.RunAsync(new InstallOptions { ApiKey = "sk-x", Model = "deepseek-v4-flash", InstallCcSwitch = true });

        Assert.Equal(
            new[] { InstallStepId.Node, InstallStepId.Claude, InstallStepId.CcSwitch, InstallStepId.Config, InstallStepId.Verify },
            steps);
    }

    [Fact]
    public async Task CcSwitchFailure_DoesNotBlockCore()
    {
        var cc = new FakeCcSwitch { Throw = true };
        var engine = BuildEngine(cc: cc);
        var success = false;
        engine.Finished += (_, s) => success = s;

        await engine.RunAsync(new InstallOptions { ApiKey = "sk-x", Model = "m", InstallCcSwitch = true });

        Assert.True(success); // claude + 配置仍成功
        Assert.Equal(1, cc.Calls);
    }

    [Fact]
    public async Task ClaudeFailure_AbortsWithError()
    {
        var claude = new FakeClaude { Throw = true };
        var engine = BuildEngine(claude: claude);
        var success = true;
        string? message = null;
        engine.Finished += (msg, s) => { success = s; message = msg; };

        await engine.RunAsync(new InstallOptions { ApiKey = "sk-x", Model = "m", InstallCcSwitch = false });

        Assert.False(success);
        Assert.Contains("npm 网络失败", message);
    }
}
