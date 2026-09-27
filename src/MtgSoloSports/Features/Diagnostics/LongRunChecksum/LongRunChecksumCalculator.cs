using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MtgSoloSports.Features.Diagnostics.LongRunChecksum;

/// <summary>
/// Stable long-run sporting fingerprint. Hashes only normalized sporting
/// columns in a deterministic order (never round payload bytes, never database
/// ids, never wall-clock values) so refactors can prove deterministic
/// equivalence across hundreds of seasons without asserting gigantic datasets.
/// Integers use invariant culture; strings use ordinal bytes.
/// </summary>
public static class LongRunChecksumCalculator
{
    public const string Version = "lr1";

    public sealed record SeasonInput(
        int SeasonNumber,
        int LeagueId,
        int StageNumber,
        int RoundNumber,
        string PayloadChecksum);

    public sealed record StageInput(
        int SeasonNumber,
        int LeagueId,
        int StageNumber,
        int StageRank,
        int SaveAthleteId,
        int StageScoreThousandths,
        int ChampionshipPointsThousandths,
        int EarnedBonusThousandths);

    public sealed record SeasonStandingInput(
        int SeasonNumber,
        int LeagueId,
        int SeasonRank,
        int SaveAthleteId,
        int TotalChampionshipPointsThousandths,
        int TotalStageScoreThousandths);

    public sealed record MembershipInput(
        int SeasonNumber,
        int SaveAthleteId,
        int? LeagueId);

    /// <summary>
    /// Computes the stable checksum over ordered sporting inputs plus RNG state.
    /// Callers may pass database order; this method sorts defensively so
    /// equivalent sets hash identically.
    /// </summary>
    public static string Compute(
        IEnumerable<SeasonInput> rounds,
        IEnumerable<StageInput> stages,
        IEnumerable<SeasonStandingInput> seasons,
        IEnumerable<MembershipInput> memberships,
        ulong rngState,
        ulong rngStream)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(seasons);
        ArgumentNullException.ThrowIfNull(memberships);

        List<string> lines = new();
        AppendRounds(lines, rounds);
        AppendStages(lines, stages);
        AppendFinals(lines, seasons);
        AppendMemberships(lines, memberships);
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"G:{rngState}:{rngStream}:{Version}"));
        return HashLines(lines);
    }

    internal static void AppendRounds(List<string> lines, IEnumerable<SeasonInput> rounds)
    {
        foreach (SeasonInput r in rounds
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.LeagueId)
            .ThenBy(e => e.StageNumber)
            .ThenBy(e => e.RoundNumber))
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"R:{r.SeasonNumber}:{r.LeagueId}:{r.StageNumber}:{r.RoundNumber}:{r.PayloadChecksum}"));
        }
    }

    internal static void AppendStages(List<string> lines, IEnumerable<StageInput> stages)
    {
        foreach (StageInput s in stages
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.LeagueId)
            .ThenBy(e => e.StageNumber)
            .ThenBy(e => e.StageRank))
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"S:{s.SeasonNumber}:{s.LeagueId}:{s.StageNumber}:{s.StageRank}:{s.SaveAthleteId}:{s.StageScoreThousandths}:{s.ChampionshipPointsThousandths}:{s.EarnedBonusThousandths}"));
        }
    }

    internal static void AppendFinals(List<string> lines, IEnumerable<SeasonStandingInput> seasons)
    {
        foreach (SeasonStandingInput s in seasons
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.LeagueId)
            .ThenBy(e => e.SeasonRank))
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"F:{s.SeasonNumber}:{s.LeagueId}:{s.SeasonRank}:{s.SaveAthleteId}:{s.TotalChampionshipPointsThousandths}:{s.TotalStageScoreThousandths}"));
        }
    }

    internal static void AppendMemberships(List<string> lines, IEnumerable<MembershipInput> memberships)
    {
        foreach (MembershipInput m in memberships
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.SaveAthleteId))
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"M:{m.SeasonNumber}:{m.SaveAthleteId}:{(m.LeagueId.HasValue ? m.LeagueId.Value.ToString(CultureInfo.InvariantCulture) : "pool")}"));
        }
    }

    internal static string HashLines(List<string> lines)
    {
        StringBuilder builder = new();
        foreach (string line in lines)
        {
            builder.Append(line);
            builder.Append('\n');
        }

        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes a per-season checksum so long-run divergence can be localized
    /// to the first differing season without comparing full datasets.
    /// </summary>
    public static IReadOnlyList<(int SeasonNumber, string Checksum)> ComputePerSeason(
        IEnumerable<SeasonInput> rounds,
        IEnumerable<StageInput> stages,
        IEnumerable<SeasonStandingInput> seasons,
        IEnumerable<MembershipInput> memberships)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(seasons);
        ArgumentNullException.ThrowIfNull(memberships);

        List<SeasonInput> roundList = rounds.ToList();
        List<StageInput> stageList = stages.ToList();
        List<SeasonStandingInput> finalList = seasons.ToList();
        List<MembershipInput> membershipList = memberships.ToList();

        List<int> numbers = CollectSeasonNumbers(roundList, stageList, finalList, membershipList);
        List<(int SeasonNumber, string Checksum)> result = new(numbers.Count);
        foreach (int season in numbers)
        {
            string checksum = Compute(
                roundList.Where(e => e.SeasonNumber == season),
                stageList.Where(e => e.SeasonNumber == season),
                finalList.Where(e => e.SeasonNumber == season),
                membershipList.Where(e => e.SeasonNumber == season),
                rngState: (ulong)season,
                rngStream: 0UL);
            result.Add((season, checksum));
        }

        return result;
    }

    internal static List<int> CollectSeasonNumbers(
        List<SeasonInput> rounds,
        List<StageInput> stages,
        List<SeasonStandingInput> finals,
        List<MembershipInput> memberships)
    {
        HashSet<int> numbers = new();
        foreach (SeasonInput r in rounds)
        {
            numbers.Add(r.SeasonNumber);
        }

        foreach (StageInput s in stages)
        {
            numbers.Add(s.SeasonNumber);
        }

        foreach (SeasonStandingInput s in finals)
        {
            numbers.Add(s.SeasonNumber);
        }

        foreach (MembershipInput m in memberships)
        {
            numbers.Add(m.SeasonNumber);
        }

        return numbers.OrderBy(e => e).ToList();
    }
}
