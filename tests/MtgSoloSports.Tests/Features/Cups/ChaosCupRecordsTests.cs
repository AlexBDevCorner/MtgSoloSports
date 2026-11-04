using MtgSoloSports.Features.Cups.CompleteChaosCup;
using MtgSoloSports.Features.Cups.GetChaosCupRecords;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-117: factual Chaos records and conservative Tournament Stories.
/// Pure calculator tests cover titles/medals/finals/streak/division/color/P16
/// plus counterexample guards (group outranking is never a duel); the single
/// handler test completes one real Chaos tournament as its named action and
/// proves reproducibility, empty-allowed stories and prior-edition
/// compatibility. No resimulation, no points impact.
/// </summary>
public sealed class ChaosCupRecordsTests
{
    [Fact]
    public void EmptyHistory_IsVacantWithEmptyStories_Reproducible()
    {
        ChaosCupRecordCalculator.RecordsResult result = ChaosCupRecordCalculator.ComputeAll(
            [], [], [], new Dictionary<int, string>());
        result.CompletedSeasons.ShouldBeEmpty();
        result.Athletes.ShouldBeEmpty();
        result.Stories.ShouldBeEmpty();
        foreach (ChaosCupRecordCalculator.DivisionBest division in result.Divisions)
        {
            division.BestFinalRank.ShouldBeNull();
        }

        foreach (ChaosCupRecordCalculator.ColorBest color in result.Colors)
        {
            color.TitlesMax.ShouldBe(0);
            color.BestFinalRank.ShouldBeNull();
        }

        ChaosCupRecordCalculator.RecordsResult retry = ChaosCupRecordCalculator.ComputeAll(
            [], [], [], new Dictionary<int, string>());
        retry.Stories.Count.ShouldBe(result.Stories.Count);
    }

    [Fact]
    public void TitlesMedalsFinalsStreakAndP16_ComputedFromCompletedHistory()
    {
        Dictionary<int, string> names = new() { [1] = "Alpha", [2] = "Beta", [3] = "Gamma" };
        var editions = new List<ChaosCupRecordCalculator.EditionInput>
        {
            new(1, true),
            new(2, true),
            new(3, false),
        };
        var entrants = new List<ChaosCupRecordCalculator.EntrantInput>
        {
            new(1, 1, 0, "Feeder1"), new(1, 2, 1, "Pool"), new(1, 3, 2, "Feeder3"),
            new(2, 1, 0, "Feeder1"), new(2, 2, 1, "Pool"), new(2, 3, 2, "Feeder3"),
            new(3, 1, 0, "Feeder1"),
        };
        var standings = new List<ChaosCupRecordCalculator.StandingInput>
        {
            new(1, 0, 1, 1, true), new(1, 0, 2, 16, true), new(1, 0, 3, 17, false),
            new(1, 5, 1, 1, false), new(1, 5, 2, 5, false),
            new(2, 0, 1, 16, true), new(2, 0, 2, 2, true), new(2, 0, 3, 17, false),
            new(2, 5, 1, 2, false), new(2, 5, 2, 1, false),
            new(3, 0, 1, 1, true),
        };

        ChaosCupRecordCalculator.RecordsResult result = ChaosCupRecordCalculator.ComputeAll(
            editions, entrants, standings, names);

        result.CompletedSeasons.ShouldBe([1, 2]);
        ChaosCupRecordCalculator.AthleteChaosRecord alpha = result.Athletes.Single(a => a.SaveAthleteId == 1);
        alpha.Titles.ShouldBe(1);
        alpha.Medals.ShouldBe(2);
        alpha.Finals.ShouldBe(2);
        alpha.BestFinalRank.ShouldBe(1);
        alpha.LongestProgressStreak.ShouldBe(2);
        alpha.P16Survivals.ShouldBe(1);

        ChaosCupRecordCalculator.AthleteChaosRecord beta = result.Athletes.Single(a => a.SaveAthleteId == 2);
        beta.Titles.ShouldBe(1);
        beta.P16Survivals.ShouldBe(1);

        ChaosCupRecordCalculator.DivisionBest pool = result.Divisions.Single(d => string.Equals(d.DivisionKey, "Pool", StringComparison.Ordinal));
        pool.BestFinalRank.ShouldBe(1);
        pool.DeepestStage.ShouldBe(5);

        ChaosCupRecordCalculator.DivisionBest feeder3 = result.Divisions.Single(d => string.Equals(d.DivisionKey, "Feeder3", StringComparison.Ordinal));
        feeder3.BestFinalRank.ShouldBeNull();
        feeder3.DeepestStage.ShouldBe(0);
    }

