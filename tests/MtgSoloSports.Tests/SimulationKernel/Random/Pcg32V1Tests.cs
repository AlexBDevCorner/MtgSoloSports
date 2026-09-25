using MtgSoloSports.SimulationKernel.Random;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Random;

public sealed class Pcg32V1Tests
{
    [Fact]
    public void SameSeed_ProducesIdenticalSequence()
    {
        var first = new Pcg32V1(42UL, 54UL);
        var second = new Pcg32V1(42UL, 54UL);

        for (int i = 0; i < 16; i++)
        {
            first.NextUInt32().ShouldBe(second.NextUInt32());
        }
    }

    [Fact]
    public void GoldenSequence_Seed42Stream54_MatchesExpected()
    {
        uint[] expected =
        [
            2707161783U,
            2068313097U,
            3122475824U,
            2211639955U,
            3215226955U,
            3421331566U,
            3217466285U,
            2167406445U,
        ];

        var rng = new Pcg32V1(42UL, 54UL);
        foreach (uint value in expected)
        {
            rng.NextUInt32().ShouldBe(value);
        }
    }

    [Fact]
    public void GoldenSequence_Seed12345Stream6789_MatchesExpected()
    {
        uint[] expected =
        [
            2829033393U,
            1104653388U,
            3792337479U,
            1239051035U,
            2475503956U,
            3205633020U,
            3608623945U,
            2810263440U,
        ];

        var rng = new Pcg32V1(12345UL, 6789UL);
        foreach (uint value in expected)
        {
            rng.NextUInt32().ShouldBe(value);
        }
    }

    [Fact]
    public void SnapshotRestore_ContinuesIdenticalSequence()
    {
        var rng = new Pcg32V1(999UL, 1UL);
        rng.NextUInt32().ShouldBe(3401471074U);
        rng.NextUInt32().ShouldBe(1453683222U);

        Pcg32State snapshot = rng.Snapshot();
        uint third = rng.NextUInt32();
        uint fourth = rng.NextUInt32();
        third.ShouldBe(1474474707U);
        fourth.ShouldBe(3667367387U);

        Pcg32V1 restored = Pcg32V1.Restore(snapshot);
        restored.NextUInt32().ShouldBe(third);
        restored.NextUInt32().ShouldBe(fourth);
    }

    [Fact]
    public void RestoreFromParts_ProducesIdenticalContinuation()
    {
        var rng = new Pcg32V1(7UL, 11UL);
        uint first = rng.NextUInt32();
        Pcg32State snapshot = rng.Snapshot();

        uint expected = rng.NextUInt32();
        Pcg32V1 restored = Pcg32V1.Restore(snapshot.State, snapshot.Stream);
        restored.NextUInt32().ShouldBe(expected);
        restored.State.ShouldBe(rng.State);
        first.ShouldNotBe(expected);
    }

    [Fact]
    public void NextBounded_StaysWithinBound_AndIsDeterministic()
    {
        var first = new Pcg32V1(2024UL, 99UL);
        var second = new Pcg32V1(2024UL, 99UL);

        for (int i = 0; i < 64; i++)
        {
            uint a = first.NextBounded(32U);
            uint b = second.NextBounded(32U);
            a.ShouldBe(b);
            a.ShouldBeLessThan(32U);
        }
    }

    [Fact]
    public void NextInt32_RejectsNonPositiveBound()
    {
        var rng = new Pcg32V1(1UL, 1UL);
        Should.Throw<ArgumentOutOfRangeException>(() => rng.NextInt32(0));
        Should.Throw<ArgumentOutOfRangeException>(() => rng.NextInt32(-3));
        Should.Throw<ArgumentOutOfRangeException>(() => rng.NextBounded(0U));
    }

    [Fact]
    public void AlgorithmIdentity_IsVersioned()
    {
        Pcg32V1.AlgorithmName.ShouldBe("Pcg32V1");
        Pcg32V1.AlgorithmVersion.ShouldBe(1);
    }
}
