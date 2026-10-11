using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;

namespace MtgSoloSports.Features.Cups.GetTypeCupTournament;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted Type
/// Cup tournament for a completed even source season (never resimulates):
/// draw, each qualification group result with qualified/eliminated teams and
/// legs, the 32 finalists, the fresh Final result and champion/podium.
/// Direct Finals (1-32 teams) return the single Final with no qualification
/// groups. Old single-field saves (phase 0) return as direct Finals without
/// manufacturing qualification history.
/// </summary>
public sealed class GetTypeCupTournamentHandler
{
    private readonly SaveStore _store;

    public GetTypeCupTournamentHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetTypeCupTournamentResponse> HandleAsync(
        Guid saveId,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SeasonEntity> LoadSourceAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (explicitSeason is null)
            {
                throw new TypeCupTournamentNotFoundException(
                    $"Type Cup tournament for Season {sourceSeasonNumber.Value} has not been resolved yet.");
            }

            await EnsureTournamentAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
            return explicitSeason;
        }

        List<TypeCupTeamStandingEntity> any = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                || e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new TypeCupTournamentNotFoundException("Type Cup tournament has not been resolved yet.");
        }

        int latestSeasonId = any.Max(e => e.SourceSeasonId);
        SeasonEntity? latest = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latestSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new InvalidOperationException("Type Cup tournament references an unknown season.");
        }

        return latest;
    }

    internal static async Task EnsureTournamentAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.TypeCupTeamStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id
                && (e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                    || e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final),
                cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            throw new TypeCupTournamentNotFoundException(
                $"Type Cup tournament for Season {source.SeasonNumber} has not been resolved yet.");
        }
    }

    internal static async Task<GetTypeCupTournamentResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        var rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
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
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<HonourEntity> honours = await context.Honours
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> names = await LoadNamesAsync(context, legs, cancellationToken).ConfigureAwait(false);

        bool hasLegacy = teams.Any(t => t.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField)
            || rounds.Any(r => r.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField);
        if (hasLegacy)
        {
            return BuildLegacyResponse(saveId, source, teams, legs, rounds, names);
        }

        int teamCount = selection.Select(e => e.CreatureType).Distinct(StringComparer.Ordinal).Count();
        var plan = TypeCupTournamentPlan.BuildFromSelection(
            teamCount,
            selection.Select(e => e.CreatureType).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList(),
            draws,
            rules);
        TypeCupTeamInvariants.ValidateTournamentPersisted(source, rounds, legs, teams, honours, rules, plan);
        return plan.IsDirectFinal
            ? BuildDirectResponse(saveId, source, plan, teams, legs, rounds, names)
            : BuildTournamentResponse(saveId, source, plan, draws, teams, legs, rounds, names, rules);
    }

    private static async Task<Dictionary<int, string>> LoadNamesAsync(
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

    private static GetTypeCupTournamentResponse BuildLegacyResponse(
        Guid saveId,
        SeasonEntity source,
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs,
        List<TypeCupTeamRoundEntity> rounds,
        Dictionary<int, string> names)
    {
        var orderedTeams = teams.OrderBy(t => t.TeamRank).ToList();
        string checksum = RunTypeCupTeamHandler.ComputeChecksum(ToRanked(orderedTeams, legs));
        var final = BuildFinalResult(teams, legs, rounds, names, checksum);
        return new GetTypeCupTournamentResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            true,
            teams.Count,
            0,
            [],
            [],
            string.Empty,
            [],
            orderedTeams.Select(t => t.CreatureType).OrderBy(t => t, StringComparer.Ordinal).ToList(),
            final,
            final.ChampionCreatureType,
            checksum);
    }

    private static GetTypeCupTournamentResponse BuildDirectResponse(
        Guid saveId,
        SeasonEntity source,
        TypeCupTournamentPlan.Plan plan,
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs,
        List<TypeCupTeamRoundEntity> rounds,
        Dictionary<int, string> names)
    {
        int finalPhase = (int)TypeCupTournamentFormat.TournamentPhase.Final;
        var finalTeams = teams.Where(t => t.TournamentPhase == finalPhase).OrderBy(t => t.TeamRank).ToList();
        var finalLegs = legs.Where(l => l.TournamentPhase == finalPhase).ToList();
        var finalRounds = rounds.Where(r => r.TournamentPhase == finalPhase).ToList();
        string checksum = RunTypeCupTeamHandler.ComputeChecksum(ToRanked(finalTeams, finalLegs));
        var final = BuildFinalResult(finalTeams, finalLegs, finalRounds, names, checksum);
        return new GetTypeCupTournamentResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            true,
            plan.TeamCount,
            0,
            [],
            [],
            string.Empty,
            [],
            finalTeams.Select(t => t.CreatureType).OrderBy(t => t, StringComparer.Ordinal).ToList(),
            final,
            final.ChampionCreatureType,
            checksum);
    }

    private static GetTypeCupTournamentResponse BuildTournamentResponse(
        Guid saveId,
        SeasonEntity source,
        TypeCupTournamentPlan.Plan plan,
        List<TypeCupTournamentDrawEntity> draws,
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs,
        List<TypeCupTeamRoundEntity> rounds,
        Dictionary<int, string> names,
        SimulationKernel.Rules.RulesV1 rules)
    {
        string drawChecksum = draws.Count > 0 ? draws.First().DrawChecksum : string.Empty;
        int finalPhase = (int)TypeCupTournamentFormat.TournamentPhase.Final;
        var finalTeams = teams.Where(t => t.TournamentPhase == finalPhase).OrderBy(t => t.TeamRank).ToList();
        var finalLegs = legs.Where(l => l.TournamentPhase == finalPhase).ToList();
        var finalRounds = rounds.Where(r => r.TournamentPhase == finalPhase).ToList();
        string finalChecksum = RunTypeCupTeamHandler.ComputeChecksum(ToRanked(finalTeams, finalLegs));
        var final = BuildFinalResult(finalTeams, finalLegs, finalRounds, names, finalChecksum);
        List<string> finalists = finalTeams.Select(t => t.CreatureType).OrderBy(t => t, StringComparer.Ordinal).ToList();
        HashSet<string> finalistSet = finalists.ToHashSet(StringComparer.Ordinal);
        List<TypeCupTournamentQualificationGroup> groups = BuildQualGroups(plan, teams, legs, names, finalistSet, rules);
        (List<TypeCupTournamentWildcard> wildcards, int policy, int wildcardCount, List<int> guaranteed) =
            BuildWildcardProvenance(plan, teams, finalistSet, rules);
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
            finalists,
            final,
            final.ChampionCreatureType,
            finalChecksum,
            policy,
            wildcardCount,
            guaranteed,
            wildcards);
    }

    private static (List<TypeCupTournamentWildcard> Wildcards, int Policy, int WildcardCount, List<int> Guaranteed)
        BuildWildcardProvenance(
            TypeCupTournamentPlan.Plan plan,
            List<TypeCupTeamStandingEntity> teams,
            HashSet<string> finalistSet,
            SimulationKernel.Rules.RulesV1 rules)
    {
        int policy = plan.QualificationPolicyVersion;
        int wildcardCount = plan.WildcardCount;
        List<int> guaranteed = plan.QualificationStages.Select(s => s.FinalPlaces).ToList();
        if (!plan.IsWildcardPolicy || wildcardCount == 0)
        {
            return ([], policy, wildcardCount, guaranteed);
        }

        List<TypeCupTournamentFormat.WildcardCandidate> candidates = CollectWildcardCandidates(plan, teams);
        List<TypeCupTournamentFormat.WildcardCandidate> ordered = OrderWildcardCandidates(candidates, rules);
        HashSet<string> winnerNames = ordered
            .Where(c => finalistSet.Contains(c.Team))
            .Select(c => c.Team)
            .ToHashSet(StringComparer.Ordinal);
        bool tieAtCutoff = DetectWildcardTie(ordered, wildcardCount, winnerNames, rules);
        List<TypeCupTournamentWildcard> wildcards = BuildWildcardRows(ordered, winnerNames, tieAtCutoff, rules);
        return (wildcards, policy, wildcardCount, guaranteed);
    }

    private static List<TypeCupTournamentFormat.WildcardCandidate> CollectWildcardCandidates(
        TypeCupTournamentPlan.Plan plan,
        List<TypeCupTeamStandingEntity> teams)
    {
        int qualPhase = (int)TypeCupTournamentFormat.TournamentPhase.Qualification;
        List<TypeCupTournamentFormat.WildcardCandidate> candidates = new();
        foreach (var stage in plan.QualificationStages.OrderBy(s => s.QualificationGroup))
        {
            var groupTeams = teams
                .Where(t => t.TournamentPhase == qualPhase && t.QualificationGroup == stage.QualificationGroup)
                .OrderBy(t => t.TeamRank)
                .ToList();
            if (groupTeams.Count != stage.GroupSize)
            {
                continue;
            }

            int guaranteedForGroup = stage.FinalPlaces;
            if (groupTeams.Count <= guaranteedForGroup)
            {
                continue;
            }

            var candidate = groupTeams[guaranteedForGroup];
            candidates.Add(new TypeCupTournamentFormat.WildcardCandidate(
                stage.QualificationGroup,
                candidate.CreatureType,
                candidate.TeamRank,
                candidate.TeamScoreThousandths,
                candidate.TeamBaseThousandths,
                stage.GroupSize));
        }

        return candidates;
    }

    private static List<TypeCupTournamentFormat.WildcardCandidate> OrderWildcardCandidates(
        List<TypeCupTournamentFormat.WildcardCandidate> candidates,
        SimulationKernel.Rules.RulesV1 rules)
    {
        List<TypeCupTournamentFormat.WildcardCandidate> ordered = [.. candidates];
        ordered.Sort((a, b) =>
        {
            int adjusted = TypeCupTournamentFormat.CompareWildcardCandidates(a, b, rules);
            return adjusted != 0
                ? adjusted
                : string.Compare(a.Team, b.Team, StringComparison.Ordinal);
        });
        return ordered;
    }

    private static List<TypeCupTournamentWildcard> BuildWildcardRows(
        List<TypeCupTournamentFormat.WildcardCandidate> ordered,
        HashSet<string> winnerNames,
        bool tieAtCutoff,
        SimulationKernel.Rules.RulesV1 rules)
    {
        List<TypeCupTournamentWildcard> wildcards = new();
        foreach (TypeCupTournamentFormat.WildcardCandidate candidate in ordered.Where(c => winnerNames.Contains(c.Team)))
        {
            long expectedSum = TypeCupTournamentFormat.ExpectedBaseSumThousandths(candidate.GroupSize, rules);
            wildcards.Add(new TypeCupTournamentWildcard(
                candidate.Team,
                candidate.QualificationGroup,
                candidate.GroupRank,
                candidate.TeamScoreThousandths,
                candidate.TeamBaseThousandths,
                candidate.GroupSize,
                checked((long)candidate.TeamScoreThousandths * candidate.GroupSize),
                expectedSum,
                tieAtCutoff));
        }

        wildcards.Sort((a, b) => string.Compare(a.CreatureType, b.CreatureType, StringComparison.Ordinal));
        return wildcards;
    }

    private static bool DetectWildcardTie(
        List<TypeCupTournamentFormat.WildcardCandidate> ordered,
        int wildcardCount,
        HashSet<string> winnerNames,
        SimulationKernel.Rules.RulesV1 rules)
    {
        if (wildcardCount <= 0 || ordered.Count == 0)
        {
            return false;
        }

        List<TypeCupTournamentFormat.WildcardCandidate> winners = ordered.Where(c => winnerNames.Contains(c.Team)).ToList();
        List<TypeCupTournamentFormat.WildcardCandidate> eliminated = ordered.Where(c => !winnerNames.Contains(c.Team)).ToList();
        if (winners.Count == 0 || eliminated.Count == 0)
        {
            return false;
        }

        foreach (TypeCupTournamentFormat.WildcardCandidate winner in winners)
        {
            foreach (TypeCupTournamentFormat.WildcardCandidate loser in eliminated)
            {
                if (TypeCupTournamentFormat.CompareWildcardCandidates(winner, loser, rules) == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<TypeCupTournamentQualificationGroup> BuildQualGroups(
        TypeCupTournamentPlan.Plan plan,
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs,
        Dictionary<int, string> names,
        HashSet<string>? finalistSet = null,
        SimulationKernel.Rules.RulesV1? rules = null)
    {
        int qualPhase = (int)TypeCupTournamentFormat.TournamentPhase.Qualification;
        List<TypeCupTournamentQualificationGroup> groups = new(plan.QualificationGroupCount);
        foreach (var stage in plan.QualificationStages.OrderBy(s => s.QualificationGroup))
        {
            var stageTeams = teams.Where(t => t.TournamentPhase == qualPhase && t.QualificationGroup == stage.QualificationGroup).OrderBy(t => t.TeamRank).ToList();
            var stageLegs = legs.Where(l => l.TournamentPhase == qualPhase && l.QualificationGroup == stage.QualificationGroup).ToList();
            string checksum = RunTypeCupTeamHandler.ComputeChecksum(ToRanked(stageTeams, stageLegs));
            groups.Add(BuildSingleQualGroup(plan, stage, stageTeams, stageLegs, names, checksum, finalistSet));
        }

        return groups;
    }

    private static TypeCupTournamentQualificationGroup BuildSingleQualGroup(
        TypeCupTournamentPlan.Plan plan,
        TypeCupTournamentPlan.QualificationStage stage,
        List<TypeCupTeamStandingEntity> stageTeams,
        List<TypeCupTeamGroupStandingEntity> stageLegs,
        Dictionary<int, string> names,
        string checksum,
        HashSet<string>? finalistSet)
    {
        if (!plan.IsWildcardPolicy)
        {
            var qualified = stageTeams.OrderBy(t => t.TeamRank).Take(stage.FinalPlaces).Select(t => t.CreatureType).OrderBy(t => t, StringComparer.Ordinal).ToList();
            var qualifiedSet = qualified.ToHashSet(StringComparer.Ordinal);
            return new TypeCupTournamentQualificationGroup(
                stage.QualificationGroup,
                stage.GroupSize,
                stage.FinalPlaces,
                checksum,
                stageTeams.Select(t => new TypeCupTournamentTeam(t.CreatureType, t.TeamRank, t.TeamScoreThousandths, t.TeamBaseThousandths, ((TypeCupMedal)t.Medal).ToString(), qualifiedSet.Contains(t.CreatureType), qualifiedSet.Contains(t.CreatureType) ? "Guaranteed" : "Eliminated")).ToList(),
                stageLegs.Select(l => new TypeCupTournamentLeg(l.SaveAthleteId, names.GetValueOrDefault(l.SaveAthleteId, $"Athlete {l.SaveAthleteId}"), l.CreatureType, l.SelectionRank, l.GroupNumber, l.GroupRank, l.GroupScoreThousandths, l.BaseScoreThousandths, qualifiedSet.Contains(l.CreatureType))).ToList(),
                qualified,
                stageTeams.Select(t => t.CreatureType).Where(t => !qualifiedSet.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList(),
                stage.FinalPlaces,
                null,
                null);
        }

        return BuildWildcardQualGroup(plan, stage, stageTeams, stageLegs, names, checksum, finalistSet);
    }

    private static TypeCupTournamentQualificationGroup BuildWildcardQualGroup(
        TypeCupTournamentPlan.Plan plan,
        TypeCupTournamentPlan.QualificationStage stage,
        List<TypeCupTeamStandingEntity> stageTeams,
        List<TypeCupTeamGroupStandingEntity> stageLegs,
        Dictionary<int, string> names,
        string checksum,
        HashSet<string>? finalistSet)
    {
        int guaranteed = stage.FinalPlaces;
        List<TypeCupTeamStandingEntity> ordered = stageTeams.OrderBy(t => t.TeamRank).ToList();
        HashSet<string> guaranteedNames = ordered.Take(guaranteed).Select(t => t.CreatureType).ToHashSet(StringComparer.Ordinal);
        string? candidate = ordered.Count > guaranteed ? ordered[guaranteed].CreatureType : null;
        string? winner = candidate is not null && finalistSet is not null && finalistSet.Contains(candidate)
            ? candidate
            : null;
        HashSet<string> qualifiedNames = new(guaranteedNames, StringComparer.Ordinal);
        if (winner is not null)
        {
            qualifiedNames.Add(winner);
        }

        List<TypeCupTournamentTeam> teamRows = new(ordered.Count);
        foreach (TypeCupTeamStandingEntity team in ordered)
        {
            bool isGuaranteed = guaranteedNames.Contains(team.CreatureType);
            bool isWinner = winner is not null && string.Equals(team.CreatureType, winner, StringComparison.Ordinal);
            bool qualified = isGuaranteed || isWinner;
            string status = isGuaranteed ? "Guaranteed" : isWinner ? "Wildcard" : "Eliminated";
            teamRows.Add(new TypeCupTournamentTeam(
                team.CreatureType, team.TeamRank, team.TeamScoreThousandths, team.TeamBaseThousandths,
                ((TypeCupMedal)team.Medal).ToString(), qualified, status));
        }

        List<string> qualifiedList = qualifiedNames.OrderBy(t => t, StringComparer.Ordinal).ToList();
        List<string> eliminatedList = ordered.Select(t => t.CreatureType).Where(t => !qualifiedNames.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        _ = plan;
        return new TypeCupTournamentQualificationGroup(
            stage.QualificationGroup,
            stage.GroupSize,
            stage.FinalPlaces,
            checksum,
            teamRows,
            stageLegs.Select(l => new TypeCupTournamentLeg(l.SaveAthleteId, names.GetValueOrDefault(l.SaveAthleteId, $"Athlete {l.SaveAthleteId}"), l.CreatureType, l.SelectionRank, l.GroupNumber, l.GroupRank, l.GroupScoreThousandths, l.BaseScoreThousandths, qualifiedNames.Contains(l.CreatureType))).ToList(),
            qualifiedList,
            eliminatedList,
            guaranteed,
            candidate,
            winner);
    }

    private static TypeCupTournamentFinalResult BuildFinalResult(
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs,
        List<TypeCupTeamRoundEntity> rounds,
        Dictionary<int, string> names,
        string checksum)
    {
        var orderedTeams = teams.OrderBy(t => t.TeamRank).ToList();
        var first = rounds.OrderBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber).First();
        var firstDoc = TypeCupTeamRoundPayloadDocument.FromStored(first.PayloadJson);
        var last = rounds.OrderBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber).Last();
        var lastDoc = TypeCupTeamRoundPayloadDocument.FromStored(last.PayloadJson);
        var champion = orderedTeams.Single(t => t.TeamRank == 1);
        var finalists = orderedTeams.Select(t => t.CreatureType).ToHashSet(StringComparer.Ordinal);
        return new TypeCupTournamentFinalResult(
            orderedTeams.Count,
            checksum,
            firstDoc.RngBeforeState,
            firstDoc.RngBeforeStream,
            lastDoc.RngAfterState,
            lastDoc.RngAfterStream,
            champion.CreatureType,
            orderedTeams.Select(t => new TypeCupTournamentTeam(t.CreatureType, t.TeamRank, t.TeamScoreThousandths, t.TeamBaseThousandths, ((TypeCupMedal)t.Medal).ToString(), true, "Qualified")).ToList(),
            legs.Select(l => new TypeCupTournamentLeg(l.SaveAthleteId, names.GetValueOrDefault(l.SaveAthleteId, $"Athlete {l.SaveAthleteId}"), l.CreatureType, l.SelectionRank, l.GroupNumber, l.GroupRank, l.GroupScoreThousandths, l.BaseScoreThousandths, finalists.Contains(l.CreatureType))).ToList());
    }

    private static IReadOnlyList<SimulationKernel.Cups.TeamEvent.TeamRanked> ToRanked(
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs)
    {
        // Rebuild minimal ranked inputs for checksum: checksum covers only
        // rank/type/score/base, so legs are not needed beyond team rows.
        // Construct via reflection-free minimal records.
        List<SimulationKernel.Cups.TeamEvent.TeamRanked> ranked = new(teams.Count);
        foreach (var team in teams.OrderBy(t => t.TeamRank))
        {
            ranked.Add(new SimulationKernel.Cups.TeamEvent.TeamRanked(0, team.CreatureType, team.TeamRank, team.TeamScoreThousandths, team.TeamBaseThousandths, team.GroupWins, team.RoundWins, [0], [0]));
        }

        return ranked;
    }
}
