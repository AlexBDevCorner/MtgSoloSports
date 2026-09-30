using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Seasons.SeasonLifecycle;

/// <summary>
/// Catalog of postseason events that are played in rounds (qualifier and Cup
/// events): stable keys, titles, shapes from the versioned rules, the round
/// cursor and the in-progress RNG chain validation shared by every event's
/// step and one-shot paths. Pure: no persistence, no randomness consumed.
/// </summary>
public static class PostseasonEvents
{
    public const string Qualifier = "qualifier";
    public const string ColorCupIndividual = "color-cup-individual";
    public const string ColorCupTeam = "color-cup-team";
    public const string TypeCupTeam = "type-cup-team";

    public static readonly IReadOnlyList<string> All = [Qualifier, ColorCupIndividual, ColorCupTeam, TypeCupTeam];

    public sealed record EventShape(int GroupCount, int RoundsPerGroup)
    {
        public int TotalRounds => GroupCount * RoundsPerGroup;

        public bool IsGrouped => GroupCount > 1;
    }

    public sealed record PlayedRoundLink(int Group, int Round, Pcg32State Before, Pcg32State After);

    public static bool IsKnown(string? key) => key is not null && All.Contains(key, StringComparer.Ordinal);

    public static string Title(string key) => key switch
    {
        Qualifier => "Superleague qualifier",
        ColorCupIndividual => "Color Cup — individual",
        ColorCupTeam => "Color Cup — team",
        TypeCupTeam => "Type Cup — team",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown postseason event."),
    };

    public static string? KeyForAction(string action) => action switch
    {
        SeasonLifecycleActions.RunQualifier => Qualifier,
        SeasonLifecycleActions.RunColorCupIndividual => ColorCupIndividual,
        SeasonLifecycleActions.RunColorCupTeam => ColorCupTeam,
        SeasonLifecycleActions.RunTypeCupTeam => TypeCupTeam,
        _ => null,
    };

    public static string InProgressPhase(string key) => key switch
    {
        Qualifier => "QualifierInProgress",
        ColorCupIndividual => "ColorCupIndividualInProgress",
        ColorCupTeam => "ColorCupTeamInProgress",
        TypeCupTeam => "TypeCupTeamInProgress",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown postseason event."),
    };

    public static EventShape Shape(string key, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return key switch
        {
            Qualifier => new EventShape(1, rules.QualifierRounds),
            ColorCupIndividual => new EventShape(1, rules.ColorCupIndividualRounds),
            ColorCupTeam => new EventShape(rules.ColorCupTeamSize, rules.ColorCupTeamGroupRounds),
            TypeCupTeam => new EventShape(rules.TypeCupMinTeamSize, rules.TypeCupGroupRounds),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown postseason event."),
        };
    }

    /// <summary>Group and round (both 1-based) of the next round after <paramref name="played"/> rounds.</summary>
    public static (int Group, int Round) Cursor(int played, EventShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentOutOfRangeException.ThrowIfNegative(played);
        return (played / shape.RoundsPerGroup + 1, played % shape.RoundsPerGroup + 1);
    }

    /// <summary>
    /// Validates a partly played event; throws <see cref="InvalidOperationException"/>
    /// on any corruption. Rounds must be contiguous in (group, round) order and
    /// fewer than the total; each round must start where the previous one
    /// ended, or — at a group boundary — where <paramref name="afterGroup"/>
    /// says the previous group's tie-break left the RNG; the save's RNG row must
    /// equal the state after the last persisted step, proving nothing else
    /// consumed randomness mid-event.
    /// </summary>
    public static void ValidateInProgress(
        string key,
        IReadOnlyList<PlayedRoundLink> played,
        EventShape shape,
        Func<int, Pcg32State> afterGroup,
        Pcg32State rngRow)
    {
        ArgumentNullException.ThrowIfNull(played);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(afterGroup);
        string title = Title(key);
        if (played.Count >= shape.TotalRounds)
        {
            throw new InvalidOperationException($"{title} has all {shape.TotalRounds} rounds persisted but no results; sporting state is corrupt.");
        }

        for (int index = 0; index < played.Count; index++)
        {
            PlayedRoundLink link = played[index];
            (int group, int round) = Cursor(index, shape);
            if (link.Group != group || link.Round != round)
            {
                throw new InvalidOperationException(
                    $"{title} round sequence is corrupt: expected group {group} round {round}, found group {link.Group} round {link.Round}.");
            }

            if (index == 0)
            {
                continue;
            }

            Pcg32State expectedBefore = round == 1 ? afterGroup(group - 1) : played[index - 1].After;
            if (link.Before != expectedBefore)
            {
                throw new InvalidOperationException($"{title} RNG chain is broken before group {group} round {round}.");
            }
        }

        if (played.Count == 0)
        {
            return;
        }

        PlayedRoundLink last = played[^1];
        Pcg32State expectedRow = last.Round == shape.RoundsPerGroup && last.Group < shape.GroupCount
            ? afterGroup(last.Group)
            : last.After;
        if (rngRow != expectedRow)
        {
            throw new InvalidOperationException($"{title} is in progress but the save RNG moved since its last round; sporting state is corrupt.");
        }
    }
}
