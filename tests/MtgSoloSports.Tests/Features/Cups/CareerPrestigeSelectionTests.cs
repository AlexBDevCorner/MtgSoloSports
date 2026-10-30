using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.GetHallOfFame;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared.Careers;
using MtgSoloSports.SimulationKernel.Careers;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-096: squad-selection prestige is the shared Career Leaders career history
/// through the selected source season, including nonpodium league and Cup
/// results. Same history earns the same raw in Career Leaders and selection;
/// historical squads stay frozen and both new paths stay deterministic.
/// </summary>
public sealed class CareerPrestigeSelectionTests
{
    private static int Score(CareerPerformanceScorer.Category category, int rank, int fieldSize, bool isFinal = true) =>
        CareerPerformanceScorer.ScoreEvent(new CareerPerformanceScorer.PlacementEvent(7, 3, category, rank, fieldSize, isFinal));

    [Fact]
    public void FiveSuperP6_BeatsSmallTitleCollection_RequiresFullPlacements()
    {
        int superP6 = Score(CareerPerformanceScorer.Category.Superleague, 6, 32);
        superP6.ShouldBe(843);
        int fiveP6 = checked(5 * superP6);
        fiveP6.ShouldBe(4215);

        int smallTitles = Score(CareerPerformanceScorer.Category.Feeder1, 1, 32)
            + Score(CareerPerformanceScorer.Category.Feeder2, 1, 32)
            + Score(CareerPerformanceScorer.Category.Feeder3, 1, 32);
        smallTitles.ShouldBe(600 + 300 + 150);
        fiveP6.ShouldBeGreaterThan(smallTitles);

        int threeSuperTitles = checked(3 * Score(CareerPerformanceScorer.Category.Superleague, 1, 32));
        threeSuperTitles.ShouldBe(4500);
        threeSuperTitles.ShouldBeGreaterThan(fiveP6);

        var events = new List<CareerPerformanceScorer.PlacementEvent>();
        for (int season = 1; season <= 5; season++)
        {
            events.Add(new CareerPerformanceScorer.PlacementEvent(1, season, CareerPerformanceScorer.Category.Superleague, 6, 32, true));
        }

        events.Add(new CareerPerformanceScorer.PlacementEvent(2, 1, CareerPerformanceScorer.Category.Feeder1, 1, 32, true));
        events.Add(new CareerPerformanceScorer.PlacementEvent(2, 2, CareerPerformanceScorer.Category.Feeder2, 1, 32, true));
        events.Add(new CareerPerformanceScorer.PlacementEvent(2, 3, CareerPerformanceScorer.Category.Feeder3, 1, 32, true));

        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> scores =
            CareerPerformanceScorer.ComputeScores(events);
        scores[1].TotalPoints.ShouldBe(fiveP6);
        scores[2].TotalPoints.ShouldBe(smallTitles);
        scores[1].TotalPoints.ShouldBeGreaterThan(scores[2].TotalPoints);

        var map = new Dictionary<int, CareerPerformanceScorer.AthleteCareerScore>(scores);
        SelectionCareerPrestige.PrestigeRawFor(1, map).ShouldBe(fiveP6);
        SelectionCareerPrestige.PrestigeRawFor(2, map).ShouldBe(smallTitles);
    }

    [Fact]
    public void LongCareer_EdgeCases_StayPositiveDeterministic()
    {
        var empty = new Dictionary<int, CareerPerformanceScorer.AthleteCareerScore>();
        SelectionCareerPrestige.PrestigeRawFor(99, empty).ShouldBe(0);
        SelectionCareerPrestige.BreakdownFor(99, empty,
            out int s0, out int f10, out int f20, out int f30, out int ci0, out int ct0, out int tt0);
        new[] { s0, f10, f20, f30, ci0, ct0, tt0 }.ShouldAllBe(x => x == 0);

        var events = new List<CareerPerformanceScorer.PlacementEvent>();
        for (int season = 1; season <= 30; season++)
        {
            events.Add(new CareerPerformanceScorer.PlacementEvent(5, season, CareerPerformanceScorer.Category.Superleague, 32, 32, true));
        }

        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> first =
            CareerPerformanceScorer.ComputeScores(events);
        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> second =
            CareerPerformanceScorer.ComputeScores(events.AsEnumerable().Reverse().ToList());
        first[5].TotalPoints.ShouldBe(second[5].TotalPoints);
        first[5].TotalPoints.ShouldBe(checked(30 * Score(CareerPerformanceScorer.Category.Superleague, 32, 32)));
        first[5].TotalPoints.ShouldBeGreaterThan(0);
        first[5].ScoredEvents.ShouldBe(30);

        SelectionCareerPrestige.BreakdownFor(5, first,
            out int s, out int f1, out int f2, out int f3, out int ci, out int ct, out int tt);
        checked(s + f1 + f2 + f3 + ci + ct + tt).ShouldBe(first[5].TotalPoints);
        s.ShouldBe(first[5].TotalPoints);
        f1.ShouldBe(0);
    }