    [Fact]
    public void Counterexample_GroupOutrankingWithoutFinal_IsNeverAnUnderdog()
    {
        Dictionary<int, string> names = new() { [1] = "Star", [2] = "Pool Grinder" };
        var editions = new List<ChaosCupRecordCalculator.EditionInput> { new(1, true) };
        var entrants = new List<ChaosCupRecordCalculator.EntrantInput>
        {
            new(1, 1, 0, "Feeder1"),
            new(1, 2, 1, "Pool"),
        };
        var standings = new List<ChaosCupRecordCalculator.StandingInput>
        {
            new(1, 0, 2, 1, true),
            new(1, 0, 1, 2, true),
            new(1, 1, 1, 1, true),
            new(1, 1, 2, 17, false),
        };

        ChaosCupRecordCalculator.RecordsResult result = ChaosCupRecordCalculator.ComputeAll(
            editions, entrants, standings, names);
        result.Stories.Count(s => string.Equals(s.Kind, ChaosCupRecordCalculator.StoryUnderdog, StringComparison.Ordinal))
            .ShouldBe(0);
    }

    [Fact]
    public void Counterexample_Feeder1Finalist_IsNeverAnUnderdog()
    {
        Dictionary<int, string> names = new() { [1] = "Established" };
        var editions = new List<ChaosCupRecordCalculator.EditionInput> { new(1, true) };
        var entrants = new List<ChaosCupRecordCalculator.EntrantInput> { new(1, 1, 0, "Feeder1") };
        var standings = new List<ChaosCupRecordCalculator.StandingInput>
        {
            new(1, 0, 1, 1, true),
            new(1, 5, 1, 5, false),
        };

        ChaosCupRecordCalculator.RecordsResult result = ChaosCupRecordCalculator.ComputeAll(
            editions, entrants, standings, names);
        result.Stories.Count(s => string.Equals(s.Kind, ChaosCupRecordCalculator.StoryUnderdog, StringComparison.Ordinal))
            .ShouldBe(0);
    }

    [Fact]
    public void Underdog_PoolFinalTopEightAndFeeder3Medal_Qualify()
    {
        Dictionary<int, string> names = new() { [1] = "Pool Hero", [2] = "F3 Medalist", [3] = "F3 Also Ran" };
        var editions = new List<ChaosCupRecordCalculator.EditionInput> { new(1, true) };
        var entrants = new List<ChaosCupRecordCalculator.EntrantInput>
        {
            new(1, 1, 1, "Pool"),
            new(1, 2, 2, "Feeder3"),
            new(1, 3, 3, "Feeder3"),
        };
        var standings = new List<ChaosCupRecordCalculator.StandingInput>
        {
            new(1, 0, 1, 1, true), new(1, 0, 2, 2, true), new(1, 0, 3, 3, true),
            new(1, 5, 1, 8, false), new(1, 5, 2, 3, false), new(1, 5, 3, 9, false),
        };

        ChaosCupRecordCalculator.RecordsResult result = ChaosCupRecordCalculator.ComputeAll(
            editions, entrants, standings, names);
        List<ChaosCupRecordCalculator.TournamentStory> underdogs = result.Stories
            .Where(s => string.Equals(s.Kind, ChaosCupRecordCalculator.StoryUnderdog, StringComparison.Ordinal))
            .ToList();
        underdogs.Count.ShouldBe(2);
        underdogs.Any(s => s.SaveAthleteId == 1 && s.SourceSeasonNumber == 1).ShouldBeTrue();
        underdogs.Any(s => s.SaveAthleteId == 2 && s.SourceSeasonNumber == 1).ShouldBeTrue();
        underdogs.Any(s => s.SaveAthleteId == 3).ShouldBeFalse();
    }

    [Fact]
    public void DefenseFailure_RequiresChampionEntryAndNonRepeat()
    {
        Dictionary<int, string> names = new() { [1] = "Champ", [2] = "Next" };
        var editions = new List<ChaosCupRecordCalculator.EditionInput> { new(1, true), new(2, true) };
        var entrants = new List<ChaosCupRecordCalculator.EntrantInput>
        {
            new(1, 1, 0, "Feeder1"), new(1, 2, 0, "Feeder1"),
            new(2, 1, 0, "Feeder1"), new(2, 2, 0, "Feeder1"),
        };
        var standings = new List<ChaosCupRecordCalculator.StandingInput>
        {
            new(1, 0, 1, 1, true), new(1, 0, 2, 2, true),
            new(1, 5, 1, 1, false), new(1, 5, 2, 4, false),
            new(2, 0, 1, 17, false), new(2, 0, 2, 1, true),
            new(2, 5, 2, 1, false),
        };

        ChaosCupRecordCalculator.RecordsResult failed = ChaosCupRecordCalculator.ComputeAll(
            editions, entrants, standings, names);
        List<ChaosCupRecordCalculator.TournamentStory> defenses = failed.Stories
            .Where(s => string.Equals(s.Kind, ChaosCupRecordCalculator.StoryDefenseFailed, StringComparison.Ordinal))
            .ToList();
        defenses.Count.ShouldBe(1);
        defenses[0].SaveAthleteId.ShouldBe(1);
        defenses[0].SourceSeasonNumber.ShouldBe(2);
        defenses[0].RelatedSeasonNumber.ShouldBe(1);

        var absentEntrants = new List<ChaosCupRecordCalculator.EntrantInput>
        {
            new(1, 1, 0, "Feeder1"), new(1, 2, 0, "Feeder1"),
            new(2, 2, 0, "Feeder1"),
        };
        ChaosCupRecordCalculator.RecordsResult absent = ChaosCupRecordCalculator.ComputeAll(
            editions, absentEntrants, standings, names);
        absent.Stories.Count(s => string.Equals(s.Kind, ChaosCupRecordCalculator.StoryDefenseFailed, StringComparison.Ordinal))
            .ShouldBe(0);
    }

