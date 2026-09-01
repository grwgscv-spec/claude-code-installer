using System.Diagnostics;

namespace ClaudeCodeInstaller.Core;

public record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool Cancelled);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args,
        string? workingDirectory = null, IProgress<string>? output = null, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args,
        string? workingDirectory = null, IProgress<string>? output = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        var stdoutTask = ReadLinesAsync(process.StandardOutput, output, ct);
        var stderrTask = ReadLinesAsync(process.StandardError, output, ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, stdout, stderr, ct.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
            catch { /* reads cancelled; ignore */ }
            throw;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { /* already exited or access denied — best effort */ }
    }

    private static async Task<string> ReadLinesAsync(StreamReader reader, IProgress<string>? output, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            sb.AppendLine(line);
            output?.Report(line);
        }
        return sb.ToString();
    }
}
