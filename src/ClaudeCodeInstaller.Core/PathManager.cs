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
        var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Contains(dir, StringComparer.OrdinalIgnoreCase)) return;
        var updated = string.IsNullOrEmpty(current) ? dir : current + ";" + dir;
        Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
    }
}
