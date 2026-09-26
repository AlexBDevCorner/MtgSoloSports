using System.Security.Cryptography;
using System.Text;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Seasons;

namespace MtgSoloSports.Features.Simulation.SeasonCompletion;

/// <summary>
/// Structural invariants for a finalized season. Fundamental failures throw
/// and abort the mutation; corrupted sporting state is never silently repaired.
/// </summary>
public static class SeasonInvariants
{
    /// <summary>
    /// Validates freshly ranked season standings before commit: exactly 32
    /// athletes, ranks 1..32 exactly once, unique athletes, totals matching
    /// the accumulated inputs, exactly one champion at rank 1, and place-count
    /// vectors consistent with a complete 32-stage season.
    /// </summary>
    public static void ValidateFinalSeason(
        IReadOnlyList<SeasonRankedAthlete> ranked,
        IReadOnlyList<SeasonAthleteTotals> totals,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(rules);

        if (ranked.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Final season must rank exactly {rules.LeagueSize} athletes, was {ranked.Count}.");
        }

        if (totals.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Final season must total exactly {rules.LeagueSize} athletes, was {totals.Count}.");
        }

        Dictionary<int, SeasonAthleteTotals> totalsById = totals.ToDictionary(t => t.AthleteId);
        HashSet<int> ranks = new();
        HashSet<int> athleteIds = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (SeasonRankedAthlete entry in ranked)
        {
            CheckStandingIdentity(entry, ranks, athleteIds, names, rules);
            CheckStandingTotals(entry, totalsById, rules);
        }

        if (!ranks.SetEquals(Enumerable.Range(1, rules.LeagueSize)))
        {
            throw new InvalidOperationException("Final season must cover ranks 1..32 exactly once.");
        }

        CheckChampion(ranked);
        CheckCompleteCoverage(totals, rules);
    }

    /// <summary>
    /// Fingerprints season standings via the kernel checksum.
    /// </summary>
    public static string ComputeChecksum(IReadOnlyList<SeasonRankedAthlete> ranked)
        => SeasonCalculator.ComputeChecksum(ranked);

    private static void CheckStandingIdentity(
        SeasonRankedAthlete entry,
        HashSet<int> ranks,
        HashSet<int> athleteIds,
        HashSet<string> names,
        RulesV1 rules)
    {
        if (entry.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Final season contains invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Final season contains an athlete with an empty name.");
        }

        if (entry.SeasonRank < 1 || entry.SeasonRank > rules.LeagueSize)
        {
            throw new InvalidOperationException($"Final season rank {entry.SeasonRank} is out of range.");
        }

        if (!ranks.Add(entry.SeasonRank))
        {
            throw new InvalidOperationException($"Final season contains duplicate rank {entry.SeasonRank}.");
        }

        if (!athleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Final season contains duplicate athlete id {entry.AthleteId}.");
        }

        if (!names.Add(entry.Name))
        {
            throw new InvalidOperationException($"Final season contains duplicate athlete '{entry.Name}'.");
        }
    }

    private static void CheckStandingTotals(
        SeasonRankedAthlete entry,
        Dictionary<int, SeasonAthleteTotals> totalsById,
        RulesV1 rules)
    {
        if (!totalsById.TryGetValue(entry.AthleteId, out SeasonAthleteTotals? totals))
        {
            throw new InvalidOperationException($"Final season standing for '{entry.Name}' has no accumulated totals.");
        }

        CheckTotalsMatch(entry, totals);
        CheckVectorsMatch(entry, totals, rules);
    }

