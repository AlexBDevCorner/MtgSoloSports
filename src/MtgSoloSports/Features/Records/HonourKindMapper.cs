using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records;

/// <summary>
/// Maps league kinds and final placements to honour kinds. Superleague leagues yield
/// <see cref="HonourKind.SuperleagueTitle"/> for rank 1; feeder leagues yield
/// <see cref="HonourKind.FeederTitle"/> for rank 1. MSS-047: ranks 2 and 3 yield
/// runner-up and third-place honours for the same competitions. Unknown kinds or
/// ranks outside 1..3 abort; never silently skipped. Rank 4 or lower is not an honour.
/// </summary>
public static class HonourKindMapper
{
    public static HonourKind FromLeagueKind(int leagueKind)
    {
        return leagueKind == (int)LeagueKind.Superleague
            ? HonourKind.SuperleagueTitle
            : HonourKind.FeederTitle;
    }

    /// <summary>
    /// Resolves the league honour kind for a final season rank 1..3.
    /// </summary>
    public static HonourKind FromLeagueRank(int leagueKind, int seasonRank)
    {
        bool isSuper = leagueKind == (int)LeagueKind.Superleague;
        return seasonRank switch
        {
            1 => isSuper ? HonourKind.SuperleagueTitle : HonourKind.FeederTitle,
            2 => isSuper ? HonourKind.SuperleagueRunnerUp : HonourKind.FeederRunnerUp,
            3 => isSuper ? HonourKind.SuperleagueThirdPlace : HonourKind.FeederThirdPlace,
            _ => throw new ArgumentOutOfRangeException(nameof(seasonRank), $"Season rank {seasonRank} is not a podium honour."),
        };
    }

    /// <summary>
    /// True for a final placement that contributes one honour (1st, 2nd or 3rd).
    /// </summary>
    public static bool IsPodiumRank(int rank) => rank is 1 or 2 or 3;

    /// <summary>
    /// True for honour kinds that represent actual victories (titles/championships).
    /// Prestige, records and title stories must use only these kinds.
    /// </summary>
    public static bool IsChampionKind(int kind) => kind is
        (int)HonourKind.FeederTitle or
        (int)HonourKind.SuperleagueTitle or
        (int)HonourKind.ColorCupIndividualChampion or
        (int)HonourKind.ColorCupTeamChampion or
        (int)HonourKind.TypeCupTeamChampion;

    /// <summary>
    /// True for league podium honours (titles plus runner-up and third place).
    /// </summary>
    public static bool IsLeagueHonourKind(int kind) => kind is
        (int)HonourKind.FeederTitle or
        (int)HonourKind.FeederRunnerUp or
        (int)HonourKind.FeederThirdPlace or
        (int)HonourKind.SuperleagueTitle or
        (int)HonourKind.SuperleagueRunnerUp or
        (int)HonourKind.SuperleagueThirdPlace;

    /// <summary>
    /// Resolves the Color Cup individual honour kind for a final cup rank 1..3.
    /// </summary>
    public static HonourKind FromColorCupIndividualRank(int cupRank) => cupRank switch
    {
        1 => HonourKind.ColorCupIndividualChampion,
        2 => HonourKind.ColorCupIndividualRunnerUp,
        3 => HonourKind.ColorCupIndividualThirdPlace,
        _ => throw new ArgumentOutOfRangeException(nameof(cupRank), $"Cup rank {cupRank} is not a podium honour."),
    };

    /// <summary>
    /// Resolves the Color Cup team honour kind for a final team rank 1..3.
    /// </summary>
    public static HonourKind FromColorCupTeamRank(int teamRank) => teamRank switch
    {
        1 => HonourKind.ColorCupTeamChampion,
        2 => HonourKind.ColorCupTeamRunnerUp,
        3 => HonourKind.ColorCupTeamThirdPlace,
        _ => throw new ArgumentOutOfRangeException(nameof(teamRank), $"Team rank {teamRank} is not a podium honour."),
    };

    /// <summary>
    /// Resolves the Type Cup team honour kind for a final team rank 1..3.
    /// </summary>
    public static HonourKind FromTypeCupTeamRank(int teamRank) => teamRank switch
    {
        1 => HonourKind.TypeCupTeamChampion,
        2 => HonourKind.TypeCupTeamRunnerUp,
        3 => HonourKind.TypeCupTeamThirdPlace,
        _ => throw new ArgumentOutOfRangeException(nameof(teamRank), $"Team rank {teamRank} is not a podium honour."),
    };
}
