namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Safe file naming for per-save SQLite databases.
/// Save files are always <c>{saveId:N}.db</c> directly inside the saves root,
/// which makes path traversal impossible through the typed (<see cref="Guid"/>) APIs.
/// </summary>
public static class SaveFileNaming
{
    public const string SaveFileExtension = ".db";

    public static string GetSaveFileName(Guid saveId) => $"{saveId:N}{SaveFileExtension}";

    /// <summary>
    /// Resolves the absolute save file path and verifies it stays inside the saves root.
    /// </summary>
    public static string GetSaveFilePath(string savesRoot, Guid saveId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savesRoot);
        string rootFull = Path.GetFullPath(savesRoot);
        string combined = Path.GetFullPath(Path.Combine(rootFull, GetSaveFileName(saveId)));
        EnsureInsideRoot(rootFull, combined);
        return combined;
    }

    /// <summary>
    /// Validates a raw file name coming from directory enumeration.
    /// Returns false for companion files (-wal/-shm/-journal) and foreign files.
    /// </summary>
    public static bool IsValidSaveFileName(string fileName, out Guid saveId)
    {
        saveId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (!fileName.EndsWith(SaveFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = fileName[..^SaveFileExtension.Length];
        return Guid.TryParseExact(stem, "N", out saveId);
    }

    /// <summary>
    /// Resolves a raw file name against the saves root and rejects anything
    /// outside the root or without the expected save-file shape.
    /// Used to prove deletion can never silently remove arbitrary files.
    /// </summary>
    public static string ResolveAndValidate(string savesRoot, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"Invalid save file name '{fileName}'.", nameof(fileName));
        }

        if (!IsValidSaveFileName(fileName, out _))
        {
            throw new ArgumentException($"Invalid save file name '{fileName}'.", nameof(fileName));
        }

        string rootFull = Path.GetFullPath(savesRoot);
        string combined = Path.GetFullPath(Path.Combine(rootFull, fileName));
        EnsureInsideRoot(rootFull, combined);
        return combined;
    }

    internal static void EnsureInsideRoot(string rootFullPath, string candidateFullPath)
    {
        string prefix = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootFullPath
            : rootFullPath + Path.DirectorySeparatorChar;
        if (!candidateFullPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved save path escapes the saves root.");
        }
    }
}
