using MtgSoloSports.Features.Diagnostics.LongRunChecksum;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Diagnostics;

public sealed class LongRunChecksumCalculatorTests
{
    [Fact]
    public void Compute_IsStableRegardlessOfInputOrder()
    {
        List<LongRunChecksumCalculator.SeasonInput> rounds =
        [
            new(1, 2, 1, 1, "aaa"),
            new(1, 1, 1, 1, "bbb"),
        ];
        List<LongRunChecksumCalculator.StageInput> stages =
        [
            new(1, 1, 1, 1, 10, 1000, 77000, 100),
        ];
        List<LongRunChecksumCalculator.SeasonStandingInput> finals = [];
        List<LongRunChecksumCalculator.MembershipInput> memberships =
        [
            new(1, 10, 1),
        ];

        string first = LongRunChecksumCalculator.Compute(rounds, stages, finals, memberships, 1UL, 2UL);
        string reordered = LongRunChecksumCalculator.Compute(
            rounds.AsEnumerable().Reverse().ToList(),
            stages,
            finals,
            memberships.AsEnumerable().Reverse().ToList(),
            1UL,
            2UL);
        string.Equals(first, reordered, StringComparison.Ordinal).ShouldBeTrue();
        first.Length.ShouldBe(64);
    }

    [Fact]
    public void Compute_ChangesWhenSportingResultChanges()
    {
        List<LongRunChecksumCalculator.SeasonInput> rounds = [new(1, 1, 1, 1, "aaa")];
        List<LongRunChecksumCalculator.StageInput> stages = [];
        List<LongRunChecksumCalculator.SeasonStandingInput> finals = [];
        List<LongRunChecksumCalculator.MembershipInput> memberships = [new(1, 1, 1)];

        string before = LongRunChecksumCalculator.Compute(rounds, stages, finals, memberships, 1UL, 2UL);
        string after = LongRunChecksumCalculator.Compute(
            [new(1, 1, 1, 1, "aab")], stages, finals, memberships, 1UL, 2UL);
        string.Equals(before, after, StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void ComputePerSeason_LocalizesDivergence()
    {
        List<LongRunChecksumCalculator.SeasonInput> rounds =
        [
            new(1, 1, 1, 1, "aaa"),
            new(2, 1, 1, 1, "bbb"),
        ];
        IReadOnlyList<(int SeasonNumber, string Checksum)> perSeason =
            LongRunChecksumCalculator.ComputePerSeason(rounds, [], [], [new(1, 1, 1), new(2, 2, 2)]);
        perSeason.Count.ShouldBe(2);
        perSeason[0].SeasonNumber.ShouldBe(1);
        perSeason[1].SeasonNumber.ShouldBe(2);
        string.Equals(perSeason[0].Checksum, perSeason[1].Checksum, StringComparison.Ordinal).ShouldBeFalse();
    }
}
