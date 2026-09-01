using ClaudeCodeInstaller.Core;
using Xunit;

namespace ClaudeCodeInstaller.Tests;

public class ClaudeInstallerTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public bool ClaudeExists { get; set; }
        public string FoundPath { get; set; } = "C:\\Users\\u\\AppData\\Roaming\\npm\\claude.cmd";
        public bool ReportFoundAfterFirstWhere { get; set; }
        public Queue<int> NpmExitCodes { get; } = new();
        public List<(string FileName, IReadOnlyList<string> Args)> Calls { get; } = new();

        private int _whereCalls;

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args,
            string? workingDirectory = null, IProgress<string>? output = null, CancellationToken ct = default)
        {
            Calls.Add((fileName, args));
            if (args.Contains("claude") && fileName.EndsWith("where.exe"))
            {
                _whereCalls++;
                var found = ClaudeExists
                    || (ReportFoundAfterFirstWhere && _whereCalls >= 2);
                return Task.FromResult(new ProcessResult(found ? 0 : 1, found ? FoundPath : "", "", false));
            }
            var exit = NpmExitCodes.Count > 0 ? NpmExitCodes.Dequeue() : 0;
            return Task.FromResult(new ProcessResult(exit, "", exit == 0 ? "" : "npm error", false));
        }
    }

    [Fact]
    public async Task InstallsViaNpmMirrorRegistry()
    {
        var fake = new FakeRunner();
        var installer = new ClaudeInstaller(fake);

        var result = await installer.EnsureClaudeAsync("C:\\nodejs\\npm.cmd", null, CancellationToken.None);

        var npmCall = fake.Calls.Single(c => c.FileName == "C:\\nodejs\\npm.cmd");
        Assert.Contains("install", npmCall.Args);
        Assert.Contains("@anthropic-ai/claude-code", npmCall.Args);
        Assert.Contains("--registry=https://registry.npmmirror.com", npmCall.Args);
        Assert.False(result.AlreadyInstalled);
    }

    [Fact]
    public async Task FirstRegistryFails_RetriesWithFallback()
    {
        var fake = new FakeRunner();
        fake.NpmExitCodes.Enqueue(1); // 镜像 registry 失败
        fake.NpmExitCodes.Enqueue(0); // 官方 registry 成功
        var installer = new ClaudeInstaller(fake);

        var result = await installer.EnsureClaudeAsync("C:\\nodejs\\npm.cmd", null, CancellationToken.None);

        var npmCalls = fake.Calls.Where(c => c.FileName == "C:\\nodejs\\npm.cmd").ToList();
        Assert.Equal(2, npmCalls.Count);
        Assert.Contains("--registry=https://registry.npmjs.org", npmCalls[1].Args);
    }

    [Fact]
    public async Task ClaudeAlreadyInstalled_IsUpgraded()
    {
        var fake = new FakeRunner { ClaudeExists = true };
        var installer = new ClaudeInstaller(fake);

        var result = await installer.EnsureClaudeAsync("C:\\nodejs\\npm.cmd", null, CancellationToken.None);

        Assert.True(result.AlreadyInstalled);
        Assert.Equal(fake.FoundPath, result.ClaudeCmd);
        Assert.Single(fake.Calls.Where(c => c.FileName == "C:\\nodejs\\npm.cmd"));
    }

    [Fact]
    public async Task AlreadyInstalledAtNonStandardPath_ReturnsWhereFoundPath()
    {
        var fake = new FakeRunner { ClaudeExists = true, FoundPath = "C:\\Users\\x\\.nodejs\\claude.cmd" };
        var installer = new ClaudeInstaller(fake);
        var result = await installer.EnsureClaudeAsync("C:\\nodejs\\npm.cmd", null, CancellationToken.None);
        Assert.True(result.AlreadyInstalled);
        Assert.Equal("C:\\Users\\x\\.nodejs\\claude.cmd", result.ClaudeCmd);
    }

    [Fact]
    public async Task FreshInstall_ReResolvesClaudeCmdAfterNpm()
    {
        // ClaudeExists is false at first (pre-install `where` finds nothing),
        // but the fake returns FoundPath on the post-install re-query.
        var fake = new FakeRunner { ClaudeExists = false, FoundPath = "C:\\Users\\x\\.nodejs\\claude.cmd" };
        // Make the fake's SECOND where call report found even though ClaudeExists is false:
        fake.ReportFoundAfterFirstWhere = true;
        var installer = new ClaudeInstaller(fake);
        var result = await installer.EnsureClaudeAsync("C:\\nodejs\\npm.cmd", null, CancellationToken.None);
        Assert.False(result.AlreadyInstalled);
        Assert.Equal("C:\\Users\\x\\.nodejs\\claude.cmd", result.ClaudeCmd);
    }
}
