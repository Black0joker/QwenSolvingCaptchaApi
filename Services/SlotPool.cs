using System.Threading.Channels;
using QwenSolvingApi.Models;

namespace QwenSolvingApi.Services;

/// <summary>
/// Channel-based concurrency gate that manages a dynamic pool of URL slots.
/// Each registered captcha URL contributes <see cref="SlotsPerUrl"/> slots.
/// Requests acquire a slot before proceeding; if all slots are in use,
/// the caller awaits asynchronously until one is released.
/// The pool grows at runtime when new URLs are registered (no restart needed).
/// </summary>
public sealed class SlotPool : IDisposable
{
    private readonly Channel<SlotInfo> _channel;
    private readonly int _slotsPerUrl;
    private int _nextSlotId = -1;

    public SlotPool(int slotsPerUrl)
    {
        if (slotsPerUrl <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotsPerUrl), "Slots per URL must be greater than zero.");

        _slotsPerUrl = slotsPerUrl;

        // Unbounded: the number of slots in the channel is determined by the
        // registered URLs, not by the channel capacity. When every slot is
        // checked out the channel is empty and AcquireAsync awaits asynchronously.
        _channel = Channel.CreateUnbounded<SlotInfo>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Number of concurrent slots created for each registered captcha URL.
    /// </summary>
    public int SlotsPerUrl => _slotsPerUrl;

    /// <summary>
    /// Total number of slots created so far (across all registered URLs).
    /// </summary>
    public int TotalSlotCount => Volatile.Read(ref _nextSlotId) + 1;

    /// <summary>
    /// Registers a captcha URL and immediately enqueues <see cref="SlotsPerUrl"/>
    /// new free slots pointing at it. Safe to call at runtime — newly registered
    /// slots become available to waiting requests without a restart.
    /// </summary>
    public IReadOnlyList<SlotInfo> RegisterUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var registered = new List<SlotInfo>(_slotsPerUrl);

        for (int i = 0; i < _slotsPerUrl; i++)
        {
            var slot = new SlotInfo
            {
                SlotId = Interlocked.Increment(ref _nextSlotId),
                Url = url
            };

            _channel.Writer.TryWrite(slot);
            registered.Add(slot);
        }

        return registered;
    }

    /// <summary>
    /// Acquires a slot from the pool. Awaits asynchronously if all slots are busy.
    /// </summary>
    public async Task<SlotInfo> AcquireAsync(CancellationToken ct = default)
    {
        return await _channel.Reader.ReadAsync(ct);
    }

    /// <summary>
    /// Releases a slot back to the pool, making it available for the next waiting request.
    /// </summary>
    public async Task ReleaseAsync(SlotInfo slot, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(slot, ct);
    }

    /// <summary>
    /// Attempts to dequeue a slot without blocking.
    /// Returns true if a slot was dequeued, false if the pool is empty.
    /// </summary>
    public bool TryDequeue(out SlotInfo? slot)
    {
        return _channel.Reader.TryRead(out slot);
    }

    /// <summary>
    /// Number of currently available (free) slots.
    /// </summary>
    public int AvailableCount => _channel.Reader.Count;

    public void Dispose()
    {
        _channel.Writer.TryComplete();
    }
}
