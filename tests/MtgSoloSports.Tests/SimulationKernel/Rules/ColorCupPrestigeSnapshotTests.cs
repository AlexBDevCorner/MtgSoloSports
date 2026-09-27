using System.Text.Json;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Rules;

public sealed class ColorCupPrestigeSnapshotTests
{
    [Fact]
    public void Snapshot_RoundTrip_PreservesPrestigeConstants()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        string json = RulesSnapshotDocument.FromRules(rules).ToJson();
        RulesV1 restored = RulesSnapshotDocument.FromJson(json).ToRules();

        restored.CupPrestigeFeederTitlePoints.ShouldBe(100);
        restored.CupPrestigeSuperleagueTitlePoints.ShouldBe(300);
        restored.CupPrestigeSuperleagueAppearancePoints.ShouldBe(20);
        restored.CupPrestigeStageWinPoints.ShouldBe(10);
        restored.CupPrestigeStageSecondPoints.ShouldBe(5);
        restored.CupPrestigeStageThirdPoints.ShouldBe(2);
        restored.CupPrestigeOtherMajorHonourPoints.ShouldBe(150);
    }

    [Fact]
    public void Snapshot_BeforePrestige_UpgradesToV1Defaults()
    {
        RulesV1 current = RulesV1.CreateDefault();
        string json = RulesSnapshotDocument.FromRules(current).ToJson();

        // Simulate a pre-MSS-023 snapshot by stripping prestige properties.
        using JsonDocument document = JsonDocument.Parse(json);
        Dictionary<string, JsonElement> kept = [];
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith("cupPrestige", StringComparison.Ordinal)
                && !string.Equals(property.Name, "cupPrestigeWeightPermille", StringComparison.Ordinal))
            {
                continue;
            }

            kept[property.Name] = property.Value.Clone();
        }

        string stripped = JsonSerializer.Serialize(kept);
        RulesV1 restored = RulesSnapshotDocument.FromJson(stripped).ToRules();

        restored.CupPrestigeFeederTitlePoints.ShouldBe(RulesV1.DefaultPrestigeFeederTitlePoints);
        restored.CupPrestigeSuperleagueTitlePoints.ShouldBe(RulesV1.DefaultPrestigeSuperleagueTitlePoints);
        restored.CupPrestigeSuperleagueAppearancePoints.ShouldBe(RulesV1.DefaultPrestigeSuperleagueAppearancePoints);
        restored.CupPrestigeStageWinPoints.ShouldBe(RulesV1.DefaultPrestigeStageWinPoints);
        restored.CupPrestigeStageSecondPoints.ShouldBe(RulesV1.DefaultPrestigeStageSecondPoints);
        restored.CupPrestigeStageThirdPoints.ShouldBe(RulesV1.DefaultPrestigeStageThirdPoints);
        restored.CupPrestigeOtherMajorHonourPoints.ShouldBe(RulesV1.DefaultPrestigeOtherMajorHonourPoints);
    }

    [Fact]
    public void InvalidPrestige_FailsValidation()
    {
        Should.Throw<InvalidOperationException>(() => RulesV1.Create(
            new RulesV1Overrides { CupPrestigeFeederTitlePoints = 999 }));
        Should.Throw<InvalidOperationException>(() => RulesV1.Create(
            new RulesV1Overrides { CupPrestigeSuperleagueTitlePoints = 999 }));
    }
}
