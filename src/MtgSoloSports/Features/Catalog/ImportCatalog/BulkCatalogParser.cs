using System.Text.Json;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Catalog.ImportCatalog;

/// <summary>
/// External-data parsing isolated from pure sporting classification.
/// Parses well-defined bulk JSON without network access, filters to eligible
/// creature printings, collapses printings by card name and classifies each
/// athlete via <see cref="SportingColorClassifier"/> from front-face inputs.
/// Double-faced entries always use <c>card_faces[0]</c>.
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
    /// Resolves the front face of a bulk entry: <c>card_faces[0]</c> when present,
    /// otherwise the top-level fields. Missing collections become empty.
    /// </summary>
    public static FrontFaceData ExtractFrontFace(BulkCardRecord card)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (card.CardFaces is { Count: > 0 })
        {
            BulkCardFace front = card.CardFaces[0];
            return new FrontFaceData(
                front.TypeLine ?? string.Empty,
                front.ManaCost ?? string.Empty,
                front.Colors ?? [],
                front.OracleText ?? string.Empty,
                front.Keywords ?? [],
                PreferImage(front.ImageUris, card.ImageUris));
        }

        return new FrontFaceData(
            card.TypeLine ?? string.Empty,
            card.ManaCost ?? string.Empty,
            card.Colors ?? [],
            card.OracleText ?? string.Empty,
            card.Keywords ?? [],
            PreferImage(card.ImageUris, null));
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
    /// The first printing wins for display metadata; a later printing only fills
    /// a missing artwork URL.
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

        FrontFaceData front = ExtractFrontFace(card);
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
    /// </summary>
    public static (int TotalPrintings, int EligiblePrintings, int UniqueAthletes, int SkippedTokens, int SkippedNonCreature) CountCollapse(IReadOnlyList<BulkCardRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        int eligible = 0;
        int skippedTokens = 0;
        int skippedNonCreature = 0;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (BulkCardRecord card in records)
        {
            if (string.IsNullOrWhiteSpace(card.Name))
            {
                continue;
            }

            FrontFaceData front = ExtractFrontFace(card);
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

        return (records.Count, eligible, names.Count, skippedTokens, skippedNonCreature);
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
