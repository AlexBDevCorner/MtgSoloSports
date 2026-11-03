namespace MtgSoloSports.Features.Cups.GetChaosCupAthleteJourney;

/// <summary>
/// Read-only Chaos Cup journey for one saved athlete (MSS-108). Reports each
/// drawn stage's group with official placement, score and advancement from the
/// persisted group standings; a drawn but incomplete group stays provisional
/// with no rank or score. The Final carries the official place 1..32 when
/// complete. Eliminated athletes carry only an exit-stage category; no global
/// numeric rank is fabricated for nonfinalists. Reads never simulate, draw or
/// consume RNG. Shareable by save, season and athlete ids.
/// </summary>
public sealed record GetChaosCupAthleteJourneyResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int AthleteId,
    string Name,
    string? ImageUrl,
    int SportingColor,
    string SportingColorName,
    int? SourceLeagueId,
    int SourceLeagueKind,
    int SourceFeederDivision,
    bool IsPoolInvite,
    string SourceLabel,
    int EffectiveBonusThousandths,
    int StagesEntered,
    bool IsFinalist,
    bool IsEliminated,
    int? EliminatedStageIndex,
    string? ExitStage,
    int? FinalPlace,
    IReadOnlyList<GetChaosCupAthleteJourneyResponse.StageEntry> Stages)
{
    public sealed record StageEntry(
        int StageIndex,
        int GroupCount,
        int GroupNumber,
        bool IsFinal,
        bool IsDrawn,
        bool IsComplete,
        bool StandingsProvisional,
        bool IsOfficial,
        int? GroupRank,
        int? GroupScoreThousandths,
        int? BaseScoreThousandths,
        int? RoundWins,
        bool? IsAdvanced,
        int RoundsPlayed,
        int TotalRounds);
}
