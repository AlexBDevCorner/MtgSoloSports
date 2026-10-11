using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Single authoritative qualifier progress evaluation (MSS-075).
/// See <see cref="QualifierProgress"/> for the model.
/// </summary>
public static class QualifierProgressEvaluator
{
    public static int ExpectedEvents(RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.OrdinaryQualifierEventCount;
    }

    public static (int Standings, int Rounds) ExpectedTotals(RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.FeederDivisionsPerColor != 3)
        {
            return (rules.QualifierSize, rules.QualifierRounds);
        }

        int standings = rules.QualifierSize + (8 * rules.FeederQualifierSize) + (8 * rules.FeederQualifierSize);
        int rounds = rules.QualifierRounds + (8 * rules.FeederQualifierRounds) + (8 * rules.FeederQualifierRounds);
        return (standings, rounds);
    }

    public static int ExpectedFieldSize(QualifierBoundary boundary, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return boundary == QualifierBoundary.Superleague ? rules.QualifierSize : rules.FeederQualifierSize;
    }

    public static int ExpectedRoundsPerEvent(RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.QualifierRounds;
    }

    public static int ExpectedWinners(RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.QualifierWinners;
    }

    /// <summary>
    /// Loads authoritative progress for one season transition. Validates each
    /// canonical event: pending, in-progress or complete. Corruption throws.
    /// </summary>
    public static async Task<QualifierProgress> LoadAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(rules);

        int totalEvents = ExpectedEvents(rules);
        (int expectedStandings, int expectedRounds) = ExpectedTotals(rules);

        List<QualifierStandingEntity> standings = await context.QualifierStandings.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<QualifierRoundEntity> rounds = await context.QualifierRounds.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        int completedEvents = CountCompleted(
            standings, rounds, rules, source.SeasonNumber);

        return new QualifierProgress(
            totalEvents,
            completedEvents,
            expectedRounds,
            rounds.Count,
            expectedStandings,
            standings.Count);
    }

    internal static int CountCompleted(
        List<QualifierStandingEntity> standings,
        List<QualifierRoundEntity> rounds,
        RulesV1 rules,
        int sourceSeasonNumber)
    {
        int completedEvents = 0;
        foreach ((QualifierBoundary boundary, int? color) in QualifierIdentity.CanonicalOrder)
        {
            if (rules.FeederDivisionsPerColor != 3 && boundary != QualifierBoundary.Superleague)
            {
                continue;
            }

            int storageColor = QualifierIdentity.ToStorageColor(color);
            List<QualifierStandingEntity> eventStandings = standings
                .Where(s => s.QualifierBoundary == (int)boundary && s.QualifierSportingColor == storageColor)
                .ToList();
            List<QualifierRoundEntity> eventRounds = rounds
                .Where(r => r.QualifierBoundary == (int)boundary && r.QualifierSportingColor == storageColor)
                .ToList();
            if (IsEventComplete(eventStandings, eventRounds, boundary, rules, sourceSeasonNumber))
            {
                completedEvents++;
            }
            else
            {
                ValidateEventNotCorrupt(eventStandings, eventRounds, boundary, rules, sourceSeasonNumber);
            }
        }

        return completedEvents;
    }

    internal static bool IsEventComplete(
        List<QualifierStandingEntity> eventStandings,
        List<QualifierRoundEntity> eventRounds,
        QualifierBoundary boundary,
        RulesV1 rules,
        int sourceSeasonNumber)
    {
        int expectedSize = ExpectedFieldSize(boundary, rules);
        int expectedRounds = ExpectedRoundsPerEvent(rules);
        int expectedWinners = ExpectedWinners(rules);
        if (eventStandings.Count != expectedSize || eventRounds.Count != expectedRounds)
        {
            return false;
        }

        int qualified = eventStandings.Count(s => s.IsQualified);
        if (qualified != expectedWinners)
        {
            throw new InvalidOperationException(
                $"Qualifier {boundary} for Season {sourceSeasonNumber} has {qualified}/{expectedWinners} winners; sporting state is corrupt.");
        }

        return true;
    }

    internal static void ValidateEventNotCorrupt(
        List<QualifierStandingEntity> eventStandings,
        List<QualifierRoundEntity> eventRounds,
        QualifierBoundary boundary,
        RulesV1 rules,
        int sourceSeasonNumber)
    {
        int expectedSize = ExpectedFieldSize(boundary, rules);
        int expectedRounds = ExpectedRoundsPerEvent(rules);
        if (eventStandings.Count == 0)
        {
            ValidatePending(eventRounds, boundary, expectedRounds, sourceSeasonNumber);
            return;
        }

        if (eventRounds.Count != expectedRounds)
        {
            throw new InvalidOperationException(
                $"Qualifier {boundary} for Season {sourceSeasonNumber} has {eventStandings.Count} standings but {eventRounds.Count}/{expectedRounds} rounds; sporting state is corrupt. Completed results are preserved; rerunning would overwrite them.");
        }

        if (eventStandings.Count != expectedSize)
        {
            throw new InvalidOperationException(
                $"Qualifier {boundary} for Season {sourceSeasonNumber} has {eventStandings.Count}/{expectedSize} standings; sporting state is corrupt.");
        }
    }

    internal static void ValidatePending(
        List<QualifierRoundEntity> eventRounds,
        QualifierBoundary boundary,
        int expectedRounds,
        int sourceSeasonNumber)
    {
        if (eventRounds.Count is < 0 or > 16)
        {
            throw new InvalidOperationException(
                $"Qualifier {boundary} for Season {sourceSeasonNumber} has corrupt round count {eventRounds.Count}; sporting state is corrupt.");
        }

        if (eventRounds.Count == expectedRounds)
        {
            throw new InvalidOperationException(
                $"Qualifier {boundary} for Season {sourceSeasonNumber} has all {expectedRounds} rounds persisted but no standings; sporting state is corrupt.");
        }
    }
}