    [Fact]
    public void NoMedalPlacementDoubleCount_OneFinishCountsOnce()
    {
        var single = new List<CareerPerformanceScorer.PlacementEvent>
        {
            new(11, 4, CareerPerformanceScorer.Category.Superleague, 1, 32, true),
        };
        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> scores =
            CareerPerformanceScorer.ComputeScores(single);
        scores[11].TotalPoints.ShouldBe(1500);
        scores[11].SuperleagueTitles.ShouldBe(1);
        scores[11].ScoredEvents.ShouldBe(1);

        int finalPoints = Score(CareerPerformanceScorer.Category.TypeCupTeam, 1, 4, true);
        int qualPoints = Score(CareerPerformanceScorer.Category.TypeCupTeam, 1, 4, false);
        finalPoints.ShouldBe(225);
        qualPoints.ShouldBe(67);
        finalPoints.ShouldBeGreaterThan(qualPoints);
    }

    [Fact]
    public void CareerBreakdown_SumsToRaw_ForBothPaths()
    {
        var events = new List<CareerPerformanceScorer.PlacementEvent>
        {
            new(21, 7, CareerPerformanceScorer.Category.Superleague, 1, 32, true),
            new(21, 7, CareerPerformanceScorer.Category.ColorCupIndividual, 5, 32, true),
            new(21, 8, CareerPerformanceScorer.Category.ColorCupTeam, 2, 8, true),
            new(21, 8, CareerPerformanceScorer.Category.TypeCupTeam, 1, 4, true),
            new(21, 8, CareerPerformanceScorer.Category.Feeder1, 5, 32, true),
        };
        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> scores =
            CareerPerformanceScorer.ComputeScores(events);
        CareerPerformanceScorer.AthleteCareerScore career = scores[21];

        var colorCandidate = new ColorCupSelectionReportDocument.Candidate(
            21, 1, 900, 1000, 800, 600, 1000, 5000, 8000, 44000, career.TotalPoints,
            "Superleague", (int)LeagueLevel.Superleague, 1000, 10_000, 55_000,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            career.SuperleaguePoints, career.Feeder1Points, career.Feeder2Points, career.Feeder3Points,
            career.ColorCupIndividualPoints, career.ColorCupTeamPoints, career.TypeCupTeamPoints);
        int colorSum = colorCandidate.PrestigeCareerSuperleaguePoints + colorCandidate.PrestigeCareerFeeder1Points
            + colorCandidate.PrestigeCareerFeeder2Points + colorCandidate.PrestigeCareerFeeder3Points
            + colorCandidate.PrestigeCareerColorIndividualPoints + colorCandidate.PrestigeCareerColorTeamPoints
            + colorCandidate.PrestigeCareerTypeTeamPoints;
        colorSum.ShouldBe(colorCandidate.PrestigeRaw);
        colorSum.ShouldBe(career.TotalPoints);

        var typeCandidate = new TypeCupSelectionReportDocument.Candidate(
            21, 1, 1, "Wizard", false, [], 950, 1000, 900, 800, 1000, 6000, 6000, 33000, career.TotalPoints,
            "Superleague", (int)LeagueLevel.Superleague, 1000, 10_000, 55_000,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            career.SuperleaguePoints, career.Feeder1Points, career.Feeder2Points, career.Feeder3Points,
            career.ColorCupIndividualPoints, career.ColorCupTeamPoints, career.TypeCupTeamPoints);
        int typeSum = typeCandidate.PrestigeCareerSuperleaguePoints + typeCandidate.PrestigeCareerFeeder1Points
            + typeCandidate.PrestigeCareerFeeder2Points + typeCandidate.PrestigeCareerFeeder3Points
            + typeCandidate.PrestigeCareerColorIndividualPoints + typeCandidate.PrestigeCareerColorTeamPoints
            + typeCandidate.PrestigeCareerTypeTeamPoints;
        typeSum.ShouldBe(typeCandidate.PrestigeRaw);
        typeSum.ShouldBe(colorSum);
    }

