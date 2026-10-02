namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Pure provisional live team totals for round-by-round Cup team events
/// (MSS-045). Sums already-persisted round final points per team across all
/// completed groups plus the rounds already played in the current in-progress
/// group. Never awards unfinished rounds and never double-counts: callers pass
/// exactly the persisted contributions once each.
/// Ordering is informational only: total score descending, then team name
/// ordinal ascending for a stable display order. Provisional ranks are plain
/// display positions (1..N); the official final team tie-break (group-rank
/// counts, aggregated round-place counts, raw base totals, seeded draw) is
/// deliberately not applied here, and no medals are awarded. All arithmetic is
/// checked fixed-point integers; no randomness, clock, or I/O.
/// </summary>
public static class TeamLiveStandings
{
    /// <summary>
    /// One persisted round placement attributed to its team.
    /// </summary>
    public sealed record LiveContribution(
        int TeamId,
        string TeamName,
        int FinalThousandths,
        int BaseThousandths);

    /// <summary>
    /// One provisional live team row. Every roster team appears exactly once,
    /// even with zero persisted rounds.
    /// </summary>
    public sealed record LiveTeamRow(
        int TeamId,
        string TeamName,
        int ProvisionalRank,
        int ScoreThousandths,
        int BaseThousandths,
        int RoundsCounted);

    /// <summary>
    /// Computes provisional live totals. <paramref name="rosterTeams"/> lists
    /// every participating team exactly once (id plus name); contributions must
    /// reference only those teams with matching names. Throws on unknown teams,
    /// name mismatches, duplicate roster entries, or negative points: corrupted
    /// sporting state is never silently repaired.
    /// </summary>
    public static IReadOnlyList<LiveTeamRow> Compute(
        IReadOnlyList<(int TeamId, string TeamName)> rosterTeams,
        IReadOnlyList<LiveContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(rosterTeams);
        ArgumentNullException.ThrowIfNull(contributions);
        Dictionary<int, string> names = ValidateRoster(rosterTeams);
        Dictionary<int, (int Score, int Base, int Rounds)> totals = SumContributions(names, contributions);
        return OrderRows(names, totals);
    }

    internal static Dictionary<int, string> ValidateRoster(IReadOnlyList<(int TeamId, string TeamName)> rosterTeams)
    {
        ArgumentNullException.ThrowIfNull(rosterTeams);
        if (rosterTeams.Count == 0)
        {
            throw new InvalidOperationException("Live team standings require at least one participating team.");
        }

        Dictionary<int, string> names = new(rosterTeams.Count);
        foreach ((int teamId, string teamName) in rosterTeams)
        {
            if (string.IsNullOrWhiteSpace(teamName))
            {
                throw new InvalidOperationException("Live team roster contains an empty team name.");
            }

            if (!names.TryAdd(teamId, teamName))
            {
                throw new InvalidOperationException($"Live team roster contains duplicate team id {teamId}.");
            }
        }

        if (names.Values.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            throw new InvalidOperationException("Live team roster contains duplicate team names.");
        }

        return names;
    }

    internal static Dictionary<int, (int Score, int Base, int Rounds)> SumContributions(
        Dictionary<int, string> names,
        IReadOnlyList<LiveContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(contributions);
        Dictionary<int, (int Score, int Base, int Rounds)> totals = new(names.Count);
        foreach (int teamId in names.Keys)
        {
            totals[teamId] = (0, 0, 0);
        }

        foreach (LiveContribution contribution in contributions)
        {
            AddContribution(names, totals, contribution);
        }

        return totals;
    }

    internal static void AddContribution(
        Dictionary<int, string> names,
        Dictionary<int, (int Score, int Base, int Rounds)> totals,
        LiveContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(contribution);
        if (!names.TryGetValue(contribution.TeamId, out string? known))
        {
            throw new InvalidOperationException($"Live contribution references unknown team id {contribution.TeamId}.");
        }

        if (!string.Equals(known, contribution.TeamName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Live contribution for team id {contribution.TeamId} changes its team name.");
        }

        if (contribution.FinalThousandths < 0 || contribution.BaseThousandths < 0)
        {
            throw new InvalidOperationException($"Live contribution for team '{contribution.TeamName}' has corrupt negative points.");
        }

        (int score, int @base, int rounds) = totals[contribution.TeamId];
        checked
        {
            totals[contribution.TeamId] = (
                score + contribution.FinalThousandths,
                @base + contribution.BaseThousandths,
                rounds + 1);
        }
    }

    internal static IReadOnlyList<LiveTeamRow> OrderRows(
        Dictionary<int, string> names,
        Dictionary<int, (int Score, int Base, int Rounds)> totals)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(totals);
        List<LiveTeamRow> rows = new(names.Count);
        foreach ((int teamId, string teamName) in names)
        {
            (int score, int @base, int rounds) = totals[teamId];
            rows.Add(new LiveTeamRow(teamId, teamName, 0, score, @base, rounds));
        }

        rows.Sort(static (left, right) =>
        {
            int score = right.ScoreThousandths.CompareTo(left.ScoreThousandths);
            return score != 0 ? score : string.Compare(left.TeamName, right.TeamName, StringComparison.Ordinal);
        });

        List<LiveTeamRow> ranked = new(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            ranked.Add(rows[i] with { ProvisionalRank = i + 1 });
        }

        return ranked;
    }
}
