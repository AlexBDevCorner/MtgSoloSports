using System.Security.Cryptography;
using System.Text;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Cups.RunChaosCupGroup;

/// <summary>
/// Pure isolated per-group RNG derivation for one Chaos Cup group (MSS-101).
/// Each (stage, group) pair owns an independent RNG identity derived only from
/// persisted edition and stage-draw checksums, so group simulation order cannot
/// influence other groups and incremental next-round calls produce byte-identical
/// results to an instant complete-group call. The global save RNG is never
/// consumed or modified by group simulation. SHA-256 is a content fingerprint
/// here, not sporting randomness; the derived state seeds the versioned Pcg32V1
/// that drives only the group's round shuffles.
/// </summary>
public static class ChaosCupGroupSimulation
{
    private const string SeedTag = "chaos-group-v1";

    /// <summary>
    /// Derives the group's initial RNG state for round 1 from persisted facts.
    /// Deterministic in (edition checksum, stage draw checksum, stage, group);
    /// distinct groups always diverge via the stage/group suffix. Throws on
    /// corrupt linkage (empty checksums, foreign stage rows, out-of-range group).
    /// </summary>
    public static Pcg32State DeriveGroupRng(
        ChaosCupEditionEntity edition,
        IReadOnlyList<ChaosCupDrawEntity> stageDraws,
        int stageIndex,
        int groupNumber)
    {
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(stageDraws);
        if (stageDraws.Count == 0)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} has no persisted draw to seed group {groupNumber}.");
        }

        int groupCount = ChaosCupFormat.GroupCountForStage(stageIndex);
        if (groupNumber < 1 || groupNumber > groupCount)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {groupNumber} is out of range (1..{groupCount}).");
        }

        if (string.IsNullOrWhiteSpace(edition.EditionChecksum))
        {
            throw new InvalidOperationException("Chaos Cup edition has an empty checksum and cannot seed group RNG.");
        }

        string drawChecksum = stageDraws[0].DrawChecksum;
        if (string.IsNullOrWhiteSpace(drawChecksum))
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw has an empty checksum and cannot seed group RNG.");
        }

        foreach (ChaosCupDrawEntity row in stageDraws)
        {
            if (row.StageIndex != stageIndex)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw contains a foreign stage {row.StageIndex} row.");
            }

            if (!string.Equals(row.DrawChecksum, drawChecksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw has inconsistent checksum metadata.");
            }
        }

        string preimage = string.Concat(
            edition.EditionChecksum, '|',
            stageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), '|',
            groupNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), '|',
            drawChecksum, '|',
            SeedTag);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(preimage));
        ulong state = BitConverter.ToUInt64(hash, 0);
        ulong stream = BitConverter.ToUInt64(hash, 8);
        return new Pcg32State(state, stream);
    }
}