    [Fact]
    public void V4Reports_CarryCareerWeights()
    {
        RulesV3 rules = RulesV3.CreateDefault();
        rules.CupPrestigeWeightPermille.ShouldBe(100);
        ColorCupSelectionReportDocument.Candidate career = BuildV4Candidate();
        int careerSum = SumCareer(copy: career);
        careerSum.ShouldBe(1420);
        careerSum.ShouldBe(career.PrestigeRaw);

        var v4 = new ColorCupSelectionReportDocument(
            ColorCupSelectionReportDocument.PayloadVersion, 350, 300, 250, 100,
            [new ColorCupSelectionReportDocument.Team(0, 1, [career])],
            CareerPerformanceScorer.WeightsVersion);
        v4.Version.ShouldBe(4);
        v4.PrestigeWeightsVersion.ShouldBe("MSS-083-v1");
        ColorCupSelectionReportDocument restoredV4 = ColorCupSelectionReportDocument.FromStored(v4.ToStored());
        restoredV4.Version.ShouldBe(4);
        restoredV4.PrestigeWeightsVersion.ShouldBe("MSS-083-v1");
        ColorCupSelectionReportDocument.Candidate back = restoredV4.Teams[0].Ranking[0];
        SumCareer(back).ShouldBe(back.PrestigeRaw);
    }

    [Fact]
    public void V3Reports_RemainReadable()
    {
        var legacy = new ColorCupSelectionReportDocument.Candidate(
            7, 1, 900, 1000, 800, 600, 1000, 5000, 8000, 44000, 1420,
            "White League", (int)LeagueLevel.Feeder1, 800, 10_000, 55_000,
            0, 400, 200, 100, 80, 0, 40, 0, 0, 600);
        var v3 = new ColorCupSelectionReportDocument(
            3, 350, 300, 250, 100,
            [new ColorCupSelectionReportDocument.Team(0, 1, [legacy])]);
        ColorCupSelectionReportDocument restoredV3 = ColorCupSelectionReportDocument.FromStored(v3.ToStored());
        restoredV3.Version.ShouldBe(3);
        restoredV3.PrestigeWeightsVersion.ShouldBe("");
        ColorCupSelectionReportDocument.Candidate legacyBack = restoredV3.Teams[0].Ranking[0];
        int legacySum = legacyBack.PrestigeSuperTitleRaw + legacyBack.PrestigeFeeder1TitleRaw
            + legacyBack.PrestigeFeeder2TitleRaw + legacyBack.PrestigeFeeder3TitleRaw
            + legacyBack.PrestigeAppearanceRaw + legacyBack.PrestigeSuperStageRaw
            + legacyBack.PrestigeFeeder1StageRaw + legacyBack.PrestigeFeeder2StageRaw
            + legacyBack.PrestigeFeeder3StageRaw + legacyBack.PrestigeMajorCupRaw;
        legacySum.ShouldBe(legacyBack.PrestigeRaw);
        legacyBack.PrestigeCareerSuperleaguePoints.ShouldBe(0);
    }

    private static ColorCupSelectionReportDocument.Candidate BuildV4Candidate() => new(
        7, 1, 900, 1000, 800, 600, 1000, 5000, 8000, 44000, 1420,
        "White League", (int)LeagueLevel.Feeder1, 800, 10_000, 55_000,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        800, 400, 100, 50, 30, 20, 20);

    private static int SumCareer(ColorCupSelectionReportDocument.Candidate copy) =>
        copy.PrestigeCareerSuperleaguePoints + copy.PrestigeCareerFeeder1Points
            + copy.PrestigeCareerFeeder2Points + copy.PrestigeCareerFeeder3Points
            + copy.PrestigeCareerColorIndividualPoints + copy.PrestigeCareerColorTeamPoints
            + copy.PrestigeCareerTypeTeamPoints;

