using MtgSoloSports.Features.History;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.History;

public sealed class RoundPayloadCodecTests
{
    [Fact]
    public void DecodeRound_PlainJson_ReturnsDocument()
    {
        RoundPayloadDocument document = new(
            RoundPayloadDocument.PayloadVersion,
            1,
            1,
            7,
            2,
            3,
            11UL,
            22UL,
            33UL,
            44UL,
            "abc123",
            []);

        string json = document.ToJson();
        RoundPayloadDocument decoded = RoundPayloadCodec.DecodeRound(json);
        decoded.SeasonNumber.ShouldBe(1);
        decoded.LeagueId.ShouldBe(7);
        decoded.StageNumber.ShouldBe(2);
        decoded.RoundNumber.ShouldBe(3);
        decoded.Checksum.ShouldBe("abc123");
    }

    [Fact]
    public void Codec_Brotli_RoundTrips()
    {
        RoundPayloadDocument document = new(
            RoundPayloadDocument.PayloadVersion,
            1,
            2,
            5,
            1,
            1,
            99UL,
            100UL,
            101UL,
            102UL,
            "checksum",
            []);

        string json = document.ToJson();
        string encoded = RoundPayloadCodec.Encode(json);
        encoded.StartsWith(RoundPayloadCodec.BrotliPrefix, StringComparison.Ordinal).ShouldBeTrue();
        string decodedJson = RoundPayloadCodec.DecodeToJson(encoded);
        decodedJson.ShouldBe(json);
        RoundPayloadDocument decoded = RoundPayloadCodec.DecodeRound(encoded);
        decoded.Checksum.ShouldBe("checksum");
        decoded.SeasonNumber.ShouldBe(2);
    }

    [Fact]
    public void DecodeToJson_CorruptCompressed_ThrowsInvalidOperation()
    {
        Should.Throw<InvalidOperationException>(() =>
            RoundPayloadCodec.DecodeToJson(RoundPayloadCodec.BrotliPrefix + "!!!not-base64!!!"));
    }

    [Fact]
    public void DecodeRound_Empty_ThrowsArgument()
    {
        Should.Throw<ArgumentException>(() => RoundPayloadCodec.DecodeRound(string.Empty));
    }
}
