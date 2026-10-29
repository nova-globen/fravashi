using System.Collections.Concurrent;

namespace Fravashi.Infrastructure.Tests.Bridge;

/// <summary>
/// The real HTTP stack with the event stream made breakable: <see cref="Sever"/> ends every open stream with the IO
/// error a dropped connection gives, and refuses new ones until <see cref="Restore"/>, which is what a bridge that is
/// briefly away looks like from the studio. The bridge itself is real, so what it reports while nobody listens is
/// what it really reports: nothing. Shared by the agent copy's tests (backlog 253) and the Code page's (backlog 257).
/// </summary>
internal sealed class SeverableEvents() : DelegatingHandler(new SocketsHttpHandler())
{
    private readonly ConcurrentBag<SeverableStream> _streams = [];
    private volatile bool _down;
    private int _connections;

    public int Connections => Volatile.Read(ref _connections);

    public void Sever()
    {
        _down = true;
        foreach (var stream in _streams)
        {
            stream.Sever();
        }
    }

    public void Restore() => _down = false;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isEvents = request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal);
        if (isEvents && _down)
        {
            throw new HttpRequestException("the bridge is away (test)");
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (isEvents && response.IsSuccessStatusCode)
        {
            var stream = new SeverableStream(await response.Content.ReadAsStreamAsync(cancellationToken));
            _streams.Add(stream);
            var content = new StreamContent(stream);
            foreach (var header in response.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = content;
            Interlocked.Increment(ref _connections);
        }

        return response;
    }

    private sealed class SeverableStream(Stream inner) : Stream
    {
        private readonly CancellationTokenSource _severed = new();

        public void Sever() => _severed.Cancel();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_severed.IsCancellationRequested)
            {
                throw new IOException("the connection dropped (test)");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _severed.Token);
            try
            {
                return await inner.ReadAsync(buffer, linked.Token);
            }
            catch (OperationCanceledException) when (_severed.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new IOException("the connection dropped (test)");
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                _severed.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
