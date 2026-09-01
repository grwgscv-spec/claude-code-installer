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
}
