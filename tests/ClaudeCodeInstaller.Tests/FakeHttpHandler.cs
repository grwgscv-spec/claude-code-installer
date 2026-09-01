using System.Net;

namespace ClaudeCodeInstaller.Tests;

public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;
    public FakeHttpHandler(params HttpResponseMessage[] responses) =>
        _responses = new Queue<HttpResponseMessage>(responses);
    public List<string> RequestedUrls { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestedUrls.Add(request.RequestUri!.ToString());
        var next = _responses.Count > 0 ? _responses.Dequeue() : NotFound();
        return Task.FromResult(next);
    }

    public static HttpResponseMessage Ok(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);
}
