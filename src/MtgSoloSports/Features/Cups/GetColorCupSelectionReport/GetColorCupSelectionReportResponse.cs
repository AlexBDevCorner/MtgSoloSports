namespace MtgSoloSports.Features.Cups.GetColorCupSelectionReport;

/// <summary>
/// Persisted explanation of one Color Cup selection (never recomputed): per
/// sporting color the top of the selection ranking with the cut after the
/// selected four. <see cref="HasFullRanking"/> is false for selections made
/// before reports were stored; those list only the selected athletes.
/// </summary>
public sealed record GetColorCupSelectionReportResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int RulesVersion,
    bool HasFullRanking,
    int TeamSize,
    int BonusWeightPermille,
    int PerformanceWeightPermille,
    int FormWeightPermille,
    int PrestigeWeightPermille,
    IReadOnlyList<GetColorCupSelectionReportResponse.Team> Teams)
{
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
        int UnadjustedFormAggregate,
        int PrestigeSuperTitleRaw,
        int PrestigeFeeder1TitleRaw,
        int PrestigeFeeder2TitleRaw,
        int PrestigeFeeder3TitleRaw,
        int PrestigeAppearanceRaw,
        int PrestigeSuperStageRaw,
        int PrestigeFeeder1StageRaw,
        int PrestigeFeeder2StageRaw,
        int PrestigeFeeder3StageRaw,
        int PrestigeMajorCupRaw);
}
