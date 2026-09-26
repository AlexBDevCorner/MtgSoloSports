using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.TieBreaking;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.TieBreaking;

public sealed class TieBreakerTests
{
    private static DeterministicTieBreakKey Key(int[] stage, int[] round, long raw) =>
        new(stage, round, raw);

    private static TieBreakKey FullKey(int[] stage, int[] round, long raw, uint draw) =>
        new(stage, round, raw, draw);

    private sealed record Athlete(string Id, DeterministicTieBreakKey Key);

    [Fact]
    public void StageCounts_CompareBestDownward()
    {
        // Two first places beat one first place even with many fewer second places.
        int result = TieBreaker.CompareDeterministic(
            Key([2, 0], [0], 0),
            Key([1, 100], [0], 0));

        result.ShouldBeLessThan(0);
    }

    [Fact]
    public void StageCounts_LaterPositionsBreakTies()
    {
        int result = TieBreaker.CompareDeterministic(
            Key([1, 0, 5], [0], 0),
            Key([1, 0, 3], [0], 0));

        result.ShouldBeLessThan(0);
    }

    [Fact]
    public void RoundCounts_UsedOnlyWhenStageTied()
    {
        int result = TieBreaker.CompareDeterministic(
            Key([1], [3, 0], 0),
            Key([1], [2, 50], 0));

        result.ShouldBeLessThan(0);

        // Round edge cannot overcome a stage deficit.
        int stageDominates = TieBreaker.CompareDeterministic(
            Key([2], [0], 0),
            Key([1], [999], 0));

        stageDominates.ShouldBeLessThan(0);
    }

