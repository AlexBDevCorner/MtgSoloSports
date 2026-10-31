using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;

namespace MtgSoloSports.Features.Cups.SelectChaosCupField;

public sealed partial class SelectChaosCupFieldHandler
{
    private sealed record SelectionResult(
        List<ChaosCupEntrantEntity> Entrants,
        IReadOnlyList<IReadOnlyList<SelectChaosCupFieldSelection.EntrantCandidate>> Groups,
        Pcg32State RngBefore,
        Pcg32State RngAfter);

    private static async Task<SelectChaosCupFieldResponse> CreateFieldAsync(
        SaveDbContext context,
        Guid saveId,
        InitialState state,
        CancellationToken cancellationToken)
    {
        FieldInputs inputs = await LoadFieldInputsAsync(context, state.Source, state.Rules, cancellationToken).ConfigureAwait(false);
        SelectionResult selection = SelectField(inputs, state);
        EnsureBonusContextFrozen(context, selection.Entrants, state.Source, state.Rules, cancellationToken);
        return await PersistFieldAsync(context, saveId, state, inputs, selection, cancellationToken).ConfigureAwait(false);
    }

    private static SelectionResult SelectField(FieldInputs inputs, InitialState state)
    {
        Pcg32V1 rng = Pcg32V1.Restore(state.RngBefore);
        IReadOnlyDictionary<SportingColor, IReadOnlyList<SelectChaosCupFieldSelection.PoolCandidate>> invited =
            SelectChaosCupFieldSelection.SelectPoolInvites(inputs.PoolByColor, inputs.RequiredByColor, rng);
        List<ChaosCupEntrantEntity> entrants = BuildEntrants(state.Source, inputs, invited);
        ValidateEntrantField(entrants);
        IReadOnlyList<IReadOnlyList<SelectChaosCupFieldSelection.EntrantCandidate>> groups =
            BuildDrawGroups(entrants, inputs.NamesByAthlete, rng);
        Pcg32State rngAfter = rng.Snapshot();
        EnsureRngAdvanced(state.RngBefore, rngAfter);
        return new SelectionResult(entrants, groups, state.RngBefore, rngAfter);
    }

    private static void EnsureRngAdvanced(Pcg32State before, Pcg32State after)
    {
        if (after.State == before.State && after.Stream == before.Stream)
        {
            throw new InvalidOperationException("Chaos Cup field selection must advance the save RNG.");
        }
    }

