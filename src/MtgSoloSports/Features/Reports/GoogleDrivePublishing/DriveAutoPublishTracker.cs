using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Single-process automatic-publish coordination. The in-memory queue only
/// signals the background worker; the durable requested checkpoint lives in
/// the app-level <see cref="DrivePublishingStore"/> destination document so
/// burst requests coalesce to the latest stable state and restart recovery
/// can reconcile an unpublished committed checkpoint. Publishing is never
/// part of sporting correctness.
/// </summary>
public sealed class DriveAutoPublishTracker
{
    private readonly ConcurrentDictionary<Guid, byte> _active = new();
    private readonly Channel<Guid> _signals = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    public bool IsPublishing(Guid saveId) => _active.ContainsKey(saveId);

    internal void SetPublishing(Guid saveId) => _active[saveId] = 1;

    internal void ClearPublishing(Guid saveId) => _active.TryRemove(saveId, out _);

    public void Signal(Guid saveId)
    {
        _signals.Writer.TryWrite(saveId);
    }

    internal ChannelReader<Guid> Reader => _signals.Reader;
}
