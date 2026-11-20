using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Stages;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Stages;

/// <summary>
/// MSS-151 golden equivalence: pins the exact pre-refactor stage ranking output,
/// including every rank's championship points (awarded through
/// <c>ScoringCalculator.ChampionshipPointsForPosition</c> after the
/// consolidation) and the seeded-draw RNG consumption for the fully tied
/// trio. The values below were captured from the pre-consolidation engine on
/// this branch; they must stay unchanged and must never be re-captured on
/// failure.
/// </summary>
public sealed class StageScoringGoldenEquivalenceTests
{
    private const string StageRankedFingerprint =
        "1:2:Tied Two:200000:77000:700|2:3:Tied Three:200000:67000:680|3:1:Tied One:200000:58000:660"
        + "|4:4:Firsts Win:150000:50000:1440|5:5:Seconds Lose:150000:43000:1550"
        + "|6:6:Distinct 06:90000:37000:900|7:7:Distinct 07:89000:32000:720|8:8:Distinct 08:88000:28000:540"
        + "|9:9:Distinct 09:87000:25000:360|10:10:Distinct 10:86000:23000:180|11:11:Distinct 11:85000:22000:0"
        + "|12:12:Distinct 12:84000:21000:0|13:13:Distinct 13:83000:20000:0|14:14:Distinct 14:82000:19000:0"
        + "|15:15:Distinct 15:81000:18000:0|16:16:Distinct 16:80000:17000:0|17:17:Distinct 17:79000:16000:0"
        + "|18:18:Distinct 18:78000:15000:0|19:19:Distinct 19:77000:14000:0|20:20:Distinct 20:76000:13000:0"
        + "|21:21:Distinct 21:75000:12000:0|22:22:Distinct 22:74000:11000:0|23:23:Distinct 23:73000:10000:0"
        + "|24:24:Distinct 24:72000:9000:0|25:25:Distinct 25:71000:8000:0|26:26:Distinct 26:70000:7000:0"
        + "|27:27:Distinct 27:69000:6000:0|28:28:Distinct 28:68000:5000:0|29:29:Distinct 29:67000:4000:0"
        + "|30:30:Distinct 30:66000:3000:0|31:31:Distinct 31:65000:2000:0|32:32:Distinct 32:64000:1000:0";

    private const ulong StageRngAfterState = 14140312242919920231UL;

    [Fact]
    public void StageCalculatorRank_MatchesPreRefactorGolden()
    {
        List<StageAthleteTotals> totals = new(32);
        totals.Add(new StageAthleteTotals(1, "Tied One", 200_000, 180_000, Counts(leading: [2, 2], hotIndex: 9, hotValue: 12)));
        totals.Add(new StageAthleteTotals(2, "Tied Two", 200_000, 180_000, Counts(leading: [2, 2], hotIndex: 9, hotValue: 12)));
        totals.Add(new StageAthleteTotals(3, "Tied Three", 200_000, 180_000, Counts(leading: [2, 2], hotIndex: 9, hotValue: 12)));
        totals.Add(new StageAthleteTotals(4, "Firsts Win", 150_000, 140_000, Counts(leading: [1], hotIndex: 2, hotValue: 15)));
        totals.Add(new StageAthleteTotals(5, "Seconds Lose", 150_000, 140_000, Counts(leading: [0, 15, 1], hotIndex: 31, hotValue: 0)));
        for (int i = 6; i <= 32; i++)
        {
            totals.Add(new StageAthleteTotals(
                i, $"Distinct {i:D2}", 90_000 - ((i - 6) * 1000), 85_000 - ((i - 6) * 1000), Counts([], i - 1, 16)));
        }

        Pcg32V1 rng = new(151151UL, 44UL);
        IReadOnlyList<StageRankedAthlete> ranked =
            StageCalculator.Rank(totals, rng, RulesV1.CreateDefault(), LeagueLevel.Feeder1);

        ranked.Count.ShouldBe(32);
        string.Join("|", ranked.Select(r =>
            $"{r.StageRank}:{r.AthleteId}:{r.Name}:{r.StageScoreThousandths}:{r.ChampionshipPointsThousandths}:{r.EarnedBonusThousandths}"))
            .ShouldBe(StageRankedFingerprint);
        rng.Snapshot().State.ShouldBe(StageRngAfterState);
        rng.Snapshot().Stream.ShouldBe(44UL);
    }

    private static int[] Counts(int[] leading, int hotIndex, int hotValue)
    {
        int[] counts = new int[32];
        for (int i = 0; i < leading.Length; i++)
        {
            counts[i] += leading[i];
        }

        counts[hotIndex] += hotValue;
        return counts;
    }
}
