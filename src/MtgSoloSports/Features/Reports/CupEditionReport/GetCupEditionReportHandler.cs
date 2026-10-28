using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.GetColorCupTeamResult;
using MtgSoloSports.Features.Cups.GetTypeCupTournament;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Reports.CupEditionReport;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Builds a versioned,
/// standalone report for exactly one Cup edition (color or type, historical
/// source season). Reuses the persisted selection squads plus the existing
/// result read models; never resimulates and never substitutes another
/// edition. Unfinished editions report finished vs pending phases and omit
/// unknown results. Read-only: single AsNoTracking context in one committed
/// read transaction, no RNG access, no mutation.
/// </summary>
public sealed class GetCupEditionReportHandler
{
    private readonly SaveStore _store;

    public GetCupEditionReportHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public sealed record ReportFile(byte[] Content, string ContentType, string FileName);

    internal sealed record SquadMember(
        int AthleteId,
        string Name,
        int SelectionRank,
        int FinalRatingThousandths,
        int? TypeRank);

    internal sealed record SquadTeam(
        string TeamKey,
        string TeamName,
        IReadOnlyList<SquadMember> Members);

    internal sealed record ReportData(
        string Cup,
        int SourceSeasonNumber,
        int SourceSeasonId,
        string State,
        int TeamCount,
        bool HasFullRanking,
        IReadOnlyList<SquadTeam> Squads,
        GetColorCupIndividualResultResponse? ColorIndividual,
        GetColorCupTeamResultResponse? ColorTeam,
        GetTypeCupTournamentResponse? TypeTournament);

    public async Task<ReportFile> HandleAsync(
        Guid saveId,
        string cup,
        int seasonNumber,
        string? format,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        string normalizedCup = ReportContract.NormalizeCup(cup);
        if (seasonNumber < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(seasonNumber));
        }

