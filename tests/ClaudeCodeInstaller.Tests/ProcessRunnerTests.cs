using ClaudeCodeInstaller.Core;
using Xunit;

namespace ClaudeCodeInstaller.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task ExitCodeZero_ReturnsSuccess()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("cmd.exe", new[] { "/c", "exit 0" });
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task ExitCodeNonZero_IsPropagated()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("cmd.exe", new[] { "/c", "exit 3" });
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task CapturesStandardOutput()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("cmd.exe", new[] { "/c", "echo hello" });
        Assert.Contains("hello", result.StandardOutput);
    }

    [Fact]
    public void AppendUserPath_AddsDirOnce()
    {
        var manager = new PathManager();
        var before = manager.GetUserPath();
        try
        {
            manager.AppendUserPath("C:\\fake\\node");
            manager.AppendUserPath("C:\\fake\\node");
            var after = manager.GetUserPath();
            Assert.Contains("C:\\fake\\node", after);
            Assert.Equal(before + ";C:\\fake\\node", after);
        }
        finally
        {
            // 还原测试污染的用户 PATH
            Environment.SetEnvironmentVariable("Path", before, EnvironmentVariableTarget.User);
        }
    }

    [Fact]
    public async Task OutputProgress_ReceivesStdoutAndStderrLines()
    {
        var runner = new ProcessRunner();
        var lines = new List<string>();
        await runner.RunAsync("cmd.exe", new[] { "/c", "echo out1 & echo err1 1>&2 & echo out2" }, null,
            new Progress<string>(lines.Add), CancellationToken.None);
        var trimmed = lines.Select(l => l.Trim()).ToList();
        Assert.Contains("out1", trimmed);
        Assert.Contains("err1", trimmed);
        Assert.Contains("out2", trimmed);
    }

    [Fact]
    public async Task Cancellation_ThrowsAndKillsChild()
    {
        var runner = new ProcessRunner();
        using var cts = new CancellationTokenSource();
        var task = runner.RunAsync("cmd.exe", new[] { "/c", "ping -t 127.0.0.1" }, null, null, cts.Token);
        await Task.Delay(300);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
