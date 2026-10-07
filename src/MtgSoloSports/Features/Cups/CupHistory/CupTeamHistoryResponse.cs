namespace MtgSoloSports.Features.Cups.CupHistory;

/// <summary>
/// One Cup team across every edition it was selected for: honours, the squad
/// and result of each season (newest first), the all-time roster and, for
/// Color teams, the individual-event medals its athletes won. Identical shape
/// for both Cups; built only from persisted rows.
/// </summary>
public sealed record CupTeamHistoryResponse(
    Guid SaveId,
    string Cup,
    string TeamKey,
    string TeamName,
    CupTeamHistoryResponse.TeamHonours Honours,
    IReadOnlyList<CupTeamHistoryResponse.Season> Seasons,
    IReadOnlyList<CupTeamHistoryResponse.RosterEntry> Roster,
    IReadOnlyList<CupTeamHistoryResponse.IndividualMedal> IndividualMedals)
{
    /// <summary>Best rank fields are null until the team has finished a Cup.</summary>
    public sealed record TeamHonours(
        int Editions,
        int Gold,
        int Silver,
        int Bronze,
        int? BestRank,
        int? BestRankSeasonNumber,
        int GroupWins,
        int RoundWins,
        long TotalScoreThousandths);

    /// <summary>Result fields are null until the team event of that edition has finished.</summary>
    public sealed record Season(
        int SourceSeasonNumber,
        string State,
        int TeamCount,
        int? TeamRank,
        string? Medal,
        int? TeamScoreThousandths,
        int? TeamBaseThousandths,
        int? GroupWins,
        int? RoundWins,
        IReadOnlyList<SquadMember> Squad,
        string? TournamentStage = null,
        int? TournamentPhase = null,
        int? QualificationGroup = null,
        bool QualifiedForFinal = false,
        bool EliminatedInQualification = false);

    /// <summary>
    /// <see cref="Reason"/> is Type Cup only and null when no selection report
    /// was stored; <see cref="Leg"/> is null until the member's group has a
    /// stored standing; <see cref="Individual"/> is Color Cup only.
    /// </summary>
    public sealed record SquadMember(
        int AthleteId,
        string Name,
        string? ImageUrl,
        int SelectionRank,
        int FinalRatingThousandths,
        int BonusNormThousandths,
        int PerformanceNormThousandths,
        int FormNormThousandths,
        int PrestigeNormThousandths,
        string? Reason,
        Leg? Leg,
        Individual? Individual);

    public sealed record Leg(
        int GroupNumber,
        int GroupRank,
        int GroupSize,
        int GroupScoreThousandths,
        int BaseScoreThousandths,
        int RoundWins);

    public sealed record Individual(int CupRank, int CupScoreThousandths, string Medal);

    public sealed record RosterEntry(
        int AthleteId,
        string Name,
        string? ImageUrl,
        int Caps,
        int FirstSeasonNumber,
        int LastSeasonNumber,
        long TotalLegScoreThousandths,
        int? BestGroupRank,
        int BestSelectionRank);

    public sealed record IndividualMedal(int SourceSeasonNumber, int AthleteId, string Name, string Medal);
}