        string normalized = ReportContract.NormalizeFormat(format);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);

        ReportData data = normalizedCup switch
        {
            "color" => await LoadColorAsync(context, saveId, source, cancellationToken).ConfigureAwait(false),
            "type" => await LoadTypeAsync(context, saveId, source, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unsupported cup '{cup}'. Use 'color' or 'type'.", nameof(cup)),
        };
        int seasonCount = await context.Seasons.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(normalized, "md", StringComparison.Ordinal))
        {
            string markdown = CupEditionReportMarkdown.Build(saveId, metadata, rules, seasonCount, data);
            return new ReportFile(
                Encoding.UTF8.GetBytes(markdown),
                ReportContract.MarkdownContentType,
                ReportFilenames.CupEditionFileName(normalizedCup, seasonNumber, "md"));
        }

        string json = CupEditionReportJson.Build(saveId, metadata, rules, seasonCount, data);
        return new ReportFile(
            Encoding.UTF8.GetBytes(json),
            ReportContract.JsonContentType,
            ReportFilenames.CupEditionFileName(normalizedCup, seasonNumber, "json"));
    }

    internal static async Task<SeasonEntity> LoadSeasonAsync(
        SaveDbContext context,
        int seasonNumber,
        CancellationToken cancellationToken)
    {
        SeasonEntity? season = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonNumber == seasonNumber, cancellationToken)
            .ConfigureAwait(false);
        if (season is null)
        {
            throw new CupEditionReportNotFoundException($"Cup edition for Season {seasonNumber} does not exist in this save.");
        }

        return season;
    }

    internal static async Task<ReportData> LoadColorAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        List<ColorCupSelectionEntity> selections = await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.SportingColor)
            .ThenBy(e => e.SelectionRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (selections.Count == 0)
        {
            throw new CupEditionReportNotFoundException($"Color Cup edition for Season {source.SeasonNumber} has not been selected yet.");
        }

        Dictionary<int, string> names = await LoadNamesAsync(context, selections.Select(s => s.SaveAthleteId).ToList(), cancellationToken).ConfigureAwait(false);
        bool hasFullRanking = await HasSelectionReportAsync(context, source.Id, CupExtensionPoint.ColorCup, cancellationToken).ConfigureAwait(false);

        GetColorCupIndividualResultResponse? individual = await TryLoadColorIndividualAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
        GetColorCupTeamResultResponse? team = await TryLoadColorTeamAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);

        bool teamComplete = team is not null;
        bool individualComplete = individual is not null;
        bool anyPlayed = teamComplete || individualComplete || await AnyColorRoundsAsync(context, source.Id, cancellationToken).ConfigureAwait(false);
        string state = CupEditionState.For(anyPlayed, teamComplete && individualComplete);

        return new ReportData(
            "Color",
            source.SeasonNumber,
            source.Id,
            state,
            selections.Select(s => s.SportingColor).Distinct().Count(),
            hasFullRanking,
            BuildColorSquads(selections, names),
            individual,
            team,
            null);
    }

    internal static async Task<ReportData> LoadTypeAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        List<TypeCupSelectionEntity> selections = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.CreatureType)
            .ThenBy(e => e.SelectionRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (selections.Count == 0)
        {
            throw new CupEditionReportNotFoundException($"Type Cup edition for Season {source.SeasonNumber} has not been selected yet.");
        }

        Dictionary<int, string> names = await LoadNamesAsync(context, selections.Select(s => s.SaveAthleteId).ToList(), cancellationToken).ConfigureAwait(false);
        bool hasFullRanking = await HasSelectionReportAsync(context, source.Id, CupExtensionPoint.TypeCup, cancellationToken).ConfigureAwait(false);

        GetTypeCupTournamentResponse? tournament = await TryLoadTournamentAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
        bool anyPlayed = tournament is not null || await AnyTypeRoundsAsync(context, source.Id, cancellationToken).ConfigureAwait(false);
        bool complete = tournament is not null && tournament.Final is not null;
        string state = CupEditionState.For(anyPlayed, complete);

        return new ReportData(
            "Type",
            source.SeasonNumber,
            source.Id,
            state,
            selections.Select(s => s.CreatureType).Distinct(StringComparer.Ordinal).Count(),
            hasFullRanking,
            BuildTypeSquads(selections, names),
            null,
            null,
            tournament);
    }

    internal static List<SquadTeam> BuildColorSquads(List<ColorCupSelectionEntity> selections, Dictionary<int, string> names)
    {
        return selections
            .GroupBy(s => s.SportingColor)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                string key = CupTeamKeys.ColorKey((SportingColor)g.Key);
                string teamName = CupTeamKeys.ColorName((SportingColor)g.Key);
                List<SquadMember> members = g
                    .OrderBy(s => s.SelectionRank)
                    .Select(s => new SquadMember(
                        s.SaveAthleteId,
                        names.GetValueOrDefault(s.SaveAthleteId, $"Athlete {s.SaveAthleteId}"),
                        s.SelectionRank,
                        s.FinalRatingThousandths,
                        null))
                    .ToList();
                return new SquadTeam(key, teamName, members);
            })
            .ToList();
    }

    internal static List<SquadTeam> BuildTypeSquads(List<TypeCupSelectionEntity> selections, Dictionary<int, string> names)
    {
        return selections
            .GroupBy(s => s.CreatureType, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                List<SquadMember> members = g
                    .OrderBy(s => s.SelectionRank)
                    .Select(s => new SquadMember(
                        s.SaveAthleteId,
                        names.GetValueOrDefault(s.SaveAthleteId, $"Athlete {s.SaveAthleteId}"),
                        s.SelectionRank,
                        s.FinalRatingThousandths,
                        s.TypeRank))
                    .ToList();
                return new SquadTeam(g.Key, g.Key, members);
            })
            .ToList();
    }

    internal static async Task<Dictionary<int, string>> LoadNamesAsync(
        SaveDbContext context,
        List<int> athleteIds,
        CancellationToken cancellationToken)
    {
        List<int> distinct = athleteIds.Distinct().ToList();
        return await context.SaveAthletes
            .AsNoTracking()
            .Where(e => distinct.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<bool> HasSelectionReportAsync(
        SaveDbContext context,
        int sourceSeasonId,
        string cup,
        CancellationToken cancellationToken)
    {
        return await context.CupSelectionReports
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == sourceSeasonId && e.Cup == cup, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<GetColorCupIndividualResultResponse?> TryLoadColorIndividualAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.ColorCupIndividualStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            return null;
        }

        return await GetColorCupIndividualResultHandler.BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<GetColorCupTeamResultResponse?> TryLoadColorTeamAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.ColorCupTeamStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            return null;
        }

        return await GetColorCupTeamResultHandler.BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<GetTypeCupTournamentResponse?> TryLoadTournamentAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool hasFinalOrLegacy = await context.TypeCupTeamStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id
                && (e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                    || e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final), cancellationToken)
            .ConfigureAwait(false);
        if (hasFinalOrLegacy)
        {
            return await GetTypeCupTournamentHandler.BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
        }

        bool hasQualification = await context.TypeCupTeamStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id
                && e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification, cancellationToken)
            .ConfigureAwait(false);
        if (!hasQualification)
        {
            return null;
        }

        return await BuildPartialTournamentAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record PartialTournamentRows(
        RulesV1 Rules,
        List<TypeCupSelectionEntity> Selection,
        List<TypeCupTournamentDrawEntity> Draws,
        List<TypeCupTeamStandingEntity> Teams,
        List<TypeCupTeamGroupStandingEntity> Legs);

    internal static async Task<GetTypeCupTournamentResponse?> BuildPartialTournamentAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        PartialTournamentRows rows = await LoadPartialRowsAsync(context, source, cancellationToken).ConfigureAwait(false);
        if (rows.Teams.Count == 0)
        {
            return null;
        }

        EnsurePartialRowsOnly(rows);
        TypeCupTournamentPlan.Plan plan = BuildPartialPlan(rows);
        Dictionary<int, string> names = await LoadLegNamesAsync(context, rows.Legs, cancellationToken).ConfigureAwait(false);
        List<TypeCupTournamentQualificationGroup> groups = CollectPartialGroups(plan, rows, names);
        if (groups.Count == 0)
        {
            return null;
        }

        return CreatePartialResponse(saveId, source, rows, plan, groups);
    }

    internal static async Task<PartialTournamentRows> LoadPartialRowsAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        List<TypeCupSelectionEntity> selection = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTournamentDrawEntity> draws = await context.TypeCupTournamentDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new PartialTournamentRows(rules, selection, draws, teams, legs);
    }

    internal static void EnsurePartialRowsOnly(PartialTournamentRows rows)
    {
        // Any Final/Legacy row would have taken the full-tournament path above;
        // seeing one here means concurrent persistence changed mid-read.
        if (rows.Teams.Any(t => t.TournamentPhase != (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification)
            || rows.Legs.Any(l => l.TournamentPhase != (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification))
        {
            throw new InvalidOperationException("Type Cup tournament gained Final standings during the report read.");
        }
    }

    internal static TypeCupTournamentPlan.Plan BuildPartialPlan(PartialTournamentRows rows)
    {
        int teamCount = rows.Selection.Select(e => e.CreatureType).Distinct(StringComparer.Ordinal).Count();
        List<string> selectedTypes = rows.Selection.Select(e => e.CreatureType).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();
        TypeCupTournamentPlan.Plan plan = TypeCupTournamentPlan.BuildFromSelection(teamCount, selectedTypes, rows.Draws, rows.Rules);
        if (plan.IsLegacy || plan.IsDirectFinal)
        {
            throw new InvalidOperationException("Type Cup qualification standings exist without a qualification tournament plan.");
        }

        return plan;
    }

    internal static List<TypeCupTournamentQualificationGroup> CollectPartialGroups(
        TypeCupTournamentPlan.Plan plan,
        PartialTournamentRows rows,
        Dictionary<int, string> names)
    {
        int qualPhase = (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification;
        List<TypeCupTournamentQualificationGroup> groups = new(plan.QualificationGroupCount);
        foreach (TypeCupTournamentPlan.QualificationStage stage in plan.QualificationStages.OrderBy(s => s.QualificationGroup))
        {
            List<TypeCupTeamStandingEntity> stageTeams = rows.Teams.Where(t => t.TournamentPhase == qualPhase && t.QualificationGroup == stage.QualificationGroup).OrderBy(t => t.TeamRank).ToList();
            if (stageTeams.Count == 0)
            {
                continue;
            }

            List<TypeCupTeamGroupStandingEntity> stageLegs = rows.Legs.Where(l => l.TournamentPhase == qualPhase && l.QualificationGroup == stage.QualificationGroup).ToList();
            // Qualification stages persist atomically on stage completion; a
            // short row count is corruption, not a reportable partial state.
            if (stageTeams.Count != stage.GroupSize || stageLegs.Count != stage.GroupSize * rows.Rules.TypeCupMinTeamSize)
            {
                throw new InvalidOperationException($"Type Cup qualification group {stage.QualificationGroup} has corrupt partial standings.");
            }

            groups.Add(BuildPartialQualGroup(plan, stage, stageTeams, stageLegs, names));
        }

        return groups;
    }

    internal static GetTypeCupTournamentResponse CreatePartialResponse(
        Guid saveId,
        SeasonEntity source,
        PartialTournamentRows rows,
        TypeCupTournamentPlan.Plan plan,
        List<TypeCupTournamentQualificationGroup> groups)
    {
        string drawChecksum = rows.Draws.Count > 0 ? rows.Draws.First().DrawChecksum : string.Empty;
        return new GetTypeCupTournamentResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            false,
            plan.TeamCount,
            plan.QualificationGroupCount,
            plan.QualificationStages.Select(s => s.GroupSize).ToList(),
            plan.QualificationStages.Select(s => s.FinalPlaces).ToList(),
            drawChecksum,
            groups,
            [],
            null,
            null,
            string.Empty,
            plan.QualificationPolicyVersion,
            plan.WildcardCount,
            plan.QualificationStages.Select(s => s.FinalPlaces).ToList(),
            []);
    }

    internal static TypeCupTournamentQualificationGroup BuildPartialQualGroup(
        TypeCupTournamentPlan.Plan plan,
        TypeCupTournamentPlan.QualificationStage stage,
        List<TypeCupTeamStandingEntity> stageTeams,
        List<TypeCupTeamGroupStandingEntity> stageLegs,
        Dictionary<int, string> names)
    {
        List<TypeCupTeamStandingEntity> ordered = stageTeams.OrderBy(t => t.TeamRank).ToList();
        string checksum = RunTypeCupTeamHandler.ComputeChecksum(ToPartialRanked(ordered));
        if (!plan.IsWildcardPolicy)
        {
            HashSet<string> qualified = ordered.Take(stage.FinalPlaces).Select(t => t.CreatureType).ToHashSet(StringComparer.Ordinal);
            return new TypeCupTournamentQualificationGroup(
                stage.QualificationGroup,
                stage.GroupSize,
                stage.FinalPlaces,
                checksum,
                ordered.Select(t => new TypeCupTournamentTeam(t.CreatureType, t.TeamRank, t.TeamScoreThousandths, t.TeamBaseThousandths, ((TypeCupMedal)t.Medal).ToString(), qualified.Contains(t.CreatureType), qualified.Contains(t.CreatureType) ? "Guaranteed" : "Eliminated")).ToList(),
                stageLegs.Select(l => new TypeCupTournamentLeg(l.SaveAthleteId, names.GetValueOrDefault(l.SaveAthleteId, $"Athlete {l.SaveAthleteId}"), l.CreatureType, l.SelectionRank, l.GroupNumber, l.GroupRank, l.GroupScoreThousandths, l.BaseScoreThousandths, qualified.Contains(l.CreatureType))).ToList(),
                qualified.OrderBy(t => t, StringComparer.Ordinal).ToList(),
                ordered.Select(t => t.CreatureType).Where(t => !qualified.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList(),
                stage.FinalPlaces,
                null,
                null);
        }

        int guaranteed = stage.FinalPlaces;
        HashSet<string> guaranteedNames = ordered.Take(guaranteed).Select(t => t.CreatureType).ToHashSet(StringComparer.Ordinal);
        string? candidate = ordered.Count > guaranteed ? ordered[guaranteed].CreatureType : null;
        List<TypeCupTournamentTeam> teamRows = new(ordered.Count);
        foreach (TypeCupTeamStandingEntity team in ordered)
        {
            if (guaranteedNames.Contains(team.CreatureType))
            {
                teamRows.Add(new TypeCupTournamentTeam(team.CreatureType, team.TeamRank, team.TeamScoreThousandths, team.TeamBaseThousandths, ((TypeCupMedal)team.Medal).ToString(), true, "Guaranteed"));
            }
            else if (candidate is not null && string.Equals(team.CreatureType, candidate, StringComparison.Ordinal))
            {
                teamRows.Add(new TypeCupTournamentTeam(team.CreatureType, team.TeamRank, team.TeamScoreThousandths, team.TeamBaseThousandths, ((TypeCupMedal)team.Medal).ToString(), false, "WildcardCandidate"));
            }
            else
            {
                teamRows.Add(new TypeCupTournamentTeam(team.CreatureType, team.TeamRank, team.TeamScoreThousandths, team.TeamBaseThousandths, ((TypeCupMedal)team.Medal).ToString(), false, "Eliminated"));
            }
        }

        List<string> qualifiedList = guaranteedNames.OrderBy(t => t, StringComparer.Ordinal).ToList();
        List<string> eliminatedList = ordered.Select(t => t.CreatureType).Where(t => !guaranteedNames.Contains(t) && !string.Equals(t, candidate, StringComparison.Ordinal)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        return new TypeCupTournamentQualificationGroup(
            stage.QualificationGroup,
            stage.GroupSize,
            stage.FinalPlaces,
            checksum,
            teamRows,
            stageLegs.Select(l => new TypeCupTournamentLeg(l.SaveAthleteId, names.GetValueOrDefault(l.SaveAthleteId, $"Athlete {l.SaveAthleteId}"), l.CreatureType, l.SelectionRank, l.GroupNumber, l.GroupRank, l.GroupScoreThousandths, l.BaseScoreThousandths, guaranteedNames.Contains(l.CreatureType))).ToList(),
            qualifiedList,
            eliminatedList,
            guaranteed,
            candidate,
            null);
    }

    internal static async Task<Dictionary<int, string>> LoadLegNamesAsync(
        SaveDbContext context,
        List<TypeCupTeamGroupStandingEntity> legs,
        CancellationToken cancellationToken)
    {
        List<int> ids = legs.Select(l => l.SaveAthleteId).Distinct().ToList();
        return await context.SaveAthletes
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static IReadOnlyList<SimulationKernel.Cups.TeamEvent.TeamRanked> ToPartialRanked(
        List<TypeCupTeamStandingEntity> teams)
    {
        List<SimulationKernel.Cups.TeamEvent.TeamRanked> ranked = new(teams.Count);
        foreach (TypeCupTeamStandingEntity team in teams.OrderBy(t => t.TeamRank))
        {
            ranked.Add(new SimulationKernel.Cups.TeamEvent.TeamRanked(0, team.CreatureType, team.TeamRank, team.TeamScoreThousandths, team.TeamBaseThousandths, team.GroupWins, team.RoundWins, [0], [0]));
        }

        return ranked;
    }

    internal static async Task<bool> AnyColorRoundsAsync(SaveDbContext context, int sourceSeasonId, CancellationToken cancellationToken)
    {
        bool teamRounds = await context.ColorCupTeamRounds
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == sourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (teamRounds)
        {
            return true;
        }

        return await context.ColorCupIndividualRounds
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == sourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<bool> AnyTypeRoundsAsync(SaveDbContext context, int sourceSeasonId, CancellationToken cancellationToken)
    {
        return await context.TypeCupTeamRounds
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == sourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string BuildJson(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        ReportData data)
    {
        return CupEditionReportJson.Build(saveId, metadata, rules, seasonCount, data);
    }

    internal static string BuildMarkdown(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        ReportData data)
    {
        return CupEditionReportMarkdown.Build(saveId, metadata, rules, seasonCount, data);
    }
}
