namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Current membership of one save athlete for one season. This is the sporting
/// source of truth for "active league vs common pool" and is kept separate from
/// <see cref="SaveAthleteEntity"/>, which remains the permanent card-identity
/// snapshot. <c>LeagueId</c> is null for common-pool members.
/// <c>DrawIndex</c> is the athlete's position (0..255) in its sporting color's
/// shuffled inaugural-draw order; league members hold 0..31 in draw order so the
/// inaugural reveal can replay persisted order without resimulation.
/// </summary>
public sealed class SeasonMembershipEntity
{
    public int Id { get; set; }

    public int SeasonId { get; set; }

    public int? LeagueId { get; set; }

    public int SaveAthleteId { get; set; }

    public int SportingColor { get; set; }

    public int DrawIndex { get; set; }
}
