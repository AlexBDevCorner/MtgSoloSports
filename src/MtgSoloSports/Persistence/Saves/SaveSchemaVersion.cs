namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Current save-file schema version. Persisted in every save database so
/// future schema migrations can be distinguished from game-rule migrations.
/// </summary>
public static class SaveSchemaVersion
{
    public const int Current = 1;
}
