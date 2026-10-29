using MtgSoloSports.Features.Cups.ListCupEditions;

namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record AlmanacCupTables(
    IReadOnlyList<AlmanacCupEditionEntry> Editions,
    IReadOnlyList<ListCupEditionsResponse.TeamSummary> ColorTeamTable,
    IReadOnlyList<ListCupEditionsResponse.ColorMedalRow> ColorIndividualMedals,
    IReadOnlyList<ListCupEditionsResponse.ColorMedalRow> ColorCombinedMedals,
    IReadOnlyList<ListCupEditionsResponse.TeamSummary> TypeTeamTable);
