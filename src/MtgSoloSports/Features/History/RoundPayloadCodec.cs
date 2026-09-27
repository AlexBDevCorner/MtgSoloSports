using System.IO.Compression;
using System.Text;
using MtgSoloSports.Features.Simulation.AdvanceRound;

namespace MtgSoloSports.Features.History;

/// <summary>
/// Codec for the immutable compact round payload stored on <c>Round.PayloadJson</c>.
/// Historical payloads are plain compact JSON today; the codec also accepts
/// Brotli-compressed payloads prefixed with <c>br1:</c> (Base64) so future
/// compressed writes stay readable without migrating old saves.
/// List/navigation queries must never call this codec; only the exact-round
/// replay path decompresses a single stored payload.
/// </summary>
public static class RoundPayloadCodec
{
    public const string BrotliPrefix = "br1:";

    public static string Encode(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        byte[] raw = Encoding.UTF8.GetBytes(json);
        using MemoryStream output = new();
        using (BrotliStream compressor = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(raw, 0, raw.Length);
        }

        return BrotliPrefix + Convert.ToBase64String(output.ToArray());
    }

    public static string DecodeToJson(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        if (!stored.StartsWith(BrotliPrefix, StringComparison.Ordinal))
        {
            return stored;
        }

        string encoded = stored.Substring(BrotliPrefix.Length);
        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Historical round payload has corrupt compressed encoding.", ex);
        }

        using MemoryStream input = new(compressed);
        using BrotliStream decompressor = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        try
        {
            decompressor.CopyTo(output);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("Historical round payload failed to decompress.", ex);
        }

        string json = Encoding.UTF8.GetString(output.ToArray());
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Historical round payload decompressed to empty content.");
        }

        return json;
    }

    public static RoundPayloadDocument DecodeRound(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        string json = DecodeToJson(stored);
        try
        {
            return RoundPayloadDocument.FromJson(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException("Historical round payload is corrupt and cannot be replayed.", ex);
        }
    }
}