    private static async Task<SelectChaosCupFieldResponse> PersistFieldAsync(
        SaveDbContext context,
        Guid saveId,
        InitialState state,
        FieldInputs inputs,
        SelectionResult selection,
        CancellationToken cancellationToken)
    {
        ChaosCupEditionEntity edition = BuildEdition(state.Source, state.Rules, selection.RngBefore, selection.RngAfter, selection.Entrants);
        List<ChaosCupDrawEntity> draws = BuildDrawRows(state.Source, state.Rules, selection.Groups, selection.RngBefore, selection.RngAfter);
        context.ChaosCupEditions.Add(edition);
        foreach (ChaosCupEntrantEntity entrant in selection.Entrants)
        {
            context.ChaosCupEntrants.Add(entrant);
        }

        foreach (ChaosCupDrawEntity draw in draws)
        {
            context.ChaosCupDraws.Add(draw);
        }

        context.ApplyRngState(selection.RngAfter);
        _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await LoadCreatedResponseAsync(context, saveId, state, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SelectChaosCupFieldResponse> LoadCreatedResponseAsync(
        SaveDbContext context,
        Guid saveId,
        InitialState state,
        CancellationToken cancellationToken)
    {
        List<ChaosCupEntrantEntity> entrants = await LoadEntrantsAsync(context, state.Source, cancellationToken).ConfigureAwait(false);
        List<ChaosCupDrawEntity> draws = await LoadStageDrawsAsync(context, state.Source, 0, cancellationToken).ConfigureAwait(false);
        ChaosCupEditionEntity edition = await context.ChaosCupEditions
            .AsNoTracking()
            .SingleAsync(e => e.SourceSeasonId == state.Source.Id, cancellationToken)
            .ConfigureAwait(false);
        ChaosCupInvariants.ValidateEdition(edition, state.Rules);
        ChaosCupInvariants.ValidateEntrants(state.Source, edition, entrants);
        ChaosCupInvariants.ValidateStageDraw(state.Source, edition, entrants, draws, 0);
        return MapResponse(saveId, state.Source, state.Rules, edition, entrants, draws);
    }

    internal static List<ChaosCupEntrantEntity> BuildEntrants(
        SeasonEntity source,
        FieldInputs inputs,
        IReadOnlyDictionary<SportingColor, IReadOnlyList<SelectChaosCupFieldSelection.PoolCandidate>> invited)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(invited);

        List<ChaosCupEntrantEntity> entrants = new(ChaosCupFormat.FieldSize);
        AddActives(source, inputs, entrants);
        AddInvited(source, inputs, invited, entrants);
        return entrants;
    }

    private static void AddActives(SeasonEntity source, FieldInputs inputs, List<ChaosCupEntrantEntity> entrants)
    {
        foreach (ActiveMember active in inputs.Actives.OrderBy(a => a.SaveAthleteId))
        {
            SportingColor color = inputs.ColorByAthlete[active.SaveAthleteId];
            (int kind, int division) = inputs.LeagueMeta[active.LeagueId];
            entrants.Add(new ChaosCupEntrantEntity
            {
                SourceSeasonId = source.Id,
                SourceSeasonNumber = source.SeasonNumber,
                SaveAthleteId = active.SaveAthleteId,
                SportingColor = (int)color,
                SourceLeagueId = active.LeagueId,
                SourceLeagueKind = kind,
                SourceFeederDivision = division,
                IsPoolInvite = false,
            });
        }
    }

    private static void AddInvited(
        SeasonEntity source,
        FieldInputs inputs,
        IReadOnlyDictionary<SportingColor, IReadOnlyList<SelectChaosCupFieldSelection.PoolCandidate>> invited,
        List<ChaosCupEntrantEntity> entrants)
    {
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            if (!invited.TryGetValue(color, out IReadOnlyList<SelectChaosCupFieldSelection.PoolCandidate>? list))
            {
                throw new InvalidOperationException($"Chaos Pool invitations miss sporting color {color}.");
            }

            foreach (SelectChaosCupFieldSelection.PoolCandidate candidate in list.OrderBy(c => c.SaveAthleteId))
            {
                entrants.Add(new ChaosCupEntrantEntity
                {
                    SourceSeasonId = source.Id,
                    SourceSeasonNumber = source.SeasonNumber,
                    SaveAthleteId = candidate.SaveAthleteId,
                    SportingColor = (int)color,
                    SourceLeagueId = null,
                    SourceLeagueKind = 0,
                    SourceFeederDivision = 0,
                    IsPoolInvite = true,
                });
            }
        }

        _ = inputs;
    }

    internal static void ValidateEntrantField(List<ChaosCupEntrantEntity> entrants)
    {
        ArgumentNullException.ThrowIfNull(entrants);
        List<ChaosCupFormat.ChaosEntrant> field = entrants
            .Select(e => new ChaosCupFormat.ChaosEntrant(e.SaveAthleteId, (SportingColor)e.SportingColor))
            .ToList();
        ChaosCupFormat.ValidateEntrantField(field);
    }

