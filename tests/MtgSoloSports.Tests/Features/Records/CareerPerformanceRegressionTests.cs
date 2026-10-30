using MtgSoloSports.SimulationKernel.Careers;
using MtgSoloSports.SimulationKernel.Leagues;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Records;

/// <summary>
/// MSS-094 historical regression fixtures: locks the MSS-083-v1 career points
/// formulas after extraction into SimulationKernel. Every expected integer is a
/// persisted historical value; any change fails loudly instead of silently
/// shifting Career Leaders totals or ordering for legacy (v1 single-feeder)
/// and tiered (F1/F2/F3) saves.
/// </summary>
public sealed class CareerPerformanceRegressionTests
{
    [Fact]
    public void Weights_AreHistoricalConstants()
    {
        CareerPerformanceScorer.WeightsVersion.ShouldBe("MSS-083-v1");
        CareerPerformanceScorer.SuperleagueWeight.ShouldBe(1000);
        CareerPerformanceScorer.Feeder1Weight.ShouldBe(400);
        CareerPerformanceScorer.Feeder2Weight.ShouldBe(200);
        CareerPerformanceScorer.Feeder3Weight.ShouldBe(100);
        CareerPerformanceScorer.ColorCupIndividualWeight.ShouldBe(800);
        CareerPerformanceScorer.ColorCupTeamWeight.ShouldBe(600);
        CareerPerformanceScorer.TypeCupTeamWeight.ShouldBe(600);
        CareerPerformanceScorer.TeamSharePermille.ShouldBe(250);
        CareerPerformanceScorer.QualificationCreditPermille.ShouldBe(300);
        CareerPerformanceScorer.FirstPlacePremiumPermille.ShouldBe(1500);
        CareerPerformanceScorer.SecondPlacePremiumPermille.ShouldBe(1250);
        CareerPerformanceScorer.ThirdPlacePremiumPermille.ShouldBe(1150);
        CareerPerformanceScorer.NonPodiumPremiumPermille.ShouldBe(1000);
    }

    [Fact]
    public void Titles_ScoreHistoricalFixedPoints()
    {
        Score(CareerPerformanceScorer.Category.Superleague, 1, 32, true).ShouldBe(1500);
        Score(CareerPerformanceScorer.Category.Feeder1, 1, 32, true).ShouldBe(600);
        Score(CareerPerformanceScorer.Category.Feeder2, 1, 32, true).ShouldBe(300);
        Score(CareerPerformanceScorer.Category.Feeder3, 1, 32, true).ShouldBe(150);
        Score(CareerPerformanceScorer.Category.ColorCupIndividual, 1, 32, true).ShouldBe(1200);
        Score(CareerPerformanceScorer.Category.ColorCupTeam, 1, 8, true).ShouldBe(225);
        Score(CareerPerformanceScorer.Category.TypeCupTeam, 1, 4, true).ShouldBe(225);
    }

    [Fact]
    public void TypeQualifier_CarriesReducedThirtyPercentCredit()
    {
        // 30% of the equivalent Final team score (225 -> 67 after truncation).
        Score(CareerPerformanceScorer.Category.TypeCupTeam, 1, 4, false).ShouldBe(67);
        Score(CareerPerformanceScorer.Category.TypeCupTeam, 2, 4, false).ShouldBe(42);
        Score(CareerPerformanceScorer.Category.TypeCupTeam, 1, 4, true)
            .ShouldBeGreaterThan(Score(CareerPerformanceScorer.Category.TypeCupTeam, 1, 4, false));
    }

    [Fact]
    public void PodiumAndMidfield_ScoreHistoricalFixedPoints()
    {
        Score(CareerPerformanceScorer.Category.Superleague, 2, 32, true).ShouldBe(1210);
        Score(CareerPerformanceScorer.Category.Superleague, 3, 32, true).ShouldBe(1077);
        Score(CareerPerformanceScorer.Category.Superleague, 16, 32, true).ShouldBe(531);
        Score(CareerPerformanceScorer.Category.Superleague, 32, 32, true).ShouldBe(31);
        Score(CareerPerformanceScorer.Category.Feeder1, 5, 32, true).ShouldBe(350);
        Score(CareerPerformanceScorer.Category.ColorCupIndividual, 5, 32, true).ShouldBe(700);
        Score(CareerPerformanceScorer.Category.ColorCupTeam, 2, 8, true).ShouldBe(164);
    }

    [Fact]
    public void FieldNormalization_KeepsHistoricalDistinctions()
    {
        CareerPerformanceScorer.PlacementFactorThousandths(5, 16).ShouldBe(750);
        CareerPerformanceScorer.PlacementFactorThousandths(5, 64).ShouldBe(937);
        CareerPerformanceScorer.PlacementFactorThousandths(1, 1).ShouldBe(1000);
        CareerPerformanceScorer.PlacementFactorThousandths(32, 32).ShouldBe(31);
        Score(CareerPerformanceScorer.Category.Superleague, 5, 64, true).ShouldBe(937);
        Score(CareerPerformanceScorer.Category.Superleague, 5, 16, true).ShouldBe(750);
        Score(CareerPerformanceScorer.Category.Feeder1, 5, 64, true).ShouldBe(374);
        Score(CareerPerformanceScorer.Category.Feeder1, 5, 16, true).ShouldBe(300);
    }

