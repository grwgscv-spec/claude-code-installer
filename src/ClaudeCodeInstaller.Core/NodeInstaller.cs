using System.IO.Compression;

namespace ClaudeCodeInstaller.Core;

public record NodeInstallResult(bool AlreadyInstalled, string NodeDir, string NpmCmd);

public interface INodeInstaller
{
    Task<NodeInstallResult> EnsureNodeAsync(string userProfileDir, IProgress<string>? log, CancellationToken ct);
}

public sealed class NodeInstaller : INodeInstaller
{
    private readonly IDownloadHelper _downloader;
    private readonly IProcessRunner _runner;
    private readonly IPathManager _pathManager;

    public NodeInstaller(IDownloadHelper downloader, IProcessRunner runner, IPathManager pathManager)
    {
        _downloader = downloader;
        _runner = runner;
        _pathManager = pathManager;
    }

    public async Task<NodeInstallResult> EnsureNodeAsync(string userProfileDir, IProgress<string>? log, CancellationToken ct)
    {
        var nodeDir = Path.Combine(userProfileDir, ".nodejs");
        var ownNode = Path.Combine(nodeDir, "node.exe");
        if (File.Exists(ownNode) && File.Exists(Path.Combine(nodeDir, "npm.cmd")))
        {
            log?.Report("检测到已安装的便携 Node，跳过安装。");
            return new NodeInstallResult(true, nodeDir, Path.Combine(nodeDir, "npm.cmd"));
        }

        // 检查系统其它位置的 node（含 `where node`）
        var where = await _runner.RunAsync("where.exe", new[] { "node" }, null, null, ct);
        if (where.ExitCode == 0 && !string.IsNullOrWhiteSpace(where.StandardOutput))
        {
            var found = where.StandardOutput.Trim().Split('\n')[0].Trim();
            var foundDir = Path.GetDirectoryName(found)!;
            if (File.Exists(Path.Combine(foundDir, "npm.cmd")))
            {
                log?.Report($"检测到已有 Node: {found}，跳过安装。");
                return new NodeInstallResult(true, foundDir, Path.Combine(foundDir, "npm.cmd"));
            }

            log?.Report($"检测到 {found} 但缺少 npm.cmd，将重新下载便携 Node。");
        }

        log?.Report($"未检测到 Node，正在下载 Node {VersionInfo.NodeVersion}…");
        var tmp = Path.Combine(Path.GetTempPath(), "claude-code-installer");
        var zipPath = await _downloader.DownloadFirstAvailableAsync(
            VersionInfo.NodeZipSources, tmp, VersionInfo.NodeZipFileName, null, ct);

        log?.Report("正在解压 Node…");
        Directory.CreateDirectory(nodeDir);
        ZipFile.ExtractToDirectory(zipPath, nodeDir, overwriteFiles: true);

        // zip 顶层是 "node-vXX-win-x64/"，把内容上移一层
        var inner = Directory.GetDirectories(nodeDir).SingleOrDefault();
        if (inner is not null)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(inner))
            {
                var target = Path.Combine(nodeDir, Path.GetFileName(entry));
                if (Directory.Exists(entry) && !Directory.Exists(target))
                    Directory.Move(entry, target);
                else if (File.Exists(entry) && !File.Exists(target))
                    File.Move(entry, target);
            }
            Directory.Delete(inner, recursive: true);
        }

        ct.ThrowIfCancellationRequested();
        _pathManager.AppendUserPath(nodeDir);
        log?.Report("Node 安装完成，已加入用户 PATH。");
        return new NodeInstallResult(false, nodeDir, Path.Combine(nodeDir, "npm.cmd"));
    }
}
