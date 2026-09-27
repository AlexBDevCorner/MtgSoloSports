namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Filesystem layout for technical recovery checkpoints. Checkpoints live
/// under <c>{savesRoot}/checkpoints/{saveId:N}/{checkpointId:N}.db</c> with a
/// same-named <c>.json</c> sidecar, so listing a save's <c>*.db</c> files never
/// mistakes a checkpoint for a live universe. All paths derive from typed
/// <see cref="Guid"/> values, which makes path traversal impossible.
/// This is save-scoped file management, not a generic repository.
/// </summary>
public static class SaveCheckpointFiles
{
    /// <summary>
    /// Maximum retained checkpoints per save. Older verified checkpoints are
    /// pruned on creation so recovery storage stays bounded.
    /// </summary>
    public const int MaxCheckpointsPerSave = 10;

    public const int MaxReasonLength = 200;

    public static string GetCheckpointsRoot(string savesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savesRoot);
        return Path.Combine(Path.GetFullPath(savesRoot), "checkpoints");
    }

    public static string GetSaveCheckpointDirectory(string savesRoot, Guid saveId)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        string rootFull = Path.GetFullPath(savesRoot);
        string combined = Path.GetFullPath(Path.Combine(GetCheckpointsRoot(rootFull), $"{saveId:N}"));
        SaveFileNaming.EnsureInsideRoot(Path.GetFullPath(GetCheckpointsRoot(rootFull)), combined);
        return combined;
    }

    public static string GetCheckpointDatabasePath(string saveCheckpointDirectory, Guid checkpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveCheckpointDirectory);
        if (checkpointId == Guid.Empty)
        {
            throw new ArgumentException("Checkpoint id must not be empty.", nameof(checkpointId));
        }

        string directoryFull = Path.GetFullPath(saveCheckpointDirectory);
        string combined = Path.GetFullPath(Path.Combine(directoryFull, $"{checkpointId:N}.db"));
        SaveFileNaming.EnsureInsideRoot(directoryFull, combined);
        return combined;
    }

    public static string GetCheckpointSidecarPath(string saveCheckpointDirectory, Guid checkpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveCheckpointDirectory);
        if (checkpointId == Guid.Empty)
        {
            throw new ArgumentException("Checkpoint id must not be empty.", nameof(checkpointId));
        }

        string directoryFull = Path.GetFullPath(saveCheckpointDirectory);
        string combined = Path.GetFullPath(Path.Combine(directoryFull, $"{checkpointId:N}.json"));
        SaveFileNaming.EnsureInsideRoot(directoryFull, combined);
        return combined;
    }

    public static string NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "manual";
        }

        string clean = reason.Trim();
        return clean.Length > MaxReasonLength ? clean[..MaxReasonLength] : clean;
    }

    /// <summary>
    /// Prunes verified checkpoint pairs beyond the retention bound, oldest
    /// first. Best effort: pruning failures never mask the operation that
    /// created the newest checkpoint.
    /// </summary>
    public static void PruneOldest(string saveCheckpointDirectory, int keep = MaxCheckpointsPerSave)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveCheckpointDirectory);
        if (!Directory.Exists(saveCheckpointDirectory))
        {
            return;
        }

        List<(DateTimeOffset CreatedUtc, Guid CheckpointId)> ordered = [];
        foreach (string sidecarPath in Directory.GetFiles(saveCheckpointDirectory, "*.json"))
        {
            try
            {
                SaveCheckpointSidecar sidecar = SaveCheckpointSidecar.FromJson(
                    File.ReadAllText(sidecarPath));
                if (File.Exists(GetCheckpointDatabasePath(saveCheckpointDirectory, sidecar.CheckpointId)))
                {
                    ordered.Add((sidecar.CreatedUtc, sidecar.CheckpointId));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
            }
        }

        ordered.Sort(static (left, right) =>
        {
            int created = left.CreatedUtc.CompareTo(right.CreatedUtc);
            return created != 0 ? created : left.CheckpointId.CompareTo(right.CheckpointId);
        });

        while (ordered.Count > keep)
        {
            (DateTimeOffset _, Guid oldest) = ordered[0];
            ordered.RemoveAt(0);
            foreach (string candidate in new[]
            {
                GetCheckpointDatabasePath(saveCheckpointDirectory, oldest),
                GetCheckpointSidecarPath(saveCheckpointDirectory, oldest),
            })
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        File.Delete(candidate);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
