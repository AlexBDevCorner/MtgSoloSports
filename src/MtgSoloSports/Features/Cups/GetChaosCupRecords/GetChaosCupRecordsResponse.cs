namespace MtgSoloSports.Features.Cups.GetChaosCupRecords;

/// <summary>
/// Read-only Chaos Cup records and Tournament Stories (MSS-117).
/// Career Chaos aggregates (titles, medals, finals, consecutive progress,
/// division/color bests, repeated P16 survivals) plus conditional,
/// source-linked story cards (defended-title failures, record-setting runs,
/// conservative underdogs). Computed live from completed editions only;
/// partial editions never contribute and absence of stories is allowed.
/// Stories are presentation-only: they never affect points, bonus, selection
/// or advancement. Shareable by save id.
/// </summary>
public sealed record GetChaosCupRecordsResponse(
    Guid SaveId,
    string ThresholdsVersion,
    IReadOnlyList<int> CompletedSeasons,
    IReadOnlyList<GetChaosCupRecordsResponse.AthleteRecord> Athletes,
    IReadOnlyList<GetChaosCupRecordsResponse.DivisionRecord> Divisions,
    IReadOnlyList<GetChaosCupRecordsResponse.ColorRecord> Colors,
    IReadOnlyList<GetChaosCupRecordsResponse.StoryCard> Stories)
{
    public sealed record AthleteRecord(
        int SaveAthleteId,
        string AthleteName,
        int Titles,
        int Medals,
        int Finals,
        int? BestFinalRank,
        int DeepestStage,
        int LongestProgressStreak,
        int P16Survivals);

    public sealed record DivisionRecord(
        string DivisionKey,
        string Label,
        int? BestFinalRank,
        IReadOnlyList<int> BestFinalHolderIds,
        int DeepestStage);

    public sealed record ColorRecord(
        int SportingColor,
        string SportingColorName,
        int TitlesMax,
        IReadOnlyList<int> TitleHolderIds,
        int? BestFinalRank,
        IReadOnlyList<int> BestRankHolderIds);

    public sealed record StoryCard(
        string Kind,
        int? SaveAthleteId,
        string AthleteName,
        int SourceSeasonNumber,
        int? RelatedSeasonNumber,
        string Detail,
        string Text);
}
