namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Pure individual Color Cup format mathematics (MSS-119).
/// Legacy editions play 16 rounds with all 32 selected athletes. The approved
/// phased edition plays 16 qualifying rounds with 32 athletes followed by 16
/// final rounds with the top-16 qualifiers, keeping 100% of qualifying points;
/// places 17-32 are final at the round-16 cutoff. All methods are deterministic
/// fixed-point/integer-only with no RNG, clock, GUID or database ordering.
/// Sporting simulation for the final phase belongs to MSS-120; this type only
/// centralizes sizes, quotas and invariants so historical editions keep their
/// meaning when defaults evolve.
/// </summary>
public static class ColorCupIndividualFormat
{
    /// <summary>
    /// Legacy single-stage format: 16 rounds of 32 athletes.
    /// Persisted as 0 so rows and payloads written before MSS-119 (missing
    /// format field deserializes to 0) read back as legacy without edits.
    /// </summary>
    public const int LegacyFormatVersion = 0;

    /// <summary>
    /// Phased two-stage format: 16 qualifying rounds of 32 plus 16 final
    /// rounds of 16. New editions only; never inferred for historical rows.
    /// </summary>
    public const int PhasedFormatVersion = 1;

    /// <summary>Qualifying rounds per edition (both formats): 16.</summary>
    public const int QualifyingRounds = 16;

    /// <summary>Final rounds in the phased format: 16.</summary>
    public const int FinalRounds = 16;

    /// <summary>Athletes per qualifying round (both formats): 32.</summary>
    public const int QualifyingFieldSize = 32;

    /// <summary>Athletes per phased final round: 16.</summary>
    public const int FinalFieldSize = 16;

    /// <summary>Cutoff rank: top 16 at the qualifying cutoff reach the final.</summary>
    public const int CutoffRank = 16;

    /// <summary>Total athletes in the selected field: 32.</summary>
    public const int FieldSize = 32;

    /// <summary>Total rounds for a format: 16 legacy, 32 phased.</summary>
    public static int TotalRounds(int formatVersion)
    {
        ValidateFormatVersion(formatVersion);
        return IsPhased(formatVersion)
            ? checked(QualifyingRounds + FinalRounds)
            : QualifyingRounds;
    }

    /// <summary>True for the phased 16+16 format.</summary>
    public static bool IsPhased(int formatVersion)
    {
        ValidateFormatVersion(formatVersion);
        return formatVersion == PhasedFormatVersion;
    }

    /// <summary>
    /// Expected placements for a 1-based round number: 32 for rounds 1-16 in
    /// both formats, 16 for phased rounds 17-32.
    /// </summary>
    public static int ExpectedFieldSize(int roundNumber, int formatVersion)
    {
        ValidateFormatVersion(formatVersion);
        ValidateRoundNumber(roundNumber, formatVersion);
        return IsPhased(formatVersion) && roundNumber > QualifyingRounds
            ? FinalFieldSize
            : QualifyingFieldSize;
    }

    /// <summary>True for qualifying rounds 1-16 in either format.</summary>
    public static bool IsQualifyingRound(int roundNumber, int formatVersion) =>
        roundNumber >= 1 && roundNumber <= QualifyingRounds && IsKnownFormat(formatVersion);

    /// <summary>True for phased final rounds 17-32.</summary>
    public static bool IsFinalRound(int roundNumber, int formatVersion)
    {
        ValidateFormatVersion(formatVersion);
        return IsPhased(formatVersion) && roundNumber > QualifyingRounds && roundNumber <= TotalRounds(formatVersion);
    }

    /// <summary>
    /// Expected round appearances for a final Cup rank: 16 for every legacy
    /// athlete; phased finalists (ranks 1-16) appear in all 32 rounds while
    /// eliminated athletes (ranks 17-32) stop at the 16-round cutoff.
    /// </summary>
    public static int ExpectedAppearancesForRank(int cupRank, int formatVersion)
    {
        ValidateFormatVersion(formatVersion);
        if (cupRank < 1 || cupRank > FieldSize)
        {
            throw new InvalidOperationException($"Color Cup rank {cupRank} is out of range.");
        }

        return IsPhased(formatVersion) && cupRank > CutoffRank
            ? QualifyingRounds
            : IsPhased(formatVersion)
                ? checked(QualifyingRounds + FinalRounds)
                : QualifyingRounds;
    }

    /// <summary>True when a round-16 rank reaches the phased final.</summary>
    public static bool QualifiesForFinal(int round16Rank, int formatVersion)
    {
        ValidateFormatVersion(formatVersion);
        if (round16Rank < 1 || round16Rank > FieldSize)
        {
            throw new InvalidOperationException($"Color Cup round-16 rank {round16Rank} is out of range.");
        }

        return !IsPhased(formatVersion) || round16Rank <= CutoffRank;
    }

    /// <summary>True when the played count completes the edition.</summary>
    public static bool IsComplete(int playedCount, int formatVersion)
    {
        ValidateFormatVersion(formatVersion);
        if (playedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(playedCount), "Played round count cannot be negative.");
        }

        return playedCount == TotalRounds(formatVersion);
    }

    /// <summary>Resolves the stored format, treating pre-MSS-119 missing values as legacy.</summary>
    public static int ResolveStored(int? storedFormatVersion)
    {
        if (storedFormatVersion is null or LegacyFormatVersion)
        {
            return LegacyFormatVersion;
        }

        ValidateFormatVersion(storedFormatVersion.Value);
        return storedFormatVersion.Value;
    }

    public static void ValidateFormatVersion(int formatVersion)
    {
        if (!IsKnownFormat(formatVersion))
        {
            throw new InvalidOperationException(
                $"Color Cup individual format version must be {LegacyFormatVersion} (legacy) or {PhasedFormatVersion} (phased), was {formatVersion}.");
        }
    }

    public static void ValidateRoundNumber(int roundNumber, int formatVersion)
    {
        ValidateFormatVersion(formatVersion);
        int total = TotalRounds(formatVersion);
        if (roundNumber < 1 || roundNumber > total)
        {
            throw new InvalidOperationException($"Color Cup round must be 1..{total}, was {roundNumber}.");
        }
    }

    private static bool IsKnownFormat(int formatVersion) =>
        formatVersion == LegacyFormatVersion || formatVersion == PhasedFormatVersion;
}
