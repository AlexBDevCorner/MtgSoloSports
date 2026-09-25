using MtgSoloSports.SimulationKernel.Random;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Random;

public sealed class DeterministicShuffleTests
{
    [Fact]
    public void GoldenShuffle_ZeroToNine_MatchesExpected()
    {
        var rng = new Pcg32V1(42UL, 54UL);
        List<int> values = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

        DeterministicShuffle.Shuffle(values, rng);

        values.ShouldBe([8, 2, 6, 4, 5, 1, 7, 0, 9, 3]);
    }

    [Fact]
    public void GoldenShuffle_OneToEight_MatchesExpected()
    {
        var rng = new Pcg32V1(12345UL, 6789UL);
        List<int> values = [1, 2, 3, 4, 5, 6, 7, 8];

        DeterministicShuffle.Shuffle(values, rng);

        values.ShouldBe([3, 8, 6, 5, 1, 4, 7, 2]);
    }

    [Fact]
    public void SameSeed_ProducesIdenticalShuffle()
    {
        List<int> first = [.. Enumerable.Range(0, 32)];
        List<int> second = [.. Enumerable.Range(0, 32)];

        DeterministicShuffle.Shuffle(first, new Pcg32V1(777UL, 13UL));
        DeterministicShuffle.Shuffle(second, new Pcg32V1(777UL, 13UL));

        first.ShouldBe(second);
    }

    [Fact]
    public void RestoredState_ProducesIdenticalShuffle()
    {
        var rng = new Pcg32V1(555UL, 9UL);
        rng.NextUInt32();

        Pcg32State snapshot = rng.Snapshot();
        List<int> first = [.. Enumerable.Range(0, 16)];
        DeterministicShuffle.Shuffle(first, rng);

        Pcg32V1 restored = Pcg32V1.Restore(snapshot);
        List<int> second = [.. Enumerable.Range(0, 16)];
        DeterministicShuffle.Shuffle(second, restored);

        first.ShouldBe(second);
    }

    [Fact]
    public void Shuffle_PreservesAllElements()
    {
        var rng = new Pcg32V1(11UL, 22UL);
        List<int> values = [.. Enumerable.Range(0, 32)];

        DeterministicShuffle.Shuffle(values, rng);

        values.OrderBy(x => x).ShouldBe(Enumerable.Range(0, 32));
    }

    [Fact]
    public void ShuffledCopy_DoesNotMutateSource_AndMatchesInPlace()
    {
        int[] source = [.. Enumerable.Range(0, 12)];
        int[] sourceCopy = (int[])source.Clone();

        int[] shuffled = DeterministicShuffle.ShuffledCopy(source, new Pcg32V1(42UL, 54UL));
        List<int> inPlace = [.. sourceCopy];
        DeterministicShuffle.Shuffle(inPlace, new Pcg32V1(42UL, 54UL));

        source.ShouldBe(sourceCopy);
        shuffled.ShouldBe(inPlace.ToArray());
    }

    [Fact]
    public void SpanOverload_IsDeterministic()
    {
        var rng = new Pcg32V1(42UL, 54UL);
        int[] values = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

        DeterministicShuffle.Shuffle(values.AsSpan(), rng);

        values.ShouldBe([8, 2, 6, 4, 5, 1, 7, 0, 9, 3]);
    }
}
