namespace MtgSoloSports.Features.Cups.GetTypeCupSelectionReport;

/// <summary>
/// Persisted explanation of one Type Cup allocation (never recomputed): per
/// fielded creature-type team the top of that type's ranking with the four
/// members flagged, the reason each member represents the type and where the
/// other ranked athletes ended up; plus types that could have fielded a team
/// but lost their athletes to other teams. <see cref="HasFullRanking"/> is
/// false for allocations made before reports were stored; those list only the
/// members and carry no reasons.
/// </summary>
public sealed record GetTypeCupSelectionReportResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int RulesVersion,
    bool HasFullRanking,
    int TeamSize,
    int CandidateCount,
    int BonusWeightPermille,
    int PerformanceWeightPermille,
    int FormWeightPermille,
    int PrestigeWeightPermille,
    IReadOnlyList<GetTypeCupSelectionReportResponse.Team> Teams,
    IReadOnlyList<GetTypeCupSelectionReportResponse.MissedTeam> MissedTeams)
{
    /// <summary>Member is permanently capped for this type and may represent no other.</summary>
    public const string ReasonCapped = "Capped";

    /// <summary>Uncapped member whose only type able to field a team is this one.</summary>
    public const string ReasonOnlyType = "OnlyType";

    /// <summary>Uncapped member placed with the type where it ranks best.</summary>
    public const string ReasonBestRank = "BestRank";

    /// <summary>Uncapped member placed away from its best-ranked type so that more teams could be fielded.</summary>
    public const string ReasonBalanced = "Balanced";

    public sealed record Team(
        string TeamKey,
        string TeamName,
        int CandidateCount,
        IReadOnlyList<Candidate> Ranking);

    public sealed record Candidate(
        int AthleteId,
        string Name,
        string? ImageUrl,
        int Rank,
        bool Selected,
        int? SelectionRank,
        string? AssignedTeam,
        bool Capped,
        string? Reason,
        IReadOnlyList<Alternative> Alternatives,
        int FinalRatingThousandths,
        int BonusNormThousandths,
        int PerformanceNormThousandths,
        int FormNormThousandths,
        int PrestigeNormThousandths,
        int BonusRawThousandths,
        int PerformanceRawThousandths,
        int FormRaw,
        int PrestigeRaw,
        string? SourceLeagueName,
        int? SourceLeagueLevel,
        int StrengthFactorPermille,
        int UnadjustedPerformanceThousandths,
        int UnadjustedFormAggregate);

    public sealed record Alternative(string TeamName, int Rank, bool FieldsTeam);

    public sealed record MissedTeam(string TeamName, int CandidateCount);
}
