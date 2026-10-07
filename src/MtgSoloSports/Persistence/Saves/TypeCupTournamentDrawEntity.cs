namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One persisted Type Cup qualification-draw row per selected team per completed
/// even source season (MSS-061). The draw happens after squad allocation and
/// before qualification rounds: squad membership never changes with group
/// assignment. For fields of at most 32 teams there is no qualification stage
/// and no draw rows exist (direct Final). For larger fields every selected team
/// appears exactly once in one balanced qualification group of at most 32 teams.
/// Group assignment is random via the versioned save RNG (never strength-seeded)
/// and persisted before competition so reload/history never redraws it.
/// <see cref="RngBeforeState"/>/<see cref="RngAfterState"/> plus
/// <see cref="DrawChecksum"/> follow existing provenance conventions: the RNG
/// commit and the draw share one transaction.
/// Field metadata (<see cref="FieldTeamCount"/>, <see cref="QualificationGroupCount"/>,
/// <see cref="GroupSize"/>, <see cref="FinalPlacesForGroup"/>) is stored per row
/// so validation never needs to recompute history.
/// </summary>
public sealed class TypeCupTournamentDrawEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public string CreatureType { get; set; } = string.Empty;

    public int QualificationGroup { get; set; }

    public int GroupSize { get; set; }

    public int FieldTeamCount { get; set; }

    public int QualificationGroupCount { get; set; }

    public int FinalPlacesForGroup { get; set; }

    public int RulesVersion { get; set; }

    public int TournamentFormatVersion { get; set; }

    public long RngBeforeState { get; set; }

    public long RngBeforeStream { get; set; }

    public long RngAfterState { get; set; }

    public long RngAfterStream { get; set; }

    public string DrawChecksum { get; set; } = string.Empty;
}
