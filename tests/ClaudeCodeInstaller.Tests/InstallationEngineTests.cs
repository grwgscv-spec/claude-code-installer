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
        public bool ThrowCancellation { get; set; }
        public int Calls { get; private set; }
        public Task<CcSwitchInstallResult> EnsureCcSwitchAsync(IProgress<string>? log, CancellationToken ct)
        {
            Calls++;
            if (ThrowCancellation) throw new OperationCanceledException();
            if (Throw) throw new InvalidOperationException("cc-switch 下载失败");
            return Task.FromResult(new CcSwitchInstallResult(true, "C:\\cc.exe"));
        }
    }

    private sealed class FakeConfig : IConfigWriter
    {
        public bool Throw { get; set; }
        public Task<ConfigWriteResult> WriteDeepSeekConfigAsync(string userProfileDir, string apiKey,
            string model, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("配置写入失败");
            return Task.FromResult(new ConfigWriteResult(null, true, null, true, "ok"));
        }
    }

    private sealed class FakeVerifyRunner : IProcessRunner
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; } = "";
        public bool ThrowCancellation { get; set; }
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args,
            string? workingDirectory = null, IProgress<string>? output = null, CancellationToken ct = default)
        {
            if (ThrowCancellation) throw new OperationCanceledException();
            return Task.FromResult(new ProcessResult(ExitCode, StdOut, "", false));
        }
    }

    private static InstallationEngine BuildEngine(FakeClaude? claude = null, FakeCcSwitch? cc = null,
        IProcessRunner? verify = null, FakeConfig? config = null) =>
        new(new FailingNode(), claude ?? new FakeClaude(), cc ?? new FakeCcSwitch(),
            config ?? new FakeConfig(), Path.GetTempPath(), verify);

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

    [Fact]
    public async Task CcSwitchCancellation_AbortsInstall()
    {
        var cc = new FakeCcSwitch { ThrowCancellation = true };
        var engine = BuildEngine(cc: cc);
        var success = true;
        string? message = null;
        engine.Finished += (msg, s) => { success = s; message = msg; };

        await engine.RunAsync(new InstallOptions { ApiKey = "sk-x", Model = "m", InstallCcSwitch = true });

        Assert.False(success);
        Assert.Contains("已取消安装", message);
    }

    [Fact]
    public async Task VerifyCancellation_AbortsInstall()
    {
        var verify = new FakeVerifyRunner { ThrowCancellation = true };
        var engine = BuildEngine(verify: verify);
        var success = true;
        string? message = null;
        engine.Finished += (msg, s) => { success = s; message = msg; };

        await engine.RunAsync(new InstallOptions { ApiKey = "sk-x", Model = "m", InstallCcSwitch = false });

        Assert.False(success);
        Assert.Contains("已取消安装", message);
    }

    [Fact]
    public async Task VerifySuccess_IncludesVersionInSummary()
    {
        var verify = new FakeVerifyRunner { ExitCode = 0, StdOut = "2.1.170 (Claude Code)" };
        var engine = BuildEngine(verify: verify);
        string? message = null;
        engine.Finished += (msg, _) => message = msg;

        await engine.RunAsync(new InstallOptions { ApiKey = "sk-x", Model = "m", InstallCcSwitch = false });

        Assert.Contains("2.1.170", message);
    }

    [Fact]
    public async Task CcSwitchDisabled_SkipsStep()
    {
        var engine = BuildEngine();
        var steps = new List<InstallStepId>();
        string? message = null;
        engine.StepStarted += (s, _) => steps.Add(s);
        engine.Finished += (msg, _) => message = msg;

        await engine.RunAsync(new InstallOptions { ApiKey = "sk-x", Model = "m", InstallCcSwitch = false });

        Assert.Equal(new[] { InstallStepId.Node, InstallStepId.Claude, InstallStepId.Config, InstallStepId.Verify }, steps);
        Assert.Contains("已跳过 cc-switch", message);
    }

    [Fact]
    public async Task CcSwitchSucceeds_SummarySaysInstalled()
    {
        var engine = BuildEngine();
        string? message = null;
        engine.Finished += (msg, _) => message = msg;

        await engine.RunAsync(new InstallOptions { ApiKey = "sk-x", Model = "m", InstallCcSwitch = true });

        Assert.Contains("已安装 cc-switch", message);
    }

    [Fact]
    public async Task ConfigFailure_AbortsWithError()
    {
        var config = new FakeConfig { Throw = true };
        var engine = BuildEngine(config: config);
        var success = true;
        string? message = null;
        engine.Finished += (msg, s) => { success = s; message = msg; };

        await engine.RunAsync(new InstallOptions { ApiKey = "sk-x", Model = "m", InstallCcSwitch = false });

        Assert.False(success);
        Assert.Contains("配置写入失败", message);
    }
}
