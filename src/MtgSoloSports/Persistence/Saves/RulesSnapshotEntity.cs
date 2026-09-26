namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Single-row table (Id always 1) holding the immutable Rules v1 snapshot.
/// The row is written once at save creation and never updated.
/// </summary>
public sealed class RulesSnapshotEntity
{
    public int Id { get; set; }

    public int RulesVersion { get; set; }

    public string RulesJson { get; set; } = string.Empty;
}
