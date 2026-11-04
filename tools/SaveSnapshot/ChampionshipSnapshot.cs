namespace MtgSoloSports.SaveSnapshot;

public sealed record ChampionshipSnapshot(
    bool Available,
    string? UnavailableReason,
    int CompletedStages,
    bool IsFinal,
    string Checksum,
    IReadOnlyList<ChampionshipRow> Standings,
    string EvidenceRef);