    [Fact]
    public void LegacyFeeder_MatchesTieredFeeder1()
    {
        // Historical v1 feeder rows store division None; they surface as Feeder 1
        // without pretending F2/F3 existed. Tiered saves carry explicit F1/F2/F3.
        LeagueHierarchy.LevelForDivision(FeederDivision.None, isSuperleague: false)
            .ShouldBe(LeagueLevel.Feeder1);
        LeagueHierarchy.LevelForDivision(FeederDivision.First, isSuperleague: false)
            .ShouldBe(LeagueLevel.Feeder1);
        LeagueHierarchy.LevelForDivision(FeederDivision.Second, isSuperleague: false)
            .ShouldBe(LeagueLevel.Feeder2);
        LeagueHierarchy.LevelForDivision(FeederDivision.Third, isSuperleague: false)
            .ShouldBe(LeagueLevel.Feeder3);

        CareerPerformanceScorer.CategoryForLevel(LeagueLevel.Feeder1)
            .ShouldBe(CareerPerformanceScorer.Category.Feeder1);

        int legacyTitle = Score(
            CareerPerformanceScorer.CategoryForLevel(
                LeagueHierarchy.LevelForDivision(FeederDivision.None, isSuperleague: false)),
            1, 32, true);
        legacyTitle.ShouldBe(600);
        Score(CareerPerformanceScorer.Category.Feeder1, 1, 32, true).ShouldBe(legacyTitle);
        Score(CareerPerformanceScorer.Category.Feeder2, 1, 32, true).ShouldBe(300);
        Score(CareerPerformanceScorer.Category.Feeder3, 1, 32, true).ShouldBe(150);
    }

    [Fact]
    public void MixedCareer_TotalsAndOrderingMatchHistory()
    {
        // Elite: S P1 (1500) + S P2 (1210) + CI P1 (1200) + TT final P1/4 (225) = 4135.
        // Accumulator: 4x F1 title (2400) + F2 title (300) = 2700.
        // Elite breadth at the top still outranks lower-tier accumulation.
        var events = new List<CareerPerformanceScorer.PlacementEvent>
        {
            new(1, 7, CareerPerformanceScorer.Category.Superleague, 1, 32, true),
            new(1, 8, CareerPerformanceScorer.Category.Superleague, 2, 32, true),
            new(1, 7, CareerPerformanceScorer.Category.ColorCupIndividual, 1, 32, true),
            new(1, 8, CareerPerformanceScorer.Category.TypeCupTeam, 1, 4, true),
            new(2, 7, CareerPerformanceScorer.Category.Feeder1, 1, 32, true),
            new(2, 8, CareerPerformanceScorer.Category.Feeder1, 1, 32, true),
            new(2, 9, CareerPerformanceScorer.Category.Feeder1, 1, 32, true),
            new(2, 10, CareerPerformanceScorer.Category.Feeder1, 1, 32, true),
            new(2, 11, CareerPerformanceScorer.Category.Feeder2, 1, 32, true),
        };

        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> scores =
            CareerPerformanceScorer.ComputeScores(events);

        scores[1].TotalPoints.ShouldBe(4135);
        scores[1].SuperleaguePoints.ShouldBe(2710);
        scores[1].ColorCupIndividualPoints.ShouldBe(1200);
        scores[1].TypeCupTeamPoints.ShouldBe(225);
        scores[1].SuperleagueTitles.ShouldBe(1);
        scores[1].MajorWins.ShouldBe(2);
        scores[1].ScoredEvents.ShouldBe(4);

        scores[2].TotalPoints.ShouldBe(2700);
        scores[2].Feeder1Points.ShouldBe(2400);
        scores[2].Feeder2Points.ShouldBe(300);
        scores[2].LeagueTitles.ShouldBe(5);

        scores[1].TotalPoints.ShouldBeGreaterThan(scores[2].TotalPoints);
    }

    [Fact]
    public void ComputeScores_IsOrderIndependentAndDeterministic()
    {
        var forward = new List<CareerPerformanceScorer.PlacementEvent>
        {
            new(7, 3, CareerPerformanceScorer.Category.Superleague, 1, 32, true),
            new(7, 4, CareerPerformanceScorer.Category.Feeder1, 5, 32, true),
            new(9, 3, CareerPerformanceScorer.Category.ColorCupTeam, 2, 8, true),
        };
        var reversed = forward.AsEnumerable().Reverse().ToList();

        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> first =
            CareerPerformanceScorer.ComputeScores(forward);
        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> second =
            CareerPerformanceScorer.ComputeScores(reversed);

        first[7].TotalPoints.ShouldBe(second[7].TotalPoints);
        first[9].TotalPoints.ShouldBe(second[9].TotalPoints);
        first[7].TotalPoints.ShouldBe(1500 + 350);
        first[9].TotalPoints.ShouldBe(164);
    }

    [Fact]
    public void KernelSource_HasNoEfOrHttpDependencies()
    {
        string source = ReadKernelSource();
        source.ShouldNotContain("Microsoft.EntityFrameworkCore");
        source.ShouldNotContain("Microsoft.AspNetCore");
        source.ShouldNotContain("System.Net.Http");
        source.ShouldNotContain("Random.Shared");
        source.ShouldNotContain("new Random(");
        source.ShouldNotContain("Guid.NewGuid()");
        source.ShouldNotContain("DateTime.");
    }

    private static int Score(CareerPerformanceScorer.Category category, int rank, int fieldSize, bool isFinal) =>
        CareerPerformanceScorer.ScoreEvent(new CareerPerformanceScorer.PlacementEvent(7, 3, category, rank, fieldSize, isFinal));

    private static string ReadKernelSource()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(
                dir, "src", "MtgSoloSports", "SimulationKernel", "Careers", "CareerPerformanceScorer.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            string slnx = Path.Combine(dir, "MtgSoloSports.slnx");
            if (File.Exists(slnx))
            {
                string nested = Path.Combine(
                    dir, "src", "MtgSoloSports", "SimulationKernel", "Careers", "CareerPerformanceScorer.cs");
                if (File.Exists(nested))
                {
                    return File.ReadAllText(nested);
                }
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate CareerPerformanceScorer kernel source for purity check.");
    }
}
