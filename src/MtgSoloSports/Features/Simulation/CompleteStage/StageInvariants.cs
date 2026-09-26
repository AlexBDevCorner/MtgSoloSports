using System.Security.Cryptography;
using System.Text;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Simulation.CompleteStage;

/// <summary>
/// Structural invariants for a completed stage. Fundamental failures throw and
/// abort the mutation; corrupted sporting state is never silently repaired.
/// </summary>
public static class StageInvariants
{
    /// <summary>
    /// Validates a freshly completed stage before commit: exactly 16 rounds of
    /// 32, stage scores chaining from round payloads, ranks 1..32 exactly once,
    /// championship points matching the snapshot table with no bonus multiplier,
    /// earned bonus matching round-plus-stage tables, and round-place counts
    /// summing to 16 per athlete.
    /// </summary>
    public static void ValidateCompletedStage(
        IReadOnlyList<StageRankedAthlete> ranked,
        IReadOnlyList<StageAthleteTotals> totals,
        RulesV1 rules,
        bool isSuperleague)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(rules);

        if (ranked.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Completed stage must rank exactly {rules.LeagueSize} athletes, was {ranked.Count}.");
        }

        if (totals.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Completed stage must total exactly {rules.LeagueSize} athletes, was {totals.Count}.");
        }

        Dictionary<int, StageAthleteTotals> totalsById = totals.ToDictionary(t => t.AthleteId);
        HashSet<int> ranks = new();
        HashSet<int> athleteIds = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (StageRankedAthlete entry in ranked)
        {
            CheckStandingIdentity(entry, ranks, athleteIds, names, rules);
            CheckStandingTotals(entry, totalsById, rules, isSuperleague);
        }

        if (!ranks.SetEquals(Enumerable.Range(1, rules.LeagueSize)))
        {
            throw new InvalidOperationException("Completed stage must cover ranks 1..32 exactly once.");
        }
    }

    /// <summary>
    /// Fingerprints stage standings: lowercase hex SHA-256 over lines of
    /// <c>Rank:AthleteId:Name:Score:Championship:Earned</c> in rank order.
    /// SHA-256 is a content fingerprint here, not sporting randomness.
    /// </summary>
    public static string ComputeChecksum(IReadOnlyList<StageRankedAthlete> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (StageRankedAthlete entry in ranked)
        {
            builder.Append(entry.StageRank.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.AthleteId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.Name);
            builder.Append(':');
            builder.Append(entry.StageScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.ChampionshipPointsThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.EarnedBonusThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('\n');
        }

        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static void CheckStandingIdentity(
        StageRankedAthlete entry,
        HashSet<int> ranks,
        HashSet<int> athleteIds,
        HashSet<string> names,
        RulesV1 rules)
    {
        if (entry.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Completed stage contains invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Completed stage contains an athlete with an empty name.");
        }

        if (entry.StageRank < 1 || entry.StageRank > rules.LeagueSize)
        {
            throw new InvalidOperationException($"Completed stage rank {entry.StageRank} is out of range.");
        }

        if (!ranks.Add(entry.StageRank))
        {
            throw new InvalidOperationException($"Completed stage contains duplicate rank {entry.StageRank}.");
        }

        if (!athleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Completed stage contains duplicate athlete id {entry.AthleteId}.");
        }

        if (!names.Add(entry.Name))
        {
            throw new InvalidOperationException($"Completed stage contains duplicate athlete '{entry.Name}'.");
        }
    }

    private static void CheckStandingTotals(
        StageRankedAthlete entry,
        Dictionary<int, StageAthleteTotals> totalsById,
        RulesV1 rules,
        bool isSuperleague)
    {
        if (!totalsById.TryGetValue(entry.AthleteId, out StageAthleteTotals? totals))
        {
            throw new InvalidOperationException($"Completed stage standing for '{entry.Name}' has no accumulated totals.");
        }

        if (!string.Equals(totals.Name, entry.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Completed stage standing for id {entry.AthleteId} has inconsistent names.");
        }

        if (entry.StageScoreThousandths != totals.StageScoreThousandths)
        {
            throw new InvalidOperationException($"Completed stage score for '{entry.Name}' does not match accumulated round finals.");
        }

        if (entry.BaseScoreThousandths != totals.BaseScoreThousandths)
        {
            throw new InvalidOperationException($"Completed stage base score for '{entry.Name}' does not match accumulated round base points.");
        }

        int expectedChampionship = rules.ScoringTable[entry.StageRank - 1] * RulesV1.FixedScale;
        if (entry.ChampionshipPointsThousandths != expectedChampionship)
        {
            throw new InvalidOperationException(
                $"Completed stage championship points for '{entry.Name}' must be {expectedChampionship}, was {entry.ChampionshipPointsThousandths}.");
        }

        int expectedEarned = ComputeExpectedEarned(entry, rules, isSuperleague);
        if (entry.EarnedBonusThousandths != expectedEarned)
        {
            throw new InvalidOperationException(
                $"Completed stage earned bonus for '{entry.Name}' must be {expectedEarned}, was {entry.EarnedBonusThousandths}.");
        }

        if (entry.RoundWins != totals.RoundWins)
        {
            throw new InvalidOperationException($"Completed stage round wins for '{entry.Name}' do not match round placements.");
        }

        if (entry.RoundPlaceCounts.Count != totals.RoundPlaceCounts.Count)
        {
            throw new InvalidOperationException($"Completed stage round-place counts for '{entry.Name}' do not match accumulated totals.");
        }

        for (int i = 0; i < entry.RoundPlaceCounts.Count; i++)
        {
            if (entry.RoundPlaceCounts[i] != totals.RoundPlaceCounts[i])
            {
                throw new InvalidOperationException($"Completed stage round-place counts for '{entry.Name}' do not match accumulated totals.");
            }
        }
    }

    private static int ComputeExpectedEarned(StageRankedAthlete entry, RulesV1 rules, bool isSuperleague)
    {
        int roundTotal = 0;
        checked
        {
            for (int position = 1; position <= entry.RoundPlaceCounts.Count; position++)
            {
                int count = entry.RoundPlaceCounts[position - 1];
                if (count == 0)
                {
                    continue;
                }

                int perFinish = position <= rules.RoundBonusThousandths.Count
                    ? rules.RoundBonusThousandths[position - 1]
                    : 0;
                if (isSuperleague)
                {
                    perFinish = checked(perFinish * rules.SuperleagueBonusMultiplier);
                }

                roundTotal = checked(roundTotal + (count * perFinish));
            }

            int stageBonus = entry.StageRank <= rules.StageBonusThousandths.Count
                ? rules.StageBonusThousandths[entry.StageRank - 1]
                : 0;
            if (isSuperleague)
            {
                stageBonus = checked(stageBonus * rules.SuperleagueBonusMultiplier);
            }

            return checked(roundTotal + stageBonus);
        }
    }
}
