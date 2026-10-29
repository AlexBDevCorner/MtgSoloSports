using System.Collections.Concurrent;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Per-save async gates serializing Drive destination mutations and publish
/// whole-operations. The store's SemaphoreSlim guards single JSON files only;
/// list-then-create Drive sequences plus the read-modify-write of the
/// destination document must not interleave between Publish and destination
/// PUT, otherwise an in-flight publish would overwrite a concurrent PUT
/// (lost update) or twin Creates would duplicate hierarchy.
/// </summary>
internal static class DriveSaveGates
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    public static SemaphoreSlim ForSave(Guid saveId) =>
        Gates.GetOrAdd(saveId, static _ => new SemaphoreSlim(1, 1));
}
