using System.Text.Json;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Catalog.ImportCatalog;

/// <summary>
/// External-data parsing isolated from pure sporting classification.
/// Parses well-defined bulk JSON without network access, filters to eligible
/// creature printings, collapses printings by card name and classifies each
/// athlete via <see cref="SportingColorClassifier"/> from front-face inputs.
/// Double-faced entries always use <c>card_faces[0]</c>; a missing front-face
/// color field is never treated as Colorless but skips that printing with an
/// actionable diagnostic (see MSS-039).
/// </summary>
public static class BulkCatalogParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Front-face view used for eligibility, extraction and classification.
    /// </summary>
    public sealed record FrontFaceData(
        string TypeLine,
        string ManaCost,
        IReadOnlyList<string> Colors,
        string OracleText,
        IReadOnlyList<string> Keywords,
        string? ImageUrl);

    /// <summary>
    /// Parses a bulk JSON array string into raw card records.
    /// </summary>
    public static IReadOnlyList<BulkCardRecord> ParseJson(string bulkJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bulkJson);
        List<BulkCardRecord>? records;
        try
        {
            records = JsonSerializer.Deserialize<List<BulkCardRecord>>(bulkJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Bulk catalog JSON is not a valid card array: {ex.Message}", ex);
        }

        if (records is null)
        {
            throw new InvalidOperationException("Bulk catalog JSON deserialized to null.");
        }

        return records;
    }

    /// <summary>
    /// Parses a bulk JSON array stream into raw card records without network access.
    /// </summary>
    public static async Task<IReadOnlyList<BulkCardRecord>> ParseJsonAsync(Stream bulkJsonStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bulkJsonStream);
        List<BulkCardRecord>? records;
        try
        {
            records = await JsonSerializer.DeserializeAsync<List<BulkCardRecord>>(bulkJsonStream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Bulk catalog JSON is not a valid card array: {ex.Message}", ex);
        }

        if (records is null)
        {
            throw new InvalidOperationException("Bulk catalog JSON deserialized to null.");
        }

        return records;
    }

    /// <summary>
    /// Parses newline-delimited JSON (JSON Lines) into raw card records without
    /// network access. Each non-empty line must be one Scryfall-like card object.
    /// This is the current Scryfall bulk-data file format (decompressed
    /// <c>.jsonl.gz</c>). Lines are parsed one at a time so the raw file is
    /// never buffered into a single string.
    /// </summary>
    public static async Task<IReadOnlyList<BulkCardRecord>> ParseJsonLinesAsync(Stream jsonLinesStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonLinesStream);
        List<BulkCardRecord> records = [];
        using StreamReader reader = new(jsonLinesStream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
        int lineNumber = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            BulkCardRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<BulkCardRecord>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Bulk catalog JSONL is malformed on line {lineNumber}: {ex.Message}", ex);
            }

            if (record is null)
            {
                throw new InvalidOperationException($"Bulk catalog JSONL deserialized to null on line {lineNumber}.");
            }

            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// Parses newline-delimited JSON (JSON Lines) text into raw card records.
    /// Fixture helper for offline tests; production streams via
    /// <see cref="ParseJsonLinesAsync(Stream, CancellationToken)"/>.
    /// </summary>
    public static IReadOnlyList<BulkCardRecord> ParseJsonLines(string jsonLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonLines);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(jsonLines);
        using MemoryStream stream = new(bytes, writable: false);
        return ParseJsonLinesAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Layouts where Scryfall scopes printed colors per face (double-sided cards).
    /// The top-level <c>colors</c> array must never be substituted for the front
    /// face: it is absent or describes both sides. A missing front-face color
    /// field on these layouts is ambiguous and must be rejected, never treated
    /// as Colorless.
    /// </summary>
    private static readonly HashSet<string> DoubleSidedLayouts = new(StringComparer.OrdinalIgnoreCase)
    {
        "transform",
        "modal_dfc",
        "double_faced_token",
        "reversible_card",
    };

    /// <summary>
    /// Resolves the front face of a bulk entry: <c>card_faces[0]</c> when present,
    /// otherwise the top-level fields. Printed colors are resolved with
    /// layout-specific Scryfall semantics (see
    /// <see cref="TryResolveFrontColors"/>); a missing/ambiguous color field is
    /// never silently converted to an empty (Colorless) array.
    /// </summary>
    /// <exception cref="InvalidOperationException">When front-face colors are absent and cannot be safely resolved.</exception>
    public static FrontFaceData ExtractFrontFace(BulkCardRecord card)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (!TryExtractFrontFace(card, out FrontFaceData? front, out string? rejectReason) || front is null)
        {
            throw new InvalidOperationException(rejectReason ?? $"Card '{card.Name}' has ambiguous front-face colors and cannot be classified.");
        }

        return front;
    }

    /// <summary>
    /// Tries to resolve the front face without throwing. Returns false with an
    /// actionable diagnostic when printed colors are absent/ambiguous; the caller
    /// must skip that printing (never default it to Colorless).
    /// </summary>
    public static bool TryExtractFrontFace(BulkCardRecord card, out FrontFaceData? front, out string? rejectReason)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (card.CardFaces is { Count: > 0 })
        {
            BulkCardFace face = card.CardFaces[0];
            if (!TryResolveFrontColors(card, out IReadOnlyList<string>? colors, out rejectReason) || colors is null)
            {
                front = null;
                return false;
            }

            front = new FrontFaceData(
                face.TypeLine ?? string.Empty,
                face.ManaCost ?? string.Empty,
                colors,
                face.OracleText ?? string.Empty,
                face.Keywords ?? [],
                PreferImage(face.ImageUris, card.ImageUris));
            rejectReason = null;
            return true;
        }

        if (!TryResolveFrontColors(card, out IReadOnlyList<string>? topColors, out rejectReason) || topColors is null)
        {
            front = null;
            return false;
        }

        front = new FrontFaceData(
            card.TypeLine ?? string.Empty,
            card.ManaCost ?? string.Empty,
            topColors,
            card.OracleText ?? string.Empty,
            card.Keywords ?? [],
            PreferImage(card.ImageUris, null));
        rejectReason = null;
        return true;
    }

    /// <summary>
    /// Resolves printed front-face colors from real Scryfall field placement.
    /// A present empty array is legitimate Colorless; a missing (null) field is
    /// ambiguous and rejects. Face-level <c>color_indicator</c> is unioned with
    /// <c>colors</c> because indicator-only fronts (for example adventure cards
    /// with a legendary-mana cost) carry their printed color there.
    /// <para>
    /// Layout semantics (verified against live Scryfall records, September 2026):
    /// <list type="bullet">
    /// <item><c>transform</c>/<c>modal_dfc</c>/<c>double_faced_token</c>/<c>reversible_card</c>:
    /// per-face colors are authoritative; the top-level array is absent or covers
    /// both sides and must never be substituted.</item>
    /// <item><c>split</c>/<c>flip</c>/<c>adventure</c>/<c>prepare</c> (single-sided
    /// multi-part cards): faces omit <c>colors</c>; the top-level array carries the
    /// front-face color (<c>adventure</c>/<c>prepare</c>/<c>flip</c> verified
    /// front-authoritative; <c>split</c> is a union but has no creature printings,
    /// so using it can only over-estimate away from Colorless, never create a
    /// false Colorless).</item>
    /// <item>Single-faced cards (no <c>card_faces</c>): the top-level array is authoritative.</item>
    /// </list>
    /// </para>
    /// Mana cost, color identity and artwork are never used as color sources.
    /// </summary>
    internal static bool TryResolveFrontColors(BulkCardRecord card, out IReadOnlyList<string>? colors, out string? rejectReason)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (card.CardFaces is { Count: > 0 })
        {
            BulkCardFace face = card.CardFaces[0];
            bool faceHasColors = face.Colors is not null;
            bool faceHasIndicator = face.ColorIndicator is not null;
            if (faceHasColors || faceHasIndicator)
            {
                colors = UnionColors(face.Colors ?? [], face.ColorIndicator ?? []);
                rejectReason = null;
                return true;
            }

            string layout = (card.Layout ?? string.Empty).Trim();
            if (DoubleSidedLayouts.Contains(layout))
            {
                colors = null;
                rejectReason =
                    $"Card '{card.Name}' (layout '{card.Layout}') omits front-face colors and color_indicator; " +
                    "double-sided layouts require per-face colors, so the printing is skipped instead of defaulting to Colorless.";
                return false;
            }

            bool topHasColors = card.Colors is not null;
            bool topHasIndicator = card.ColorIndicator is not null;
            if (!topHasColors && !topHasIndicator)
            {
                colors = null;
                rejectReason =
                    $"Card '{card.Name}' (layout '{card.Layout}') omits both face-level and top-level colors/color_indicator; " +
                    "the printing is skipped instead of defaulting to Colorless.";
                return false;
            }

            colors = UnionColors(card.Colors ?? [], card.ColorIndicator ?? []);
            rejectReason = null;
            return true;
        }

        bool singleHasColors = card.Colors is not null;
        bool singleHasIndicator = card.ColorIndicator is not null;
        if (!singleHasColors && !singleHasIndicator)
        {
            colors = null;
            rejectReason =
                $"Card '{card.Name}' omits top-level colors and color_indicator; " +
                "the printing is skipped instead of defaulting to Colorless.";
            return false;
        }

        colors = UnionColors(card.Colors ?? [], card.ColorIndicator ?? []);
        rejectReason = null;
        return true;
    }

    internal static IReadOnlyList<string> UnionColors(IReadOnlyList<string> colors, IReadOnlyList<string> indicator)
    {
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(indicator);

        HashSet<string> distinct = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in colors)
        {
            if (!string.IsNullOrWhiteSpace(entry))
            {
                _ = distinct.Add(entry.Trim().ToUpperInvariant());
            }
        }

        foreach (string entry in indicator)
        {
            if (!string.IsNullOrWhiteSpace(entry))
            {
                _ = distinct.Add(entry.Trim().ToUpperInvariant());
            }
        }

        string[] order = ["W", "U", "B", "R", "G"];
        List<string> result = new(distinct.Count);
        foreach (string slot in order)
        {
            if (distinct.Contains(slot))
            {
                result.Add(slot);
            }
        }

        // Preserve validation of unknown letters to the classifier by passing
        // through anything outside WUBRG instead of silently dropping it.
        foreach (string entry in distinct)
        {
            if (Array.IndexOf(order, entry) < 0)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Eligibility: front-face type line must contain Creature and the entry must
    /// not be a token. Tokens are excluded via layout, set_type or type line.
    /// </summary>
    public static bool IsEligible(BulkCardRecord card, FrontFaceData front)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(front);

        if (IsToken(card, front))
        {
            return false;
        }

        return front.TypeLine.Contains("Creature", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds collapsed athlete candidates grouped by exact card name.
    /// Output is sorted by name (ordinal) so persistence order is deterministic.
    /// The first valid printing wins for display metadata; a later valid printing
    /// only fills a missing artwork URL. Printings with absent/ambiguous colors
    /// are skipped entirely, so a malformed first printing can never permanently
    /// win duplicate collapse over a later valid printing.
    /// </summary>
    public static IReadOnlyList<CatalogAthlete> BuildAthletes(IReadOnlyList<BulkCardRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        Dictionary<string, MutableAthlete> collapsed = new(StringComparer.Ordinal);
        foreach (BulkCardRecord card in records)
        {
            AddCard(collapsed, card);
        }

        return ToSortedAthletes(collapsed);
    }

    private static void AddCard(Dictionary<string, MutableAthlete> collapsed, BulkCardRecord card)
    {
        ArgumentNullException.ThrowIfNull(collapsed);
        ArgumentNullException.ThrowIfNull(card);

        if (string.IsNullOrWhiteSpace(card.Name))
        {
            return;
        }

        string name = card.Name.Trim();
        if (name.Length == 0)
        {
            return;
        }

        // Token / non-creature screening uses the front type line only, so a
        // token or sorcery with missing colors is not misreported as ambiguous.
        string frontTypeLine = GetFrontTypeLine(card);
        if (IsTokenByType(card, frontTypeLine))
        {
            return;
        }

        if (!frontTypeLine.Contains("Creature", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TryExtractFrontFace(card, out FrontFaceData? front, out _) || front is null)
        {
            return;
        }

        if (!IsEligible(card, front))
        {
            return;
        }

        if (!collapsed.TryGetValue(name, out MutableAthlete? existing))
        {
            collapsed[name] = CreateAthlete(name, card, front);
            return;
        }

        if (string.IsNullOrWhiteSpace(existing.ImageUrl) && !string.IsNullOrWhiteSpace(front.ImageUrl))
        {
            existing.ImageUrl = front.ImageUrl;
            if (!string.IsNullOrWhiteSpace(card.Set))
            {
                existing.SetCode = card.Set;
            }
        }
    }

    private static MutableAthlete CreateAthlete(string name, BulkCardRecord card, FrontFaceData front)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(front);

        bool isArtifact = IsArtifact(front.TypeLine);
        bool hasDevoid = HasDevoid(front);
        bool hasHybrid = HasHybridMana(front.ManaCost);
        IReadOnlyList<string> normalizedColors = SportingColorClassifier.NormalizeColors(front.Colors);
        SportingColor sportingColor = SportingColorClassifier.Classify(front.Colors, hasHybrid, hasDevoid);
        IReadOnlyList<string> creatureTypes = ExtractCreatureTypes(front.TypeLine);
        return new MutableAthlete(
            name,
            sportingColor,
            creatureTypes,
            isArtifact,
            hasDevoid,
            hasHybrid,
            normalizedColors,
            front.ManaCost,
            front.TypeLine,
            front.ImageUrl,
            card.Set);
    }

    private static IReadOnlyList<CatalogAthlete> ToSortedAthletes(Dictionary<string, MutableAthlete> collapsed)
    {
        ArgumentNullException.ThrowIfNull(collapsed);

        List<string> names = [.. collapsed.Keys];
        names.Sort(StringComparer.Ordinal);
        List<CatalogAthlete> result = new(names.Count);
        foreach (string name in names)
        {
            MutableAthlete mutable = collapsed[name];
            result.Add(new CatalogAthlete(
                mutable.Name,
                mutable.SportingColor,
                mutable.CreatureTypes,
                mutable.IsArtifact,
                mutable.HasDevoid,
                mutable.HasHybridMana,
                mutable.FrontColors,
                mutable.ManaCost,
                mutable.TypeLine,
                mutable.ImageUrl,
                mutable.SetCode));
        }

        return result;
    }

    /// <summary>
    /// Counts raw input, eligible printings and collapsed unique athletes.
    /// Creature candidates with absent/ambiguous colors are reported separately
    /// as <c>SkippedAmbiguousColor</c> and never counted as eligible.
    /// </summary>
    public static (int TotalPrintings, int EligiblePrintings, int UniqueAthletes, int SkippedTokens, int SkippedNonCreature, int SkippedAmbiguousColor) CountCollapse(IReadOnlyList<BulkCardRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        int eligible = 0;
        int skippedTokens = 0;
        int skippedNonCreature = 0;
        int skippedAmbiguous = 0;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (BulkCardRecord card in records)
        {
            if (string.IsNullOrWhiteSpace(card.Name))
            {
                continue;
            }

            string frontTypeLine = GetFrontTypeLine(card);
            if (IsTokenByType(card, frontTypeLine))
            {
                skippedTokens++;
                continue;
            }

            if (!frontTypeLine.Contains("Creature", StringComparison.OrdinalIgnoreCase))
            {
                skippedNonCreature++;
                continue;
            }

            if (!TryExtractFrontFace(card, out FrontFaceData? front, out _) || front is null)
            {
                skippedAmbiguous++;
                continue;
            }

            if (IsToken(card, front))
            {
                skippedTokens++;
                continue;
            }

            if (!front.TypeLine.Contains("Creature", StringComparison.OrdinalIgnoreCase))
            {
                skippedNonCreature++;
                continue;
            }

            eligible++;
            _ = names.Add(card.Name.Trim());
        }

        return (records.Count, eligible, names.Count, skippedTokens, skippedNonCreature, skippedAmbiguous);
    }

    internal static string GetFrontTypeLine(BulkCardRecord card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.CardFaces is { Count: > 0 })
        {
            return card.CardFaces[0]?.TypeLine ?? string.Empty;
        }

        return card.TypeLine ?? string.Empty;
    }

    internal static bool IsTokenByType(BulkCardRecord card, string frontTypeLine)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(frontTypeLine);

        if (string.Equals(card.Layout, "token", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(card.Layout, "double_faced_token", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(card.SetType, "token", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return frontTypeLine.Contains("Token", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsToken(BulkCardRecord card, FrontFaceData front)
    {
        if (string.Equals(card.Layout, "token", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(card.Layout, "double_faced_token", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(card.SetType, "token", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return front.TypeLine.Contains("Token", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsArtifact(string typeLine) =>
        typeLine.Contains("Artifact", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Hybrid presence is defined as a slash inside the front-face mana cost
    /// (for example <c>{W/U}</c>, <c>{2/W}</c>, <c>{W/P}</c>).
    /// </summary>
    internal static bool HasHybridMana(string manaCost) =>
        manaCost.Contains('/', StringComparison.Ordinal);

    internal static bool HasDevoid(FrontFaceData front)
    {
        foreach (string keyword in front.Keywords)
        {
            if (string.Equals(keyword.Trim(), "Devoid", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return front.OracleText.Contains("Devoid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts creature subtypes from the front-face type line: text after the
    /// em dash (U+2014, Scryfall convention) or a plain hyphen fallback,
    /// split on whitespace.
    /// </summary>
    internal static IReadOnlyList<string> ExtractCreatureTypes(string typeLine)
    {
        if (string.IsNullOrWhiteSpace(typeLine))
        {
            return [];
        }

        int dash = typeLine.IndexOf('—');
        if (dash < 0)
        {
            dash = typeLine.IndexOf(" - ", StringComparison.Ordinal);
            if (dash >= 0)
            {
                dash += 1;
            }
        }

        string subtypes = dash >= 0 ? typeLine[(dash + 1)..] : string.Empty;
        string[] parts = subtypes.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> types = new(parts.Length);
        foreach (string part in parts)
        {
            string cleaned = part.Trim();
            if (cleaned.Length == 0 || string.Equals(cleaned, "—", StringComparison.Ordinal) || string.Equals(cleaned, "-", StringComparison.Ordinal))
            {
                continue;
            }

            types.Add(cleaned);
        }

        return types;
    }

    private static string? PreferImage(BulkImageUris? primary, BulkImageUris? fallback)
    {
        string? candidate = primary?.Normal ?? primary?.Large ?? primary?.Small;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = fallback?.Normal ?? fallback?.Large ?? fallback?.Small;
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    private sealed class MutableAthlete
    {
        public MutableAthlete(
            string name,
            SportingColor sportingColor,
            IReadOnlyList<string> creatureTypes,
            bool isArtifact,
            bool hasDevoid,
            bool hasHybridMana,
            IReadOnlyList<string> frontColors,
            string manaCost,
            string typeLine,
            string? imageUrl,
            string? setCode)
        {
            Name = name;
            SportingColor = sportingColor;
            CreatureTypes = creatureTypes;
            IsArtifact = isArtifact;
            HasDevoid = hasDevoid;
            HasHybridMana = hasHybridMana;
            FrontColors = frontColors;
            ManaCost = manaCost;
            TypeLine = typeLine;
            ImageUrl = imageUrl;
            SetCode = setCode;
        }

        public string Name { get; }

        public SportingColor SportingColor { get; }

        public IReadOnlyList<string> CreatureTypes { get; }

        public bool IsArtifact { get; }

        public bool HasDevoid { get; }

        public bool HasHybridMana { get; }

        public IReadOnlyList<string> FrontColors { get; }

        public string ManaCost { get; }

        public string TypeLine { get; }

        public string? ImageUrl { get; set; }

        public string? SetCode { get; set; }
    }
}