    [Fact]
    public void BothSelectionPaths_AreDeterministic_OnIdenticalCareerRaws()
    {
        RulesV3 rules = RulesV3.CreateDefault();
        List<ColorCupSelection.CandidateRaw> colorCandidates =
        [
            new(1, "Alpha", 0, 1000, 8000, 44000, 4215),
            new(2, "Beta", 0, 1000, 6400, 35200, 1050),
            new(3, "Gamma", 0, 1000, 4000, 22000, 0),
            new(4, "Delta", 0, 1000, 4000, 22000, 0),
        ];
        IReadOnlyList<ColorCupSelection.ScoredCandidate> first = ColorCupSelection.RankAll(colorCandidates, rules);
        IReadOnlyList<ColorCupSelection.ScoredCandidate> second = ColorCupSelection.RankAll(colorCandidates, rules);
        first.Select(c => c.AthleteId).ShouldBe(second.Select(c => c.AthleteId));
        first[0].Name.ShouldBe("Alpha");

        List<TypeCupAllocation.CandidateRaw> typeCandidates =
        [
            new(1, "Alpha", ["Wizard"], null, 1000, 8000, 44000, 4215),
            new(2, "Beta", ["Wizard"], null, 1000, 6400, 35200, 1050),
            new(3, "Gamma", ["Wizard"], null, 1000, 4000, 22000, 0),
            new(4, "Delta", ["Wizard"], null, 1000, 4000, 22000, 0),
        ];
        TypeCupAllocation.AllocationResult firstAlloc = TypeCupAllocation.Allocate(typeCandidates, rules);
        TypeCupAllocation.AllocationResult secondAlloc = TypeCupAllocation.Allocate(typeCandidates, rules);
        firstAlloc.Teams[0].Members.Select(m => m.AthleteId)
            .ShouldBe(secondAlloc.Teams[0].Members.Select(m => m.AthleteId));
    }

