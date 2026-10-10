using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Seasons.SeasonLifecycle;

/// <summary>
/// Catalog of postseason events that are played in rounds (qualifier and Cup
/// events): stable keys, titles, shapes from the versioned rules, the round
/// cursor and the in-progress RNG chain validation shared by every event's
/// step and one-shot paths. Pure: no persistence, no randomness consumed.
/// </summary>
public static class PostseasonEvents
{
    public const string Qualifier = "qualifier";
    public const string ColorCupIndividual = "color-cup-individual";
    public const string ColorCupTeam = "color-cup-team";
    public const string TypeCupTeam = "type-cup-team";

    public static readonly IReadOnlyList<string> All = [Qualifier, ColorCupIndividual, ColorCupTeam, TypeCupTeam];

    public sealed record EventShape(int GroupCount, int RoundsPerGroup)
    {
        public int TotalRounds => GroupCount * RoundsPerGroup;

        public bool IsGrouped => GroupCount > 1;
    }

    public sealed record PlayedRoundLink(int Group, int Round, Pcg32State Before, Pcg32State After);

    /// <summary>
    /// Progress of the round-based event that is the next legal lifecycle
    /// action. <see cref="Group"/>/<see cref="RoundInGroup"/> are the rank
    /// group/round of the next round to play (null for single-stage events).
    /// Type Cup tournament stages add <see cref="TournamentPhase"/>,
    /// <see cref="QualificationGroup"/>, <see cref="QualificationGroupCount"/>
    /// and <see cref="TournamentStage"/> (for example "Qualification Group 2
    /// of 3" or "Final"); older callers ignore the additive fields.
    /// </summary>
    public sealed record SeasonEventProgress(
        string Event,
        int SourceSeasonNumber,
        int RoundsPlayed,
        int TotalRounds,
        int GroupCount,
        int RoundsPerGroup,
        int? Group,
        int? RoundInGroup,
        int? TournamentPhase = null,
        int? QualificationGroup = null,
        int? QualificationGroupCount = null,
        string? TournamentStage = null);

    public static bool IsKnown(string? key) => key is not null && All.Contains(key, StringComparer.Ordinal);

    public static string Title(string key) => key switch
    {
        Qualifier => "Superleague qualifier",
        ColorCupIndividual => "Color Cup — individual",
        ColorCupTeam => "Color Cup — team",
        TypeCupTeam => "Type Cup — team",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown postseason event."),
    };

    public static string? KeyForAction(string action) => action switch
    {
        SeasonLifecycleActions.RunQualifier => Qualifier,
        SeasonLifecycleActions.RunColorCupIndividual => ColorCupIndividual,
        SeasonLifecycleActions.RunColorCupTeam => ColorCupTeam,
        SeasonLifecycleActions.RunTypeCupTeam => TypeCupTeam,
        _ => null,
    };

    public static string InProgressPhase(string key) => key switch
    {
        Qualifier => "QualifierInProgress",
        ColorCupIndividual => "ColorCupIndividualInProgress",
        ColorCupTeam => "ColorCupTeamInProgress",
        TypeCupTeam => "TypeCupTeamInProgress",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown postseason event."),
    };

    public static EventShape Shape(string key, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return key switch
        {
            Qualifier => QualifierShape(rules),
            ColorCupIndividual => new EventShape(1, rules.ColorCupIndividualRounds),
            ColorCupTeam => new EventShape(rules.ColorCupTeamSize, rules.ColorCupTeamGroupRounds),
            TypeCupTeam => new EventShape(rules.TypeCupMinTeamSize, rules.TypeCupGroupRounds),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown postseason event."),
        };
    }

    internal static EventShape QualifierShape(RulesV1 rules)
    {
        if (rules.FeederDivisionsPerColor != 3)
        {
            return new EventShape(1, rules.QualifierRounds);
        }

        int totalRounds = rules.QualifierRounds
            + (8 * rules.FeederQualifierRounds)
            + (8 * rules.FeederQualifierRounds);
        return new EventShape(1, totalRounds);
    }

    /// <summary>Group and round (both 1-based) of the next round after <paramref name="played"/> rounds.</summary>
    public static (int Group, int Round) Cursor(int played, EventShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentOutOfRangeException.ThrowIfNegative(played);
        return (played / shape.RoundsPerGroup + 1, played % shape.RoundsPerGroup + 1);
    }

