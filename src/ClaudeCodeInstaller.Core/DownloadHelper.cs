using System.Net;

namespace ClaudeCodeInstaller.Core;

public class DownloadException : Exception
{
    public DownloadException(string message) : base(message) { }
}

public record DownloadProgress(long BytesReceived, long? TotalBytes, int Percent, string Source);

public interface IDownloadHelper
{
    Task<string> DownloadFirstAvailableAsync(IReadOnlyList<string> sources, string destDir,
        string fileName, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
}

public sealed class DownloadHelper : IDownloadHelper
{
    private const int AttemptsPerSource = 2;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _client;

    public DownloadHelper(HttpMessageHandler? handler = null)
    {
        var http = handler ?? new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _client = new HttpClient(http) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeCodeInstaller/1.0");
    }

    public async Task<string> DownloadFirstAvailableAsync(IReadOnlyList<string> sources, string destDir,
        string fileName, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        foreach (var source in sources)
        {
            for (var attempt = 1; attempt <= AttemptsPerSource; attempt++)
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(AttemptTimeout);
                try
                {
                    return await DownloadFromAsync(source, destDir, fileName, progress, attemptCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 尝试级超时（非用户取消）→ 记为该源失败并重试
                    errors.Add($"{source} 超时 (尝试 {attempt})");
                    progress?.Report(new DownloadProgress(0, null, 0, $"{source} 超时，重试…"));
                }
                catch (HttpRequestException ex)
                {
                    // A non-2xx HTTP status is a deterministic failure for this
                    // source: retrying would produce the same 4xx/5xx response. So
                    // bail out of this source's retry loop and try the next source.
                    errors.Add($"{source}: {ex.Message}");
                    progress?.Report(new DownloadProgress(0, null, 0, $"{source} 失败: {ex.Message}"));
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{source} (尝试 {attempt}): {ex.Message}");
                    progress?.Report(new DownloadProgress(0, null, 0, $"{source} 失败: {ex.Message}"));
                }
            }
        }
        throw new DownloadException("所有下载源均失败。\n" + string.Join("\n", errors));
    }

    private async Task<string> DownloadFromAsync(string url, string destDir, string fileName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var partPath = Path.Combine(destDir, fileName + ".part");
        var finalPath = Path.Combine(destDir, fileName);

        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(partPath);
        var buffer = new byte[81920];
        long received = 0;
        var lastReported = -1;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            if (total is { } t and > 0)
            {
                var pct = (int)(received * 100 / t);
                if (pct != lastReported)
                {
                    lastReported = pct;
                    progress?.Report(new DownloadProgress(received, t, pct, url));
                }
            }
            else
            {
                progress?.Report(new DownloadProgress(received, null, -1, url));
            }
        }
        // Flush and release the temp file handle before renaming; on Windows the
        // open FileStream would otherwise lock the file and make File.Move fail.
        await target.FlushAsync(ct);
        await target.DisposeAsync();
        File.Move(partPath, finalPath, overwrite: true);
        progress?.Report(new DownloadProgress(received, total, 100, url));
        return finalPath;
    }
}