    [Fact]
    public async Task SelectionPrestige_MatchesHallOfFame_ForSameSourceCap()
    {
        var (store, root) = Tests.Features.TestSaveStores.CreateStore("mtgsolosports-career-sel-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Career Selection Parity", 4242UL, 777UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            List<int> ids = await TakeAthletesAsync(store, saveId, 12);
            await SeedTwoSeasonsAsync(store, saveId, ids);

            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                CareerPerformanceLoader.LoadResult hallOfFame = await CareerPerformanceLoader.LoadAsync(
                    context, 7, 8, CancellationToken.None);
                IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> hallScores =
                    CareerPerformanceScorer.ComputeScores(hallOfFame.Events);

                IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> selectionScores =
                    await CareerPerformanceLoader.LoadScoresUpToAsync(
                        context, 8, ids, CancellationToken.None);

                selectionScores.Keys.ShouldBe(hallScores.Keys, ignoreOrder: true);
                foreach (int athleteId in hallScores.Keys)
                {
                    selectionScores[athleteId].ShouldBe(hallScores[athleteId]);
                    SelectionCareerPrestige.PrestigeRawFor(athleteId, selectionScores)
                        .ShouldBe(hallScores[athleteId].TotalPoints);
                }

                SelectionCareerPrestige.PrestigeRawFor(999_001, selectionScores).ShouldBe(0);
            }

            GetHallOfFameHandler handler = new(store);
            GetHallOfFameResponse range = await handler.HandleAsync(saveId, 100, 7, 8);
            range.Leaders.ShouldNotBeEmpty();
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> viaSelection =
                    await CareerPerformanceLoader.LoadScoresUpToAsync(
                        context, 8, athleteIds: null, CancellationToken.None);
                foreach (HallOfFameEntry leader in range.Leaders)
                {
                    viaSelection[leader.AthleteId].TotalPoints.ShouldBe(leader.CareerPoints);
                }
            }
        }
        finally
        {
            Tests.Features.TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task<List<int>> TakeAthletesAsync(SaveStore store, Guid saveId, int count)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SaveAthletes
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .Take(count)
            .Select(e => e.Id)
            .ToListAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task SeedTwoSeasonsAsync(SaveStore store, Guid saveId, List<int> ids)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        (SeasonEntity season7, SeasonEntity season8) = await CreateSeasonsAsync(context).ConfigureAwait(false);
        (LeagueEntity sl7, LeagueEntity f1_7, LeagueEntity sl8, LeagueEntity f1_8) =
            await CreateLeaguesAsync(context, season7, season8).ConfigureAwait(false);
        AddLeagueStandings(context, season7, season8, sl7, f1_7, sl8, f1_8, ids);
        AddColorCups(context, season7, ids);
        AddTypeCup(context, season8, ids);
        AddChampionHonour(context, season7, sl7, ids);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<(SeasonEntity, SeasonEntity)> CreateSeasonsAsync(SaveDbContext context)
    {
        SeasonEntity season7 = new() { SeasonNumber = 7, HasSuperleague = true, IsComplete = true };
        SeasonEntity season8 = new() { SeasonNumber = 8, HasSuperleague = true, IsComplete = true };
        context.Seasons.AddRange(season7, season8);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return (season7, season8);
    }

    private static async Task<(LeagueEntity, LeagueEntity, LeagueEntity, LeagueEntity)> CreateLeaguesAsync(
        SaveDbContext context, SeasonEntity season7, SeasonEntity season8)
    {
        LeagueEntity sl7 = new() { SeasonId = season7.Id, Kind = (int)LeagueKind.Superleague, SportingColor = 0, FeederDivision = 0, Name = "Super S7" };
        LeagueEntity f1_7 = new() { SeasonId = season7.Id, Kind = (int)LeagueKind.Feeder, SportingColor = 0, FeederDivision = 1, Name = "F1 S7" };
        LeagueEntity sl8 = new() { SeasonId = season8.Id, Kind = (int)LeagueKind.Superleague, SportingColor = 0, FeederDivision = 0, Name = "Super S8" };
        LeagueEntity f1_8 = new() { SeasonId = season8.Id, Kind = (int)LeagueKind.Feeder, SportingColor = 0, FeederDivision = 1, Name = "F1 S8" };
        context.Leagues.AddRange(sl7, f1_7, sl8, f1_8);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return (sl7, f1_7, sl8, f1_8);
    }

    private static void AddLeagueStandings(
        SaveDbContext context,
        SeasonEntity season7, SeasonEntity season8,
        LeagueEntity sl7, LeagueEntity f1_7, LeagueEntity sl8, LeagueEntity f1_8,
        List<int> ids)
    {
        for (int i = 0; i < 4; i++)
        {
            context.SeasonStandings.Add(Standing(season7.Id, sl7.Id, ids[i], i + 1));
            context.SeasonStandings.Add(Standing(season7.Id, f1_7.Id, ids[4 + i], i + 1));
            context.SeasonStandings.Add(Standing(season8.Id, sl8.Id, ids[4 + i], i + 1));
            context.SeasonStandings.Add(Standing(season8.Id, f1_8.Id, ids[i], i + 1));
        }
    }

    private static void AddColorCups(SaveDbContext context, SeasonEntity season7, List<int> ids)
    {
        AddColorIndividual(context, season7, ids);
        AddColorTeam(context, season7, ids);
    }

    private static void AddColorIndividual(SaveDbContext context, SeasonEntity season7, List<int> ids)
    {
        for (int i = 0; i < 8; i++)
        {
            context.ColorCupIndividualStandings.Add(new ColorCupIndividualStandingEntity
            {
                SourceSeasonId = season7.Id,
                SourceSeasonNumber = 7,
                SaveAthleteId = ids[i],
                CupRank = i + 1,
                CupScoreThousandths = 0,
                BaseScoreThousandths = 0,
                RoundWins = 0,
                RoundPlaceCountsJson = "[]",
                Medal = 0,
                SportingColor = 0,
                SelectionRank = 1,
            });
        }
    }

    private static void AddColorTeam(SaveDbContext context, SeasonEntity season7, List<int> ids)
    {
        context.ColorCupTeamStandings.Add(new ColorCupTeamStandingEntity
        {
            SourceSeasonId = season7.Id,
            SourceSeasonNumber = 7,
            SportingColor = 0,
            TeamRank = 1,
            TeamScoreThousandths = 0,
            TeamBaseThousandths = 0,
            GroupWins = 0,
            RoundWins = 0,
            GroupPlaceCountsJson = "[]",
            RoundPlaceCountsJson = "[]",
            Medal = 1,
        });
        context.ColorCupTeamStandings.Add(new ColorCupTeamStandingEntity
        {
            SourceSeasonId = season7.Id,
            SourceSeasonNumber = 7,
            SportingColor = 1,
            TeamRank = 2,
            TeamScoreThousandths = 0,
            TeamBaseThousandths = 0,
            GroupWins = 0,
            RoundWins = 0,
            GroupPlaceCountsJson = "[]",
            RoundPlaceCountsJson = "[]",
            Medal = 2,
        });
        context.ColorCupTeamGroupStandings.Add(new ColorCupTeamGroupStandingEntity
        {
            SourceSeasonId = season7.Id,
            SourceSeasonNumber = 7,
            GroupNumber = 1,
            SaveAthleteId = ids[0],
            GroupRank = 1,
            GroupScoreThousandths = 0,
            BaseScoreThousandths = 0,
            RoundWins = 0,
            RoundPlaceCountsJson = "[]",
            SportingColor = 0,
            SelectionRank = 1,
        });
        context.ColorCupTeamGroupStandings.Add(new ColorCupTeamGroupStandingEntity
        {
            SourceSeasonId = season7.Id,
            SourceSeasonNumber = 7,
            GroupNumber = 1,
            SaveAthleteId = ids[4],
            GroupRank = 2,
            GroupScoreThousandths = 0,
            BaseScoreThousandths = 0,
            RoundWins = 0,
            RoundPlaceCountsJson = "[]",
            SportingColor = 1,
            SelectionRank = 1,
        });
    }

    private static void AddTypeCup(SaveDbContext context, SeasonEntity season8, List<int> ids)
    {
        string[] teams = ["Alpha", "Beta", "Gamma", "Delta"];
        for (int i = 0; i < 4; i++)
        {
            context.TypeCupTeamStandings.Add(new TypeCupTeamStandingEntity
            {
                SourceSeasonId = season8.Id,
                SourceSeasonNumber = 8,
                TournamentPhase = 2,
                QualificationGroup = 0,
                CreatureType = teams[i],
                TeamRank = i + 1,
                TeamScoreThousandths = 0,
                TeamBaseThousandths = 0,
                GroupWins = 0,
                RoundWins = 0,
                GroupPlaceCountsJson = "[]",
                RoundPlaceCountsJson = "[]",
                Medal = i < 3 ? i + 1 : 0,
            });
            context.TypeCupTeamGroupStandings.Add(new TypeCupTeamGroupStandingEntity
            {
                SourceSeasonId = season8.Id,
                SourceSeasonNumber = 8,
                TournamentPhase = 2,
                QualificationGroup = 0,
                GroupNumber = 1,
                SaveAthleteId = ids[i],
                GroupRank = i + 1,
                GroupScoreThousandths = 0,
                BaseScoreThousandths = 0,
                RoundWins = 0,
                RoundPlaceCountsJson = "[]",
                CreatureType = teams[i],
                SelectionRank = 1,
            });
        }
    }

    private static void AddChampionHonour(SaveDbContext context, SeasonEntity season7, LeagueEntity sl7, List<int> ids)
    {
        context.Honours.Add(new HonourEntity
        {
            SeasonId = season7.Id,
            SeasonNumber = 7,
            SaveAthleteId = ids[0],
            Kind = (int)HonourKind.SuperleagueTitle,
            LeagueId = sl7.Id,
            LeagueName = "Super S7",
            LeagueKind = (int)LeagueKind.Superleague,
        });
    }

    private static SeasonStandingEntity Standing(int seasonId, int leagueId, int athleteId, int rank) => new()
    {
        SeasonId = seasonId,
        LeagueId = leagueId,
        SaveAthleteId = athleteId,
        SeasonRank = rank,
        TotalChampionshipPointsThousandths = 0,
        TotalStageScoreThousandths = 0,
        TotalBaseScoreThousandths = 0,
        StageWins = 0,
        RoundWins = 0,
        StagePlaceCountsJson = "[]",
        RoundPlaceCountsJson = "[]",
        IsChampion = rank == 1,
    };
}