    private static void CheckTotalsMatch(SeasonRankedAthlete entry, SeasonAthleteTotals totals)
    {
        if (!string.Equals(totals.Name, entry.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Final season standing for id {entry.AthleteId} has inconsistent names.");
        }

        if (entry.TotalChampionshipPointsThousandths != totals.TotalChampionshipPointsThousandths)
        {
            throw new InvalidOperationException($"Final season championship total for '{entry.Name}' does not match stage points.");
        }

        if (entry.TotalStageScoreThousandths != totals.TotalStageScoreThousandths)
        {
            throw new InvalidOperationException($"Final season stage score for '{entry.Name}' does not match accumulated finals.");
        }

        if (entry.TotalBaseScoreThousandths != totals.TotalBaseScoreThousandths)
        {
            throw new InvalidOperationException($"Final season base score for '{entry.Name}' does not match accumulated base points.");
        }
    }

    private static void CheckVectorsMatch(
        SeasonRankedAthlete entry,
        SeasonAthleteTotals totals,
        RulesV1 rules)
    {
        if (entry.StagePlaceCounts.Count != rules.LeagueSize || entry.RoundPlaceCounts.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException($"Final season place counts for '{entry.Name}' do not match accumulated totals.");
        }

        for (int i = 0; i < entry.StagePlaceCounts.Count; i++)
        {
            if (entry.StagePlaceCounts[i] != totals.StagePlaceCounts[i])
            {
                throw new InvalidOperationException($"Final season stage-place counts for '{entry.Name}' do not match accumulated totals.");
            }
        }

        for (int i = 0; i < entry.RoundPlaceCounts.Count; i++)
        {
            if (entry.RoundPlaceCounts[i] != totals.RoundPlaceCounts[i])
            {
                throw new InvalidOperationException($"Final season round-place counts for '{entry.Name}' do not match accumulated totals.");
            }
        }

        if (entry.StageWins != totals.StageWins || entry.RoundWins != totals.RoundWins)
        {
            throw new InvalidOperationException($"Final season wins for '{entry.Name}' do not match placement counts.");
        }

        bool expectedChampion = entry.SeasonRank == 1;
        if (entry.IsChampion != expectedChampion)
        {
            throw new InvalidOperationException($"Final season champion flag for '{entry.Name}' must be {expectedChampion}.");
        }
    }

    private static void CheckChampion(IReadOnlyList<SeasonRankedAthlete> ranked)
    {
        int champions = ranked.Count(r => r.IsChampion);
        if (champions != 1)
        {
            throw new InvalidOperationException($"Final season must have exactly one champion, was {champions}.");
        }

        SeasonRankedAthlete champion = ranked.Single(r => r.IsChampion);
        if (champion.SeasonRank != 1)
        {
            throw new InvalidOperationException("Final season champion must hold season rank 1.");
        }
    }

    private static void CheckCompleteCoverage(IReadOnlyList<SeasonAthleteTotals> totals, RulesV1 rules)
    {
        foreach (SeasonAthleteTotals entry in totals)
        {
            int stageSum = entry.StagePlaceCounts.Sum();
            if (stageSum != rules.StagesPerSeason)
            {
                throw new InvalidOperationException(
                    $"Final season totals for '{entry.Name}' cover {stageSum} stages, expected {rules.StagesPerSeason}.");
            }

            int roundSum = entry.RoundPlaceCounts.Sum();
            checked
            {
                if (roundSum != rules.StagesPerSeason * rules.RoundsPerStage)
                {
                    throw new InvalidOperationException(
                        $"Final season totals for '{entry.Name}' cover {roundSum} rounds, expected {rules.StagesPerSeason * rules.RoundsPerStage}.");
                }
            }
        }
    }

    /// <summary>
    /// Builds a stable fingerprint input for tests/diagnostics.
    /// SHA-256 is a content fingerprint, not sporting randomness.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<SeasonRankedAthlete> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (SeasonRankedAthlete entry in ranked.OrderBy(r => r.SeasonRank))
        {
            builder.Append(entry.SeasonRank);
            builder.Append(':');
            builder.Append(entry.AthleteId);
            builder.Append(':');
            builder.Append(entry.TotalChampionshipPointsThousandths);
            builder.Append('\n');
        }

        return Convert.ToHexStringLower(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
