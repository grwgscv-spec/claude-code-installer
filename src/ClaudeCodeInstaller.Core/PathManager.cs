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
    }
}