    internal static void EnsureBonusContextFrozen(
        SaveDbContext context,
        List<ChaosCupEntrantEntity> entrants,
        SeasonEntity source,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<int, int> seasonNumbers = LoadSeasonNumbers(context, source);
        int cupSeason = checked(seasonNumbers[source.Id] + 1);
        List<AdvanceRoundHandler.MemberRow> roster = BuildBonusRoster(entrants);
        List<StageStandingEntity> standings = LoadBonusStandings(context, roster, seasonNumbers, cupSeason);
        Dictionary<int, Bonus> bonuses = ComputeBonuses(roster, standings, seasonNumbers, cupSeason, rules);
        if (bonuses.Count != entrants.Count)
        {
            throw new InvalidOperationException("Chaos Cup bonus freeze misses entrants.");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static Dictionary<int, int> LoadSeasonNumbers(SaveDbContext context, SeasonEntity source)
    {
        Dictionary<int, int> seasonNumbers = context.Seasons
            .AsNoTracking()
            .ToDictionary(e => e.Id, e => e.SeasonNumber);
        if (!seasonNumbers.ContainsKey(source.Id))
        {
            throw new InvalidOperationException($"Save has no season row for season {source.SeasonNumber}.");
        }

        return seasonNumbers;
    }

    private static List<AdvanceRoundHandler.MemberRow> BuildBonusRoster(List<ChaosCupEntrantEntity> entrants)
    {
        return entrants
            .Select(e => new AdvanceRoundHandler.MemberRow(e.SaveAthleteId, $"Athlete {e.SaveAthleteId}", e.SportingColor))
            .ToList();
    }

    private static List<StageStandingEntity> LoadBonusStandings(
        SaveDbContext context,
        List<AdvanceRoundHandler.MemberRow> roster,
        Dictionary<int, int> seasonNumbers,
        int cupSeason)
    {
        var relevant = AdvanceRoundHandler.SelectBonusSeasonIds(seasonNumbers, cupSeason);
        HashSet<int> rosterIds = roster.Select(r => r.AthleteId).ToHashSet();
        return context.StageStandings
            .AsNoTracking()
            .Where(e => rosterIds.Contains(e.SaveAthleteId) && relevant.Contains(e.SeasonId))
            .ToList();
    }

    private static Dictionary<int, Bonus> ComputeBonuses(
        List<AdvanceRoundHandler.MemberRow> roster,
        List<StageStandingEntity> standings,
        Dictionary<int, int> seasonNumbers,
        int cupSeason,
        RulesV1 rules)
    {
        Dictionary<int, List<BonusContribution>> contributions = AdvanceRoundHandler.GroupBonusContributions(
            roster, standings, seasonNumbers, cupSeason);
        return AdvanceRoundHandler.ComputeStageStartBonuses(roster, contributions, cupSeason, 1, rules);
    }

    internal static IReadOnlyList<IReadOnlyList<SelectChaosCupFieldSelection.EntrantCandidate>> BuildDrawGroups(
        List<ChaosCupEntrantEntity> entrants,
        Dictionary<int, string> namesByAthlete,
        Pcg32V1 rng)
    {
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(namesByAthlete);
        ArgumentNullException.ThrowIfNull(rng);
        Dictionary<SportingColor, IReadOnlyList<SelectChaosCupFieldSelection.EntrantCandidate>> byColor =
            GroupByColor(entrants, namesByAthlete);
        return SelectChaosCupFieldSelection.BuildBalancedFirstDraw(byColor, rng);
    }

    private static Dictionary<SportingColor, IReadOnlyList<SelectChaosCupFieldSelection.EntrantCandidate>> GroupByColor(
        List<ChaosCupEntrantEntity> entrants,
        Dictionary<int, string> namesByAthlete)
    {
        Dictionary<SportingColor, List<SelectChaosCupFieldSelection.EntrantCandidate>> grouped = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            grouped[color] = [];
        }

        foreach (ChaosCupEntrantEntity entrant in entrants)
        {
            SportingColor color = (SportingColor)entrant.SportingColor;
            if (!namesByAthlete.TryGetValue(entrant.SaveAthleteId, out string? name))
            {
                throw new InvalidOperationException($"Chaos field references unknown athlete {entrant.SaveAthleteId}.");
            }

            grouped[color].Add(new SelectChaosCupFieldSelection.EntrantCandidate(
                entrant.SaveAthleteId, name, color, entrant.SourceLeagueId,
                entrant.SourceLeagueKind, entrant.SourceFeederDivision, entrant.IsPoolInvite));
        }

        return grouped.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SelectChaosCupFieldSelection.EntrantCandidate>)kv.Value);
    }

    internal static ChaosCupEditionEntity BuildEdition(
        SeasonEntity source,
        RulesV1 rules,
        Pcg32State rngBefore,
        Pcg32State rngAfter,
        List<ChaosCupEntrantEntity> entrants)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(entrants);
        ChaosCupEditionEntity edition = NewEdition(source, rules, rngBefore, rngAfter, entrants);
        ChaosCupInvariants.ValidateEdition(edition, rules);
        ChaosCupInvariants.ValidateEntrants(source, edition, entrants);
        return edition;
    }

    private static ChaosCupEditionEntity NewEdition(
        SeasonEntity source,
        RulesV1 rules,
        Pcg32State rngBefore,
        Pcg32State rngAfter,
        List<ChaosCupEntrantEntity> entrants)
    {
        return new ChaosCupEditionEntity
        {
            SourceSeasonId = source.Id,
            SourceSeasonNumber = source.SeasonNumber,
            RulesVersion = rules.Version,
            ChaosFormatVersion = ChaosCupFormat.ChaosFormatVersion,
            FieldSize = ChaosCupFormat.FieldSize,
            RngBeforeState = unchecked((long)rngBefore.State),
            RngBeforeStream = unchecked((long)rngBefore.Stream),
            RngAfterState = unchecked((long)rngAfter.State),
            RngAfterStream = unchecked((long)rngAfter.Stream),
            EditionChecksum = ChaosCupInvariants.ComputeEditionChecksum(entrants),
        };
    }

    internal static List<ChaosCupDrawEntity> BuildDrawRows(
        SeasonEntity source,
        RulesV1 rules,
        IReadOnlyList<IReadOnlyList<SelectChaosCupFieldSelection.EntrantCandidate>> groups,
        Pcg32State rngBefore,
        Pcg32State rngAfter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(groups);
        EnsureGroupCount(groups);
        List<ChaosCupDrawEntity> rows = new(ChaosCupFormat.FieldSize);
        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            AddGroupRows(source, rules, groups[groupIndex], groupIndex, rngBefore, rngAfter, rows);
        }

        string checksum = ChaosCupInvariants.ComputeStageDrawChecksum(0, rows);
        foreach (ChaosCupDrawEntity row in rows)
        {
            row.DrawChecksum = checksum;
        }

        return rows;
    }

    private static void EnsureGroupCount(IReadOnlyList<IReadOnlyList<SelectChaosCupFieldSelection.EntrantCandidate>> groups)
    {
        if (groups.Count != ChaosCupFormat.GroupCountForStage(0))
        {
            throw new InvalidOperationException(
                $"Chaos first draw must hold exactly {ChaosCupFormat.GroupCountForStage(0)} groups, was {groups.Count}.");
        }
    }

    private static void AddGroupRows(
        SeasonEntity source,
        RulesV1 rules,
        IReadOnlyList<SelectChaosCupFieldSelection.EntrantCandidate> group,
        int groupIndex,
        Pcg32State rngBefore,
        Pcg32State rngAfter,
        List<ChaosCupDrawEntity> rows)
    {
        if (group.Count != ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException(
                $"Chaos first-stage group {groupIndex + 1} must hold exactly {ChaosCupFormat.GroupSize} athletes, was {group.Count}.");
        }

        for (int drawIndex = 0; drawIndex < group.Count; drawIndex++)
        {
            SelectChaosCupFieldSelection.EntrantCandidate member = group[drawIndex];
            rows.Add(NewDrawRow(source, rules, groupIndex, drawIndex, member, rngBefore, rngAfter));
        }
    }

    private static ChaosCupDrawEntity NewDrawRow(
        SeasonEntity source,
        RulesV1 rules,
        int groupIndex,
        int drawIndex,
        SelectChaosCupFieldSelection.EntrantCandidate member,
        Pcg32State rngBefore,
        Pcg32State rngAfter)
    {
        return new ChaosCupDrawEntity
        {
            SourceSeasonId = source.Id,
            SourceSeasonNumber = source.SeasonNumber,
            StageIndex = 0,
            GroupNumber = groupIndex + 1,
            SaveAthleteId = member.SaveAthleteId,
            DrawIndex = drawIndex,
            GroupSize = ChaosCupFormat.GroupSize,
            StageGroupCount = ChaosCupFormat.GroupCountForStage(0),
            RulesVersion = rules.Version,
            ChaosFormatVersion = ChaosCupFormat.ChaosFormatVersion,
            RngBeforeState = unchecked((long)rngBefore.State),
            RngBeforeStream = unchecked((long)rngBefore.Stream),
            RngAfterState = unchecked((long)rngAfter.State),
            RngAfterStream = unchecked((long)rngAfter.Stream),
            DrawChecksum = string.Empty,
        };
    }

    internal static async Task<List<ChaosCupEntrantEntity>> LoadEntrantsAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        return await context.ChaosCupEntrants
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<List<ChaosCupDrawEntity>> LoadStageDrawsAsync(
        SaveDbContext context,
        SeasonEntity source,
        int stageIndex,
        CancellationToken cancellationToken)
    {
        return await context.ChaosCupDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<SelectChaosCupFieldResponse> LoadPersistedResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ChaosCupEditionEntity? edition = await context.ChaosCupEditions
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (edition is null)
        {
            throw new SelectChaosCupFieldConflictException(
                $"Chaos Cup field for Season {source.SeasonNumber} has not been selected yet.");
        }

        List<ChaosCupEntrantEntity> entrants = await LoadEntrantsAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<ChaosCupDrawEntity> draws = await LoadStageDrawsAsync(context, source, 0, cancellationToken).ConfigureAwait(false);
        ChaosCupInvariants.ValidateEdition(edition, rules);
        ChaosCupInvariants.ValidateEntrants(source, edition, entrants);
        ChaosCupInvariants.ValidateStageDraw(source, edition, entrants, draws, 0);
        return MapResponse(saveId, source, rules, edition, entrants, draws);
    }

    internal static SelectChaosCupFieldResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        ChaosCupEditionEntity edition,
        List<ChaosCupEntrantEntity> entrants,
        List<ChaosCupDrawEntity> draws)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(draws);
        return NewResponse(saveId, source, rules, edition, entrants, draws);
    }

    private static SelectChaosCupFieldResponse NewResponse(
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        ChaosCupEditionEntity edition,
        List<ChaosCupEntrantEntity> entrants,
        List<ChaosCupDrawEntity> draws)
    {
        int active = entrants.Count(e => !e.IsPoolInvite);
        int invited = entrants.Count(e => e.IsPoolInvite);
        List<SelectChaosCupFieldResponse.PerColorQuota> perColor = BuildQuotas(entrants);
        List<SelectChaosCupFieldResponse.DrawGroup> groups = BuildGroups(draws);
        return new SelectChaosCupFieldResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            rules.Version,
            ChaosCupFormat.ChaosFormatVersion,
            ChaosCupFormat.FieldSize,
            active,
            invited,
            perColor,
            edition.EditionChecksum,
            draws.Count == 0 ? string.Empty : draws[0].DrawChecksum,
            unchecked((ulong)edition.RngBeforeState),
            unchecked((ulong)edition.RngBeforeStream),
            unchecked((ulong)edition.RngAfterState),
            unchecked((ulong)edition.RngAfterStream),
            groups);
    }

    private static List<SelectChaosCupFieldResponse.PerColorQuota> BuildQuotas(List<ChaosCupEntrantEntity> entrants)
    {
        List<SelectChaosCupFieldResponse.PerColorQuota> perColor = new(8);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            int colorActive = entrants.Count(e => e.SportingColor == (int)color && !e.IsPoolInvite);
            int colorInvited = entrants.Count(e => e.SportingColor == (int)color && e.IsPoolInvite);
            perColor.Add(new SelectChaosCupFieldResponse.PerColorQuota(
                (int)color, color.ToString(), colorActive, colorInvited));
        }

        return perColor;
    }

    private static List<SelectChaosCupFieldResponse.DrawGroup> BuildGroups(List<ChaosCupDrawEntity> draws)
    {
        return draws
            .GroupBy(d => d.GroupNumber)
            .OrderBy(g => g.Key)
            .Select(g => new SelectChaosCupFieldResponse.DrawGroup(
                g.Key,
                g.OrderBy(d => d.DrawIndex).Select(d => d.SaveAthleteId).ToList()))
            .ToList();
    }
}
