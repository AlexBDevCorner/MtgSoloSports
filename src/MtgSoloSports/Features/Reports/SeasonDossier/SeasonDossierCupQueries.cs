using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.GetColorCupTeamResult;
using MtgSoloSports.Features.Cups.GetTypeCupTournament;
using MtgSoloSports.Features.Reports.CupEditionReport;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal static class SeasonDossierCupQueries
{
    internal static async Task<(CupSummaryEntry? Color, CupSummaryEntry? Type)> LoadCupsAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity season,
        CancellationToken cancellationToken)
    {
        CupSummaryEntry? color = await TryLoadColorAsync(context, saveId, season, cancellationToken).ConfigureAwait(false);
        CupSummaryEntry? type = await TryLoadTypeAsync(context, saveId, season, cancellationToken).ConfigureAwait(false);
        return (color, type);
    }

    internal static async Task<CupSummaryEntry?> TryLoadColorAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity season,
        CancellationToken cancellationToken)
    {
        GetCupEditionReportHandler.ReportData data;
        try
        {
            data = await GetCupEditionReportHandler.LoadColorAsync(context, saveId, season, cancellationToken).ConfigureAwait(false);
        }
        catch (CupEditionReportNotFoundException)
        {
            return ColorNotHeld();
        }

        IndividualCupSummary? individual = MapIndividual(data.ColorIndividual);
        TeamCupSummary? team = MapColorTeam(data.ColorTeam, data.Squads);
        string note = BuildColorNote(data);
        return new CupSummaryEntry("Color", data.State, true, individual, team, null, note);
    }

    internal static CupSummaryEntry ColorNotHeld()
    {
        return new CupSummaryEntry("Color", "NotHeld", false, null, null, null, "Color Cup not yet selected for this season; no squads or events persisted.");
    }

    internal static IndividualCupSummary? MapIndividual(GetColorCupIndividualResultResponse? individual)
    {
        if (individual is null)
        {
            return new IndividualCupSummary(null, null, []);
        }

        List<CupPodiumEntry> top = individual.Standings
            .Where(r => r.CupRank is >= 1 and <= 3)
            .OrderBy(r => r.CupRank)
            .Select(r => new CupPodiumEntry(r.CupRank, r.SportingColor, r.SportingColor, r.CupScoreThousandths, r.Medal))
            .ToList();
        return new IndividualCupSummary(individual.ChampionName, individual.ChampionAthleteId, top);
    }

    internal static TeamCupSummary? MapColorTeam(
        GetColorCupTeamResultResponse? team,
        IReadOnlyList<GetCupEditionReportHandler.SquadTeam> squads)
    {
        if (team is null)
        {
            return new TeamCupSummary(null, null, [], []);
        }

        List<CupPodiumEntry> top = team.Teams
            .Where(t => t.TeamRank is >= 1 and <= 3)
            .OrderBy(t => t.TeamRank)
            .Select(t => new CupPodiumEntry(
                t.TeamRank,
                Cups.CupHistory.CupTeamKeys.ColorKey((SimulationKernel.Catalog.SportingColor)t.SportingColor),
                t.TeamName,
                t.TeamScoreThousandths,
                t.Medal))
            .ToList();
        GetColorCupTeamMember? champion = team.Teams.OrderBy(t => t.TeamRank).FirstOrDefault();
        List<CupRosterEntry> roster = [];
        string? championKey = null;
        if (champion is not null)
        {
            championKey = Cups.CupHistory.CupTeamKeys.ColorKey((SimulationKernel.Catalog.SportingColor)champion.SportingColor);
            roster = squads
                .Where(s => string.Equals(s.TeamKey, championKey, StringComparison.Ordinal))
                .SelectMany(s => s.Members)
                .OrderBy(m => m.SelectionRank)
                .Select(m => new CupRosterEntry(m.AthleteId, m.Name))
                .ToList();
        }

        return new TeamCupSummary(championKey, champion?.TeamName, top, roster);
    }

    internal static string BuildColorNote(GetCupEditionReportHandler.ReportData data)
    {
        bool individualDone = data.ColorIndividual is not null;
        bool teamDone = data.ColorTeam is not null;
        if (individualDone && teamDone)
        {
            return "Color Cup complete: individual ranks 1-3 hold official medals; team ranks 1-3 hold official team medals. Squads are historically correct selections.";
        }

        if (individualDone || teamDone)
        {
            return $"Color Cup {data.State}: one event finished, the other is pending; pending results are omitted, never inferred.";
        }

        return $"Color Cup {data.State}: squads selected; individual and team events pending.";
    }

    internal static async Task<CupSummaryEntry?> TryLoadTypeAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity season,
        CancellationToken cancellationToken)
    {
        GetCupEditionReportHandler.ReportData data;
        try
        {
            data = await GetCupEditionReportHandler.LoadTypeAsync(context, saveId, season, cancellationToken).ConfigureAwait(false);
        }
        catch (CupEditionReportNotFoundException)
        {
            return TypeNotHeld();
        }

        TypeCupSummary tournament = MapTournament(data.TypeTournament, data.Squads);
        string note = BuildTypeNote(data);
        return new CupSummaryEntry("Type", data.State, true, null, null, tournament, note);
    }

    internal static CupSummaryEntry TypeNotHeld()
    {
        return new CupSummaryEntry("Type", "NotHeld", false, null, null, null, "Type Cup not yet selected for this season; no squads or events persisted.");
    }

    internal static TypeCupSummary MapTournament(
        GetTypeCupTournamentResponse? tournament,
        IReadOnlyList<GetCupEditionReportHandler.SquadTeam> squads)
    {
        if (tournament is null)
        {
            return new TypeCupSummary(null, [], 0, 0, [], [], false);
        }

        List<CupPodiumEntry> finalTop = [];
        List<CupRosterEntry> roster = [];
        if (tournament.Final is not null)
        {
            finalTop = tournament.Final.Teams
                .Where(t => t.TeamRank is >= 1 and <= 3)
                .OrderBy(t => t.TeamRank)
                .Select(t => new CupPodiumEntry(t.TeamRank, t.CreatureType, t.CreatureType, t.TeamScoreThousandths, t.Medal))
                .ToList();
            roster = squads
                .Where(s => string.Equals(s.TeamKey, tournament.ChampionCreatureType, StringComparison.Ordinal))
                .SelectMany(s => s.Members)
                .OrderBy(m => m.SelectionRank)
                .Select(m => new CupRosterEntry(m.AthleteId, m.Name))
                .ToList();
        }

        List<string> qualified = tournament.QualificationGroups
            .SelectMany(g => g.QualifiedTeams)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        return new TypeCupSummary(
            tournament.ChampionCreatureType,
            finalTop,
            tournament.QualificationGroups.Count,
            tournament.QualificationGroupCount,
            qualified,
            roster,
            tournament.IsDirectFinal);
    }

    internal static string BuildTypeNote(GetCupEditionReportHandler.ReportData data)
    {
        if (data.TypeTournament?.Final is not null)
        {
            return "Type Cup complete: only Final ranks 1-3 hold official medals; qualification groups decided Final entry and never award titles.";
        }

        if (data.TypeTournament is not null)
        {
            return $"Type Cup {data.State}: qualification in progress ({data.TypeTournament.QualificationGroups.Count}/{data.TypeTournament.QualificationGroupCount} groups finished); Final pending and omitted.";
        }

        return $"Type Cup {data.State}: squads selected; qualification and Final pending.";
    }
}
