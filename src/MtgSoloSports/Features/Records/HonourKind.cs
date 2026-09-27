using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records;

/// <summary>
/// Official honour kinds persisted in <c>Honours</c>. Only league championships
/// are major honours before Cups (game-rules §17): eight feeder titles plus one
/// Superleague title per season from Season 2. Stages are statistics, the
/// qualifier is a career event, and Cup honours arrive with Cup tasks.
/// Stored as an integer so future Cup kinds extend without rewriting rows.
/// </summary>
public enum HonourKind
{
    FeederTitle = 0,
    SuperleagueTitle = 1,
}
