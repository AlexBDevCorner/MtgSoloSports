namespace MtgSoloSports.SaveSnapshot;

public sealed record EvidenceSource(
    Guid SaveId,
    string SaveName,
    string SnapshotPath,
    DateTimeOffset ExportedUtc,
    DateTimeOffset AcquiredUtc,
    string DatabaseSha256,
    int SchemaVersion,
    int RulesVersion,
    string RngAlgorithm,
    int RngVersion,
    int CurrentSeason,
    string Phase,
    int SavedRulesVersion,
    int StagesPerSeason,
    int RoundsPerStage,
    int LeagueSize);
