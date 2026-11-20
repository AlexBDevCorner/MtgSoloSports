using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Rules;

/// <summary>
/// MSS-144 regression proof for the rules-snapshot decode boundary. Corrupt
/// persisted values are rewrapped into <see cref="SaveCorruptionException"/>
/// (the 500 corruption class) at the persistence boundary, while the
/// import-only compatibility wrapper keeps converting them to
/// <see cref="InvalidOperationException"/> so an untrusted uploaded artifact
/// stays a 400 client error. The kernel keeps throwing
/// <see cref="InvalidOperationException"/> and its sporting math is unchanged.
/// </summary>
public sealed class RulesSnapshotCorruptionTests
{
    [Fact]
    public void Codec_CorruptPersistedValueV1_ThrowsCorruptionClass()
    {
        string json = RulesSnapshotCodec.Encode(RulesV1.CreateDefault());
        json.ShouldContain("\"leagueSize\":32");
        string corrupt = json.Replace("\"leagueSize\":32", "\"leagueSize\":31");

        SaveCorruptionException ex = Should.Throw<SaveCorruptionException>(() => RulesSnapshotCodec.Decode(corrupt));
        ex.Message.ShouldContain("invalid persisted values", Case.Insensitive);
        ex.Message.ShouldContain("LeagueSize", Case.Insensitive);
    }

    [Fact]
    public void Codec_CorruptPersistedValueV3_ThrowsCorruptionClass()
    {
        string json = RulesSnapshotCodec.Encode(RulesV3.CreateDefault());
        json.ShouldContain("\"feeder3BonusDenominator\":4");
        string corrupt = json.Replace("\"feeder3BonusDenominator\":4", "\"feeder3BonusDenominator\":0");

        SaveCorruptionException ex = Should.Throw<SaveCorruptionException>(() => RulesSnapshotCodec.Decode(corrupt));
        ex.Message.ShouldContain("invalid persisted values", Case.Insensitive);
        ex.Message.ShouldContain("denominator", Case.Insensitive);
    }

    [Fact]
    public void Codec_TamperedJsonType_ThrowsCorruptionClass()
    {
        string json = RulesSnapshotCodec.Encode(RulesV1.CreateDefault());
        string corrupt = json.Replace("\"leagueSize\":32", "\"leagueSize\":\"big\"");

        SaveCorruptionException ex = Should.Throw<SaveCorruptionException>(() => RulesSnapshotCodec.Decode(corrupt));
        ex.Message.ShouldContain("invalid JSON values", Case.Insensitive);
    }

    [Fact]
    public void ImportWrapper_RewrapsCorruptionAsClientErrorClass()
    {
        InvalidOperationException ex = Should.Throw<InvalidOperationException>(
            () => SaveRulesCompatibility.ReadImportableSnapshotRules("{\"version\":99}"));
        ex.Message.ShouldContain("not supported", Case.Insensitive);
        ex.InnerException.ShouldBeOfType<SaveCorruptionException>();
    }
}
