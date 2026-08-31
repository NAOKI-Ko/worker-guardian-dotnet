using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace WorkerGuardian;

public sealed class EventPipeline<TEvent> : IAsyncDisposable
    where TEvent : GuardianEvent
{
    private readonly Channel<TEvent> channel;
    private bool disposed;

    public EventPipeline(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        channel = Channel.CreateBounded<TEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false,
        });
    }

    public ValueTask PublishAsync(TEvent item, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return channel.Writer.WriteAsync(item, cancellationToken);
    }

    public async IAsyncEnumerable<TEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            channel.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }
}

