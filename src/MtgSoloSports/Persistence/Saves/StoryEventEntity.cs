namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One durable structured sporting story event per athlete. MSS-021 persists
/// lightweight narratives derived from meaningful state transitions (first stage
/// win, first league title, repeat-title milestones, first Superleague
/// appearance, promotion, relegation, return from pool). The row stores the
/// stable <see cref="EventType"/> plus structured <see cref="ContextJson"/>
/// rather than only rendered prose; wording is rendered deterministically by
/// <c>Features/Stories/StoryEventRenderer</c> so AI never affects simulation.
/// Rows are emitted transactionally with the sporting operation that causes
/// them. <see cref="DedupKey"/> plus the unique index on
/// <c>(SaveAthleteId, EventType, DedupKey)</c> makes emission idempotent across
/// retries and reloads. <see cref="EventType"/> is a plain string so future
/// Cup and record tasks can add types without rewriting old events.
/// <see cref="SeasonNumber"/> orders dashboard recency; <see cref="StageNumber"/>
/// is set for stage-scoped events and null for season/transition events.
/// </summary>
public sealed class StoryEventEntity
{
    public int Id { get; set; }

    public int SaveAthleteId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string DedupKey { get; set; } = string.Empty;

    public int SeasonNumber { get; set; }

    public int? StageNumber { get; set; }

    public string ContextJson { get; set; } = "{}";
}
