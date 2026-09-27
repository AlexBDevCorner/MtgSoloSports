using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records;

/// <summary>
/// Official honour kinds persisted in <c>Honours</c>. League championships are
/// eight feeder titles plus one Superleague title per season from Season 2;
/// MSS-024 adds the official Color Cup individual championship (game-rules
/// §14/§17), MSS-025 adds the official Color Cup team championship shared
/// by the four members of the winning color team, and MSS-027 adds the official
/// Type Cup team championship shared by the four members of the winning creature-type
/// team (game-rules §15/§17). Stages are statistics and
/// the qualifier is a career event. Stored as an integer so future Cup kinds
/// extend without rewriting rows.
/// </summary>
public enum HonourKind
{
    FeederTitle = 0,
    SuperleagueTitle = 1,
    ColorCupIndividualChampion = 2,
    ColorCupTeamChampion = 3,
    TypeCupTeamChampion = 4,
}
