using System.IO.Compression;
using ClaudeCodeInstaller.Core;
using Xunit;

namespace ClaudeCodeInstaller.Tests;

public class NodeInstallerTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public int WhereExitCode { get; set; } = 1; // `where node` 默认找不到
        public string FoundPath { get; set; } = "C:\\fake\\node\\node.exe";
        public List<(string FileName, IReadOnlyList<string> Args)> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args,
            string? workingDirectory = null, IProgress<string>? output = null, CancellationToken ct = default)
        {
            Calls.Add((fileName, args));
            var isWhere = args.Contains("node") && fileName.EndsWith("where.exe");
            var exit = isWhere ? WhereExitCode : 0;
            return Task.FromResult(new ProcessResult(exit, isWhere && WhereExitCode == 0 ? FoundPath : "", "", false));
        }
    }

    private sealed class FakePathManager : IPathManager
    {
        public string Path { get; private set; } = "";
        public string GetUserPath() => Path;
        public void AppendUserPath(string dir) => Path += (Path == "" ? "" : ";") + dir;
    }

    private static string CreateNodeZip(string dir)
    {
        var zipPath = Path.Combine(dir, "node.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        archive.CreateEntry("node.exe").Open().Close();
        archive.CreateEntry("npm.cmd").Open().Close();
        return zipPath;
    }

    private sealed class FakeDownloader : IDownloadHelper
    {
        private readonly string _zipPath;
        public FakeDownloader(string zipPath) => _zipPath = zipPath;
        public Task<string> DownloadFirstAvailableAsync(IReadOnlyList<string> sources, string destDir,
            string fileName, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            // 与真实 DownloadHelper.DownloadFromAsync 一致：先确保目标目录存在再写文件。
            // 该 tmp 缓存目录在测试间复用且不会自动清理，故用 overwrite 避免残留导致“文件已存在”。
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, fileName);
            File.Copy(_zipPath, dest, overwrite: true);
            return Task.FromResult(dest);
        }
    }

    [Fact]
    public async Task NodeMissing_InstallsPortableNode_AndAppendsPath()
    {
        var profile = Path.Combine(Path.GetTempPath(), "node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var zip = CreateNodeZip(profile);
        try
        {
            var fakeProc = new FakeProcessRunner { WhereExitCode = 1 };
            var fakePath = new FakePathManager();
            var installer = new NodeInstaller(new FakeDownloader(zip), fakeProc, fakePath);

            var result = await installer.EnsureNodeAsync(profile, null, CancellationToken.None);

            Assert.False(result.AlreadyInstalled);
            var nodeDir = Path.Combine(profile, ".nodejs");
            Assert.True(File.Exists(Path.Combine(nodeDir, "node.exe")));
            Assert.True(File.Exists(Path.Combine(nodeDir, "npm.cmd")));
            Assert.Equal(Path.Combine(nodeDir, "npm.cmd"), result.NpmCmd);
            Assert.Contains(nodeDir, fakePath.Path);
        }
        finally { Directory.Delete(profile, true); }
    }

    [Fact]
    public async Task NodeAlreadyInstalled_SkipsInstall()
    {
        var profile = Path.Combine(Path.GetTempPath(), "node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            var sysNode = Path.Combine(profile, "sysnode");
            Directory.CreateDirectory(sysNode);
            await File.WriteAllTextAsync(Path.Combine(sysNode, "node.exe"), "x");
            await File.WriteAllTextAsync(Path.Combine(sysNode, "npm.cmd"), "x");

            var fakeProc = new FakeProcessRunner { WhereExitCode = 0, FoundPath = Path.Combine(sysNode, "node.exe") };
            var installer = new NodeInstaller(new FakeDownloader(profile + ".zip"), fakeProc, new FakePathManager());

            var result = await installer.EnsureNodeAsync(profile, null, CancellationToken.None);

            Assert.True(result.AlreadyInstalled);
            Assert.Equal(Path.Combine(sysNode, "npm.cmd"), result.NpmCmd);
        }
        finally { Directory.Delete(profile, true); }
    }

    [Fact]
    public async Task NodeFoundButNpmMissing_FallsThroughToPortableInstall()
    {
        var profile = Path.Combine(Path.GetTempPath(), "node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var zip = CreateNodeZip(profile);
        try
        {
            // where finds a node.exe in a dir that has no npm.cmd (C:\fake\node doesn't exist)
            var fakeProc = new FakeProcessRunner { WhereExitCode = 0, FoundPath = "C:\\fake\\node\\node.exe" };
            var fakePath = new FakePathManager();
            var installer = new NodeInstaller(new FakeDownloader(zip), fakeProc, fakePath);

            var result = await installer.EnsureNodeAsync(profile, null, CancellationToken.None);

            Assert.False(result.AlreadyInstalled);
            Assert.True(File.Exists(Path.Combine(profile, ".nodejs", "node.exe")));
            Assert.True(File.Exists(Path.Combine(profile, ".nodejs", "npm.cmd")));
        }
        finally { Directory.Delete(profile, true); }
    }

    private static string CreateNodeZipWithInnerFolder(string dir)
    {
        var zipPath = Path.Combine(dir, "node.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var name in new[] { "node-v24.16.0-win-x64/node.exe", "node-v24.16.0-win-x64/npm.cmd" })
        {
            var entry = archive.CreateEntry(name);
            entry.Open().Close();
        }
        return zipPath;
    }

    [Fact]
    public async Task NodeZipWithInnerFolder_IsHoistedIntoNodeDir()
    {
        var profile = Path.Combine(Path.GetTempPath(), "node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var zip = CreateNodeZipWithInnerFolder(profile);
        try
        {
            var installer = new NodeInstaller(new FakeDownloader(zip), new FakeProcessRunner { WhereExitCode = 1 }, new FakePathManager());
            var result = await installer.EnsureNodeAsync(profile, null, CancellationToken.None);
            Assert.False(result.AlreadyInstalled);
            Assert.True(File.Exists(Path.Combine(profile, ".nodejs", "node.exe")));
            Assert.True(File.Exists(Path.Combine(profile, ".nodejs", "npm.cmd")));
            // inner folder must be gone
            Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(profile, ".nodejs")), d => d.Contains("node-v24"));
        }
        finally { Directory.Delete(profile, true); }
    }
}
