using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Rules;

public sealed class RulesV2Tests
{
    [Fact]
    public void CreateDefault_UsesTieredVersionWithExactScales()
    {
        RulesV2 rules = RulesV2.CreateDefault();

        Should.NotThrow(() => rules.Validate());
        rules.Version.ShouldBe(2);
        rules.Algorithm.ShouldBe("Pcg32V1");
        rules.GetBonusScale(LeagueLevel.Superleague).ShouldBe(new TierBonusScale(2, 1));
        rules.GetBonusScale(LeagueLevel.Feeder1).ShouldBe(new TierBonusScale(1, 1));
        rules.GetBonusScale(LeagueLevel.Feeder2).ShouldBe(new TierBonusScale(1, 2));
        rules.GetBonusScale(LeagueLevel.Feeder3).ShouldBe(new TierBonusScale(1, 4));
        rules.SupportsLevel(LeagueLevel.Feeder3).ShouldBeTrue();
    }

    [Fact]
    public void V1_SupportsOnlySuperleagueAndFeeder1()
    {
        RulesV1 rules = RulesV1.CreateDefault();

        rules.Version.ShouldBe(1);
        rules.SupportsLevel(LeagueLevel.Superleague).ShouldBeTrue();
        rules.SupportsLevel(LeagueLevel.Feeder1).ShouldBeTrue();
        rules.SupportsLevel(LeagueLevel.Feeder2).ShouldBeFalse();
        rules.SupportsLevel(LeagueLevel.Feeder3).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => rules.GetBonusScale(LeagueLevel.Feeder2));
        Should.Throw<InvalidOperationException>(() => rules.GetBonusScale(LeagueLevel.Feeder3));
    }

    [Fact]
    public void V1Snapshot_RemainsValidThroughCompatibilityPath()
    {
        RulesV1 v1 = RulesV1.CreateDefault();
        string json = RulesSnapshotDocument.FromRules(v1).ToJson();

        RulesV1 decoded = RulesSnapshotCodec.Decode(json);
        decoded.Version.ShouldBe(1);
        decoded.ShouldBeOfType<RulesV1>();
        Should.NotThrow(() => decoded.Validate());

        // Historical v1 math is unchanged: regular 100, Superleague 200.
        MtgSoloSports.SimulationKernel.Scoring.ScoringCalculator
            .RoundBonusForPosition(1, decoded, isSuperleague: false).Thousandths.ShouldBe(100);
        MtgSoloSports.SimulationKernel.Scoring.ScoringCalculator
            .RoundBonusForPosition(1, decoded, isSuperleague: true).Thousandths.ShouldBe(200);
    }

    [Fact]
    public void V2Snapshot_RoundTripsExplicitTierModel()
    {
        RulesV2 rules = RulesV2.CreateDefault();
        string json = RulesSnapshotCodec.Encode(rules);

        RulesV1 decoded = RulesSnapshotCodec.Decode(json);
        RulesV2 tiered = decoded.ShouldBeOfType<RulesV2>();
        tiered.Version.ShouldBe(2);
        Should.NotThrow(() => tiered.Validate());
        tiered.GetBonusScale(LeagueLevel.Feeder2).ShouldBe(new TierBonusScale(1, 2));
        tiered.GetBonusScale(LeagueLevel.Feeder3).ShouldBe(new TierBonusScale(1, 4));

        // Core sporting tables are identical across versions.
        tiered.ScoringTable.ShouldBe(RulesV1.CreateDefault().ScoringTable);
        tiered.RoundBonusThousandths.ShouldBe(RulesV1.CreateDefault().RoundBonusThousandths);
    }

    [Fact]
    public void V2Snapshot_InvalidScale_FailsValidation()
    {
        Should.Throw<InvalidOperationException>(() => RulesV2.Create(
            null,
            feeder2Numerator: 1,
            feeder2Denominator: 3));
        Should.Throw<InvalidOperationException>(() => RulesV2.Create(
            null,
            feeder3Numerator: 1,
            feeder3Denominator: 3));
        Should.Throw<InvalidOperationException>(() => RulesV2.Create(
            null,
            superleagueNumerator: 3,
            superleagueDenominator: 1));
    }

    [Fact]
    public void Codec_RejectsUnknownVersion()
    {
        Should.Throw<InvalidOperationException>(() => RulesSnapshotCodec.Decode("{\"version\":99}"));
    }

    [Fact]
    public void Compatibility_AcceptsV1AndV2Manifests()
    {
        SaveBundleManifest v1Manifest = new(
            SaveBundleManifest.ExpectedFormat,
            SaveBundleManifest.CurrentFormatVersion,
            Guid.NewGuid(),
            "v1",
            DateTimeOffset.UtcNow,
            SaveSchemaVersion.Current,
            1,
            "Pcg32V1",
            1,
            1,
            "SeasonInProgress",
            SaveBundleManifest.DatabaseEntryName,
            new string('a', 64),
            DateTimeOffset.UtcNow,
            "tests");
        SaveBundleManifest v2Manifest = v1Manifest with { RulesVersion = 2 };

        Should.NotThrow(() => SaveRulesCompatibility.EnsureImportableRules(v1Manifest));
        Should.NotThrow(() => SaveRulesCompatibility.EnsureImportableRules(v2Manifest));

        SaveBundleManifest future = v1Manifest with { RulesVersion = 99 };
        Should.Throw<InvalidOperationException>(() => SaveRulesCompatibility.EnsureImportableRules(future));
    }
}