    /// <summary>
    /// Validates a partly played event; throws <see cref="InvalidOperationException"/>
    /// on any corruption. Rounds must be contiguous in (group, round) order and
    /// fewer than the total; each round must start where the previous one
    /// ended, or — at a group boundary — where <paramref name="afterGroup"/>
    /// says the previous group's tie-break left the RNG; the save's RNG row must
    /// equal the state after the last persisted step, proving nothing else
    /// consumed randomness mid-event.
    /// </summary>
    public static void ValidateInProgress(
        string key,
        IReadOnlyList<PlayedRoundLink> played,
        EventShape shape,
        Func<int, Pcg32State> afterGroup,
        Pcg32State rngRow)
    {
        ArgumentNullException.ThrowIfNull(played);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(afterGroup);
        string title = Title(key);
        if (played.Count >= shape.TotalRounds)
        {
            throw new InvalidOperationException($"{title} has all {shape.TotalRounds} rounds persisted but no results; sporting state is corrupt.");
        }

        for (int index = 0; index < played.Count; index++)
        {
            PlayedRoundLink link = played[index];
            (int group, int round) = Cursor(index, shape);
            if (link.Group != group || link.Round != round)
            {
                throw new InvalidOperationException(
                    $"{title} round sequence is corrupt: expected group {group} round {round}, found group {link.Group} round {link.Round}.");
            }

            if (index == 0)
            {
                continue;
            }

            Pcg32State expectedBefore = round == 1 ? afterGroup(group - 1) : played[index - 1].After;
            if (link.Before != expectedBefore)
            {
                throw new InvalidOperationException($"{title} RNG chain is broken before group {group} round {round}.");
            }
        }

        if (played.Count == 0)
        {
            return;
        }

        PlayedRoundLink last = played[^1];
        Pcg32State expectedRow = last.Round == shape.RoundsPerGroup && last.Group < shape.GroupCount
            ? afterGroup(last.Group)
            : last.After;
        if (rngRow != expectedRow)
        {
            throw new InvalidOperationException($"{title} is in progress but the save RNG moved since its last round; sporting state is corrupt.");
        }
    }

    /// <summary>
    /// Adds event progress (and the computed in-progress phase once at least
    /// one round is stored) when the next legal action is a round-based event.
    /// Read-only; never repairs.
    /// </summary>
    public static async Task<SeasonLifecycleSnapshot> WithEventProgressAsync(
        SaveDbContext context,
        SeasonLifecycleSnapshot snapshot,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rules);
        string? key = snapshot.LegalNextActions.Count > 0 ? KeyForAction(snapshot.LegalNextActions[0]) : null;
        if (key is null || snapshot.SourceSeasonNumber is not int sourceNumber)
        {
            return snapshot;
        }

        SeasonEntity source = await context.Seasons.AsNoTracking()
            .SingleAsync(e => e.SeasonNumber == sourceNumber, cancellationToken).ConfigureAwait(false);
        if (string.Equals(key, TypeCupTeam, StringComparison.Ordinal))
        {
            return await WithTypeCupProgressAsync(context, snapshot, source, rules, cancellationToken).ConfigureAwait(false);
        }