    [Fact]
    public void RecordRun_RequiresRepeatLevelThreshold()
    {
        Dictionary<int, string> names = new() { [1] = "Once", [2] = "Twice" };
        var editions = new List<ChaosCupRecordCalculator.EditionInput> { new(1, true), new(2, true) };
        var entrants = new List<ChaosCupRecordCalculator.EntrantInput>
        {
            new(1, 1, 0, "Feeder1"), new(1, 2, 0, "Feeder1"),
            new(2, 1, 0, "Feeder1"), new(2, 2, 0, "Feeder1"),
        };
        var standings = new List<ChaosCupRecordCalculator.StandingInput>
        {
            new(1, 0, 1, 1, true), new(1, 0, 2, 2, true),
            new(1, 5, 1, 1, false), new(1, 5, 2, 2, false),
            new(2, 0, 1, 17, false), new(2, 0, 2, 1, true),
            new(2, 5, 2, 1, false),
        };

        ChaosCupRecordCalculator.RecordsResult result = ChaosCupRecordCalculator.ComputeAll(
            editions, entrants, standings, names);
        List<ChaosCupRecordCalculator.TournamentStory> records = result.Stories
            .Where(s => string.Equals(s.Kind, ChaosCupRecordCalculator.StoryRecordRun, StringComparison.Ordinal))
            .ToList();
        records.Any(s => s.SaveAthleteId == 2).ShouldBeTrue();

        var single = new List<ChaosCupRecordCalculator.EditionInput> { new(1, true) };
        ChaosCupRecordCalculator.RecordsResult lonely = ChaosCupRecordCalculator.ComputeAll(
            single,
            entrants.Where(e => e.SourceSeasonNumber == 1).ToList(),
            standings.Where(s => s.SourceSeasonNumber == 1).ToList(),
            names);
        lonely.Stories.Count(s => string.Equals(s.Kind, ChaosCupRecordCalculator.StoryRecordRun, StringComparison.Ordinal))
            .ShouldBe(0);
    }

    [Fact]
    public async Task CompletedTournament_RecordsAreReproducible_WithEmptyAllowedStories()
    {
        // Incidental Season 1 history is forked; the Chaos field plus full-tournament
        // completion below is the named action under test.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-records-");
        try
        {
            await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            CompleteChaosCupResponse cupped = await new CompleteChaosCupHandler(store)
                .CompleteCupAsync(saveId, sourceSeasonNumber: 1);
            cupped.IsComplete.ShouldBeTrue();

            GetChaosCupRecordsHandler handler = new(store);
            GetChaosCupRecordsResponse first = await handler.HandleAsync(saveId);
            first.ThresholdsVersion.ShouldBe(ChaosCupRecordThresholds.Version);
            first.CompletedSeasons.ShouldBe([1]);
            first.Athletes.Count(a => a.Titles == 1).ShouldBe(1);
            first.Athletes.Count(a => a.Medals == 1).ShouldBe(3);
            first.Athletes.Count(a => a.Finals == 1).ShouldBe(32);
            foreach (GetChaosCupRecordsResponse.AthleteRecord athlete in first.Athletes.Where(a => a.Finals > 0))
            {
                athlete.BestFinalRank.ShouldNotBeNull();
            }

            GetChaosCupRecordsResponse second = await handler.HandleAsync(saveId);
            second.CompletedSeasons.ShouldBe(first.CompletedSeasons);
            second.Stories.Count.ShouldBe(first.Stories.Count);
            second.Stories.Select(s => s.Kind + "|" + s.SaveAthleteId + "|" + s.SourceSeasonNumber)
                .ShouldBe(first.Stories.Select(s => s.Kind + "|" + s.SaveAthleteId + "|" + s.SourceSeasonNumber));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task NoEdition_ReturnsNotFound_PartialEditionStaysVacant()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-records-compat-");
        try
        {
            await Should.ThrowAsync<GetChaosCupRecordsNotFoundException>(() =>
                new GetChaosCupRecordsHandler(store).HandleAsync(saveId));

            await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            GetChaosCupRecordsResponse partial = await new GetChaosCupRecordsHandler(store).HandleAsync(saveId);
            partial.CompletedSeasons.ShouldBeEmpty();
            partial.Stories.ShouldBeEmpty();
            foreach (GetChaosCupRecordsResponse.AthleteRecord athlete in partial.Athletes)
            {
                athlete.Titles.ShouldBe(0);
                athlete.Finals.ShouldBe(0);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }
}
