using System.Net;
using System.Text;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.UnitTests.Support;

/// <summary>可在首段和结束帧之间暂停的真实 HTTP 流替身，不访问模型网络。</summary>
internal sealed class StreamingTextTransport(string first, string last = "") : HttpMessageHandler, IHttpClientFactory
{
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls { get; private set; }
    public string? Payload { get; private set; }
    public string? ClientName { get; private set; }
    public Uri? Url { get; private set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string ContentType { get; set; } = "text/event-stream";
    public string Last { get; set; } = last;
    public HttpClient CreateClient(string name) { ClientName = name; return new HttpClient(this, disposeHandler: false); }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++; Url = request.RequestUri; Payload = await request.Content!.ReadAsStringAsync(ct);
        var response = new HttpResponseMessage(Status) { Content = new StreamContent(new GatedStream(first, Last, Gate.Task)) };
        response.Content.Headers.ContentType = new(ContentType);
        return response;
    }
    public static TextGenerationRequest Request(ProviderType protocol, Func<string, CancellationToken, Task> receive) => new(
        new AiProvider(ProviderId.New(), null, "Synthetic", null, null, protocol, null, "https://example.test", true, null, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
        "synthetic-key", "test-model", "test prompt", null, [], 32768, 0)
    { HttpClientName = "workflow-model", OnTextDelta = receive };

    private sealed class GatedStream(string first, string last, Task gate) : Stream
    {
        private readonly MemoryStream _first = new(Encoding.UTF8.GetBytes(first));
        private readonly MemoryStream _last = new(Encoding.UTF8.GetBytes(last));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_first.Position < _first.Length) return await _first.ReadAsync(buffer, ct);
            await gate.WaitAsync(ct);
            return await _last.ReadAsync(buffer, ct);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _first.Dispose(); _last.Dispose(); } base.Dispose(disposing); }
    }
}