    [Fact]
    public void RawTotal_UsedOnlyWhenPlacementVectorsTied()
    {
        int result = TieBreaker.CompareDeterministic(
            Key([1, 2], [3, 4], 5000),
            Key([1, 2], [3, 4], 4999));

        result.ShouldBeLessThan(0);

        int placementsDominate = TieBreaker.CompareDeterministic(
            Key([1, 2], [3, 4], 0),
            Key([1, 2], [3, 4], 1_000_000));

        // Vectors are identical here, so raw decides: higher wins.
        placementsDominate.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void FinalDraw_DecidesOnlyFullDeterministicTie()
    {
        int drawDecides = TieBreaker.Compare(
            FullKey([1], [2], 3000, draw: 5),
            FullKey([1], [2], 3000, draw: 9));

        drawDecides.ShouldBeLessThan(0);

        // A better deterministic record wins regardless of a worse draw.
        int deterministicDominates = TieBreaker.Compare(
            FullKey([2], [0], 0, draw: 999),
            FullKey([1], [0], 0, draw: 0));

        deterministicDominates.ShouldBeLessThan(0);
    }

    [Fact]
    public void Compare_FullyEqual_ReturnsZero()
    {
        TieBreaker.Compare(
            FullKey([1, 1], [2, 2], 100, draw: 7),
            FullKey([1, 1], [2, 2], 100, draw: 7)).ShouldBe(0);

        TieBreaker.CompareDeterministic(
            Key([1], [2], 100),
            Key([1], [2], 100)).ShouldBe(0);
    }

    [Fact]
    public void Rank_OrdersBestFirstWithSequentialRanks()
    {
        List<string> entries = ["mid", "best", "worst"];
        IReadOnlyDictionary<string, TieBreakKey> keys = new Dictionary<string, TieBreakKey>(StringComparer.Ordinal)
        {
            ["best"] = FullKey([3], [0], 9000, draw: 99),
            ["mid"] = FullKey([2], [0], 1000, draw: 0),
            ["worst"] = FullKey([1], [0], 1000, draw: 0),
        };

        IReadOnlyList<RankedEntry<string>> ranked = TieBreaker.Rank(entries, e => keys[e]);

        ranked.Select(r => r.Entry).ShouldBe(["best", "mid", "worst"]);
        ranked.Select(r => r.Rank).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void Rank_DuplicateFullTie_ThrowsToProtectDeterminism()
    {
        List<string> entries = ["a", "b"];
        IReadOnlyDictionary<string, TieBreakKey> keys = new Dictionary<string, TieBreakKey>(StringComparer.Ordinal)
        {
            ["a"] = FullKey([1], [1], 100, draw: 42),
            ["b"] = FullKey([1], [1], 100, draw: 42),
        };

        Should.Throw<InvalidOperationException>(() => TieBreaker.Rank(entries, e => keys[e]));
    }

    [Fact]
    public void Rank_Empty_ReturnsEmpty()
    {
        TieBreaker.Rank<string>([], _ => FullKey([0], [0], 0, draw: 0)).ShouldBeEmpty();
        TieBreaker.RankWithSeededDraw<string>(
            [],
            _ => Key([0], [0], 0),
            e => e,
            new Pcg32V1(1UL, 1UL)).ShouldBeEmpty();
    }

    [Fact]
    public void RankWithSeededDraw_SameSeed_IsDeterministic()
    {
        List<Athlete> athletes =
        [
            new("a", Key([1], [1], 100)),
            new("b", Key([1], [1], 100)),
            new("c", Key([1], [1], 100)),
            new("d", Key([0], [0], 0)),
        ];

        IReadOnlyList<RankedEntry<Athlete>> first = TieBreaker.RankWithSeededDraw(
            athletes, a => a.Key, a => a.Id, new Pcg32V1(11UL, 22UL));
        IReadOnlyList<RankedEntry<Athlete>> second = TieBreaker.RankWithSeededDraw(
            athletes, a => a.Key, a => a.Id, new Pcg32V1(11UL, 22UL));

        first.Select(r => r.Entry.Id).ShouldBe(second.Select(r => r.Entry.Id));
        first.Select(r => r.Rank).ShouldBe([1, 2, 3, 4]);

        // The untied last athlete stays last under any seed.
        first[^1].Entry.Id.ShouldBe("d");
    }

    [Fact]
    public void RankWithSeededDraw_DoesNotConsumeRngWithoutTies()
    {
        List<Athlete> athletes =
        [
            new("a", Key([3], [0], 300)),
            new("b", Key([2], [0], 200)),
            new("c", Key([1], [0], 100)),
        ];

        var rng = new Pcg32V1(77UL, 88UL);
        Pcg32State before = rng.Snapshot();

        IReadOnlyList<RankedEntry<Athlete>> ranked = TieBreaker.RankWithSeededDraw(
            athletes, a => a.Key, a => a.Id, rng);

        ranked.Select(r => r.Entry.Id).ShouldBe(["a", "b", "c"]);
        rng.Snapshot().ShouldBe(before);
    }

    [Fact]
    public void RankWithSeededDraw_ConsumesRngOnlyForTiedGroups()
    {
        List<Athlete> untied =
        [
            new("a", Key([2], [0], 0)),
            new("b", Key([1], [0], 0)),
        ];
        List<Athlete> tied =
        [
            new("a", Key([1], [0], 0)),
            new("b", Key([1], [0], 0)),
        ];

        var plainRng = new Pcg32V1(5UL, 6UL);
        Pcg32State plainBefore = plainRng.Snapshot();
        TieBreaker.RankWithSeededDraw(untied, a => a.Key, a => a.Id, plainRng);
        plainRng.Snapshot().ShouldBe(plainBefore);

        var tiedRng = new Pcg32V1(5UL, 6UL);
        Pcg32State tiedBefore = tiedRng.Snapshot();
        TieBreaker.RankWithSeededDraw(tied, a => a.Key, a => a.Id, tiedRng);
        tiedRng.Snapshot().ShouldNotBe(tiedBefore);
    }

    [Fact]
    public void RankWithSeededDraw_IsIndependentOfInputOrder()
    {
        List<Athlete> forward =
        [
            new("a", Key([1], [1], 50)),
            new("b", Key([1], [1], 50)),
            new("c", Key([1], [1], 50)),
        ];
        List<Athlete> reversed = forward.AsEnumerable().Reverse().ToList();

        IReadOnlyList<RankedEntry<Athlete>> first = TieBreaker.RankWithSeededDraw(
            forward, a => a.Key, a => a.Id, new Pcg32V1(9UL, 9UL));
        IReadOnlyList<RankedEntry<Athlete>> second = TieBreaker.RankWithSeededDraw(
            reversed, a => a.Key, a => a.Id, new Pcg32V1(9UL, 9UL));

        first.Select(r => r.Entry.Id).ShouldBe(second.Select(r => r.Entry.Id));
    }

    [Fact]
    public void RankWithSeededDraw_DuplicateIds_Throw()
    {
        List<Athlete> athletes =
        [
            new("same", Key([1], [0], 0)),
            new("same", Key([0], [0], 0)),
        ];

        Should.Throw<InvalidOperationException>(() => TieBreaker.RankWithSeededDraw(
            athletes, a => a.Key, a => a.Id, new Pcg32V1(1UL, 1UL)));
    }

    [Fact]
    public void MismatchedVectorLengths_Throw()
    {
        Should.Throw<InvalidOperationException>(() => TieBreaker.CompareDeterministic(
            Key([1, 2], [0], 0),
            Key([1], [0], 0)));

        Should.Throw<InvalidOperationException>(() => TieBreaker.CompareDeterministic(
            Key([1], [0, 1], 0),
            Key([1], [0], 0)));
    }

    [Fact]
    public void NegativePlacementCounts_Throw()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => TieBreaker.CompareDeterministic(
            Key([-1], [0], 0),
            Key([0], [0], 0)));
    }
}
