namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// Process-wide guard against overlapping Scryfall imports. The handler is
/// scoped, so the lock itself is a singleton. Frontend repeat clicks are also
/// disabled, but the server guard is authoritative.
/// </summary>
public sealed class ScryfallImportLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Tries to acquire the import slot without waiting. Returns false when
    /// another import already holds it.
    /// </summary>
    public bool TryAcquire() => _semaphore.Wait(0);

    public void Release()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Releasing an unheld slot is a no-op for robustness in tests.
        }
    }
}
