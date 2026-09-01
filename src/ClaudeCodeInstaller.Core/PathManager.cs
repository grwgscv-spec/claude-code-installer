namespace ClaudeCodeInstaller.Core;

public interface IPathManager
{
    string GetUserPath();
    void AppendUserPath(string dir);
}

public sealed class PathManager : IPathManager
{
    public string GetUserPath() =>
        Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";

    public void AppendUserPath(string dir)
    {
        var current = GetUserPath();
        var normalized = dir.TrimEnd(Path.DirectorySeparatorChar);
        var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(e => e.TrimEnd(Path.DirectorySeparatorChar).Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return;
        var updated = string.IsNullOrEmpty(current) ? normalized : current.TrimEnd(';') + ";" + normalized;
        Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);

        // 同步更新当前进程 PATH，让本次运行内派生的进程（where/npm/cmd）能看到新目录
        var processPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Process) ?? "";
        if (!processPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(e => e.TrimEnd(Path.DirectorySeparatorChar).Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            var pUpdated = string.IsNullOrEmpty(processPath) ? normalized : processPath.TrimEnd(';') + ";" + normalized;
            Environment.SetEnvironmentVariable("Path", pUpdated, EnvironmentVariableTarget.Process);
        }
    }
}
