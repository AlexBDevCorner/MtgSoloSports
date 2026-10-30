using MtgSoloSports.Features.History.GetSeasonPlacements;
using MtgSoloSports.Features.Reports.StandingsReport;

namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record SuperleagueSection(
    int? LeagueId,
    string LeagueName,
    bool IsOfficialSuperleague,
    IReadOnlyList<GetStandingsReportHandler.StandingsReportRow> Table,
    IReadOnlyList<SeasonPlacementCell> StagePlacements);