        int played = key switch
        {
            Qualifier => await CountQualifierRoundsAsync(context, source, cancellationToken).ConfigureAwait(false),
            ColorCupIndividual => await context.ColorCupIndividualRounds.CountAsync(e => e.SourceSeasonId == source.Id, cancellationToken).ConfigureAwait(false),
            _ => await context.ColorCupTeamRounds.CountAsync(e => e.SourceSeasonId == source.Id, cancellationToken).ConfigureAwait(false),
        };
        EventShape shape = Shape(key, rules);
        (int group, int round) = Cursor(Math.Min(played, shape.TotalRounds - 1), shape);
        SeasonEventProgress progress = new(
            key,
            sourceNumber,
            played,
            shape.TotalRounds,
            shape.GroupCount,
            shape.RoundsPerGroup,
            shape.IsGrouped ? group : null,
            shape.IsGrouped ? round : null);
        return snapshot with
        {
            EventProgress = progress,
            ComputedPhase = played > 0 ? InProgressPhase(key) : snapshot.ComputedPhase,
        };
    }

    private static async Task<SeasonLifecycleSnapshot> WithTypeCupProgressAsync(
        SaveDbContext context,
        SeasonLifecycleSnapshot snapshot,
        SeasonEntity source,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        int played = await context.TypeCupTeamRounds.CountAsync(e => e.SourceSeasonId == source.Id, cancellationToken).ConfigureAwait(false);
        (int total, int group, int round, int? phase, int? qual, int? qualCount, string? stage) =
            await TypeCupProgressAsync(context, source, played, rules, cancellationToken).ConfigureAwait(false);
        EventShape shape = Shape(TypeCupTeam, rules);
        SeasonEventProgress progress = new(
            TypeCupTeam,
            snapshot.SourceSeasonNumber!.Value,
            played,
            total,
            shape.GroupCount,
            shape.RoundsPerGroup,
            group,
            round,
            phase,
            qual,
            qualCount,
            stage);
        return snapshot with
        {
            EventProgress = progress,
            ComputedPhase = played > 0 ? InProgressPhase(TypeCupTeam) : snapshot.ComputedPhase,
        };
    }

    private static async Task<(int Total, int Group, int Round, int? Phase, int? Qual, int? QualCount, string? Stage)> TypeCupProgressAsync(
        SaveDbContext context,
        SeasonEntity source,
        int played,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        EventShape shape = Shape(TypeCupTeam, rules);
        List<TypeCupSelectionEntity> selection = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (selection.Count == 0)
        {
            return FallbackTypeCupProgress(played, shape);
        }

        int teamCount = selection.Count / rules.TypeCupMinTeamSize;
        List<TypeCupTournamentDrawEntity> draws = await context.TypeCupTournamentDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (PendingTournamentProgress(source, played, teamCount, draws.Count, rules) is var pending && pending.Handled)
        {
            return pending.Progress;
        }

        try
        {
            var plan = Cups.RunTypeCupTeam.TypeCupTournamentPlan.BuildFromSelection(
                teamCount,
                selection.Select(e => e.CreatureType).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList(),
                draws,
                rules);
            int total = plan.TotalRounds;
            if (played >= total)
            {
                (int g, int r) = Cursor(total - 1, shape);
                return (total, g, r, null, null, PlanQualCount(plan), "Complete");
            }

            var cursor = Cups.RunTypeCupTeam.TypeCupTournamentPlan.Cursor(played, plan, rules);
            return (total, cursor.RankGroup, cursor.Round, cursor.Stage.Phase, cursor.Stage.QualificationGroup, PlanQualCount(plan), StageLabel(plan, cursor.Stage));
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is ArgumentOutOfRangeException)
        {
            return FallbackTypeCupProgress(played, shape);
        }
    }

    /// <summary>
    /// Prospective progress for a healthy pending tournament: squads are
    /// selected but the qualification draw is auto-created on first play, so
    /// no draw rows exist yet. Balanced group sizes and Final quotas are pure
    /// math on the team count (only the group assignment needs the draw RNG),
    /// so the read model can report the tournament without throwing a
    /// mutation-path conflict. Rounds persisted without a draw are corruption
    /// and throw; callers must not swallow that as a fallback.
    /// </summary>
    private static (bool Handled, (int Total, int Group, int Round, int? Phase, int? Qual, int? QualCount, string? Stage) Progress)
        PendingTournamentProgress(SeasonEntity source, int played, int teamCount, int drawCount, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        if (drawCount != 0
            || rules.TypeCupTournamentFormatVersion == RulesV1.LegacyTypeCupTournamentFormatVersion
            || SimulationKernel.Cups.TypeCupTournamentFormat.IsDirectFinal(teamCount, rules))
        {
            return (false, default);
        }

        if (played != 0)
        {
            throw new InvalidOperationException(
                $"Type Cup for Season {source.SeasonNumber} has {played} rounds persisted without a qualification draw; sporting state is corrupt.");
        }

        int groupCount = SimulationKernel.Cups.TypeCupTournamentFormat.QualificationGroupCount(teamCount, rules);
        int perStage = rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds;
        int prospectiveTotal = checked((groupCount + 1) * perStage);
        return (true, (prospectiveTotal, 1, 1,
            (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification,
            1, groupCount, $"Qualification Group 1 of {groupCount}"));
    }

    private static (int Total, int Group, int Round, int? Phase, int? Qual, int? QualCount, string? Stage) FallbackTypeCupProgress(
        int played,
        EventShape shape)
    {
        (int group, int round) = Cursor(Math.Min(played, shape.TotalRounds - 1), shape);
        return (shape.TotalRounds, group, round, null, null, null, null);
    }

    private static int? PlanQualCount(Cups.RunTypeCupTeam.TypeCupTournamentPlan.Plan plan) =>
        plan.IsTournament ? plan.QualificationGroupCount : null;

    private static string StageLabel(Cups.RunTypeCupTeam.TypeCupTournamentPlan.Plan plan, Cups.RunTypeCupTeam.TypeCupTournamentPlan.StageKey stage)
    {
        if (plan.IsLegacy)
        {
            return "Single field";
        }

        if (plan.IsDirectFinal)
        {
            return "Final";
        }

        if (stage.Phase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final)
        {
            return "Final";
        }

        return $"Qualification Group {stage.QualificationGroup} of {plan.QualificationGroupCount}";
    }

    /// <summary>
    /// Title of a partly played postseason event other than <paramref name="key"/>
    /// for <paramref name="sourceSeasonId"/>, or null. A partly played event
    /// owns the save RNG until it completes: playing anything else in between
    /// would move the RNG under it and leave it unable to resume.
    /// </summary>
    public static async Task<string?> FindOtherInProgressAsync(
        SaveDbContext context,
        string key,
        int sourceSeasonId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        int? qualifier = Except(key, Qualifier, sourceSeasonId);
        int? individual = Except(key, ColorCupIndividual, sourceSeasonId);
        int? colorTeam = Except(key, ColorCupTeam, sourceSeasonId);
        int? typeTeam = Except(key, TypeCupTeam, sourceSeasonId);

        if (await HasQualifierPartialAsync(context, qualifier, cancellationToken).ConfigureAwait(false))
        {
            return Title(Qualifier);
        }

        if (await context.ColorCupIndividualRounds.AnyAsync(
                r => r.SourceSeasonId != individual && !context.ColorCupIndividualStandings.Any(s => s.SourceSeasonId == r.SourceSeasonId),
                cancellationToken).ConfigureAwait(false))
        {
            return Title(ColorCupIndividual);
        }

        if (await context.ColorCupTeamRounds.AnyAsync(
                r => r.SourceSeasonId != colorTeam && !context.ColorCupTeamStandings.Any(s => s.SourceSeasonId == r.SourceSeasonId),
                cancellationToken).ConfigureAwait(false))
        {
            return Title(ColorCupTeam);
        }

        return await HasTypeCupPartialAsync(context, typeTeam, cancellationToken).ConfigureAwait(false)
            ? Title(TypeCupTeam)
            : null;
    }

    /// <summary>
    /// A Type Cup tournament owns the RNG while any of its rounds are persisted
    /// without Final (or legacy) standings. Qualification standings alone do
    /// not complete the Cup: the Final must still run.
    /// </summary>
    internal static async Task<bool> HasTypeCupPartialAsync(
        SaveDbContext context,
        int? excludedSourceSeasonId,
        CancellationToken cancellationToken)
    {
        List<int> seasons = await context.TypeCupTeamRounds
            .AsNoTracking()
            .Where(r => r.SourceSeasonId != excludedSourceSeasonId)
            .Select(r => r.SourceSeasonId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (int seasonId in seasons)
        {
            bool complete = await context.TypeCupTeamStandings.AnyAsync(
                s => s.SourceSeasonId == seasonId
                    && (s.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                        || s.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final),
                cancellationToken).ConfigureAwait(false);
            if (!complete)
            {
                return true;
            }
        }

        return false;
    }

    private static int? Except(string key, string candidate, int sourceSeasonId) =>
        string.Equals(key, candidate, StringComparison.Ordinal) ? sourceSeasonId : null;

    internal static async Task<bool> HasQualifierPartialAsync(
        SaveDbContext context, int? excludedFromSeasonId, CancellationToken cancellationToken)
    {
        List<QualifierRoundEntity> rounds = await context.QualifierRounds
            .AsNoTracking()
            .Where(r => r.FromSeasonId != excludedFromSeasonId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (QualifierRoundEntity round in rounds)
        {
            bool hasStandings = await context.QualifierStandings.AnyAsync(
                s => s.FromSeasonId == round.FromSeasonId
                    && s.ToSeasonId == round.ToSeasonId
                    && s.QualifierBoundary == round.QualifierBoundary
                    && s.QualifierSportingColor == round.QualifierSportingColor,
                cancellationToken).ConfigureAwait(false);
            if (!hasStandings)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<int> CountQualifierRoundsAsync(SaveDbContext context, SeasonEntity source, CancellationToken cancellationToken)
    {
        SeasonEntity? next = await context.Seasons.AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonNumber == source.SeasonNumber + 1, cancellationToken).ConfigureAwait(false);
        return next is null
            ? 0
            : await context.QualifierRounds.CountAsync(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
    }
}
