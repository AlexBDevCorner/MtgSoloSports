using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Athletes.Projections;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads one athlete's career
/// profile from transactional projections plus the save-owned card snapshot,
/// plus official honours, postseason movements and Cup selections for the
/// spectator profile view. Never decompresses round payloads; the normal path
/// touches only <c>SaveAthletes</c> + <c>AthleteCareers</c> +
/// <c>AthleteSeasonSummaries</c> + <c>Honours</c> + <c>Movements</c> +
/// Cup selection tables.
/// When projections are missing (saves created before MSS-013), falls back to
/// an in-memory rebuild from normalized standings/memberships for that single
/// athlete — still without round payloads — so old saves remain readable while
/// new writes stay transactional. Corrupt references abort; missing athletes 404.
/// </summary>
public sealed class GetAthleteProfileHandler
{
    private readonly SaveStore _store;

    public GetAthleteProfileHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetAthleteProfileResponse> HandleAsync(Guid saveId, int athleteId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (athleteId <= 0)
        {
            throw new ArgumentException("Athlete id must be positive.", nameof(athleteId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SaveAthleteEntity athlete = await LoadAthleteAsync(context, saveId, athleteId, cancellationToken).ConfigureAwait(false);
        AthleteCareerEntity? career = await LoadCareerAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        List<AthleteSeasonSummaryEntity> summaries = await LoadSummariesAsync(context, athleteId, cancellationToken).ConfigureAwait(false);

        if (career is null || await IsIncompleteAsync(context, summaries, cancellationToken).ConfigureAwait(false))
        {
            return await BuildFallbackAsync(context, saveId, athlete, cancellationToken).ConfigureAwait(false);
        }

        List<AthleteHonourDto> honours = await LoadHonoursAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        List<AthleteMovementDto> movements = await LoadMovementsAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        List<AthleteCupSelectionDto> selections = await LoadCupSelectionsAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        return MapPersisted(saveId, athlete, career, summaries, honours, movements, selections);
    }

    internal static async Task<SaveAthleteEntity> LoadAthleteAsync(
        SaveDbContext context,
        Guid saveId,
        int athleteId,
        CancellationToken cancellationToken)
    {
        SaveAthleteEntity? athlete = await context.SaveAthletes
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == athleteId, cancellationToken)
            .ConfigureAwait(false);
        if (athlete is null)
        {
            throw new AthleteProfileNotFoundException($"Athlete {athleteId} does not exist in save '{saveId:D}'.");
        }

        return athlete;
    }

    internal static Task<AthleteCareerEntity?> LoadCareerAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        return context.AthleteCareers
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SaveAthleteId == athleteId, cancellationToken);
    }

    internal static Task<List<AthleteSeasonSummaryEntity>> LoadSummariesAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        return context.AthleteSeasonSummaries
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken);
    }

    internal static async Task<bool> IsIncompleteAsync(
        SaveDbContext context,
        List<AthleteSeasonSummaryEntity> summaries,
        CancellationToken cancellationToken)
    {
        List<int> seasonIds = await context.Seasons
            .AsNoTracking()
            .Select(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (summaries.Count != seasonIds.Count)
        {
            return true;
        }

        HashSet<int> covered = summaries.Select(s => s.SeasonId).ToHashSet();
        return !seasonIds.All(covered.Contains);
    }

    internal static GetAthleteProfileResponse MapPersisted(
        Guid saveId,
        SaveAthleteEntity athlete,
        AthleteCareerEntity career,
        List<AthleteSeasonSummaryEntity> summaries,
        IReadOnlyList<AthleteHonourDto>? honours = null,
        IReadOnlyList<AthleteMovementDto>? movements = null,
        IReadOnlyList<AthleteCupSelectionDto>? selections = null)
    {
        AthleteCardDto card = MapCard(athlete);
        AthleteCareerDto careerDto = MapCareer(career);
        List<AthleteSeasonDto> seasons = summaries
            .OrderBy(s => s.SeasonNumber)
            .Select(MapSeason)
            .ToList();
        return new GetAthleteProfileResponse(
            saveId,
            athlete.Id,
            card,
            careerDto,
            seasons,
            honours ?? Array.Empty<AthleteHonourDto>(),
            movements ?? Array.Empty<AthleteMovementDto>(),
            selections ?? Array.Empty<AthleteCupSelectionDto>());
    }

    internal static async Task<GetAthleteProfileResponse> BuildFallbackAsync(
        SaveDbContext context,
        Guid saveId,
        SaveAthleteEntity athlete,
        CancellationToken cancellationToken)
    {
        // Single-athlete rebuild without persisting and without round payloads.
        // Uses the same pure calculator as transactional writes.
        RulesV1Snapshot snapshot = await LoadRulesSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        AthleteProjectionUpdater.AthleteHistory history =
            await AthleteProjectionUpdater.LoadHistoryAsync(context, athlete.Id, cancellationToken).ConfigureAwait(false);
        List<AthleteHonourDto> honours = await LoadHonoursAsync(context, athlete.Id, cancellationToken).ConfigureAwait(false);
        List<AthleteMovementDto> movements = await LoadMovementsAsync(context, athlete.Id, cancellationToken).ConfigureAwait(false);
        List<AthleteCupSelectionDto> selections = await LoadCupSelectionsAsync(context, athlete.Id, cancellationToken).ConfigureAwait(false);
        return MapFallback(saveId, athlete, history, snapshot.Rules, honours, movements, selections);
    }

    internal sealed record RulesV1Snapshot(SimulationKernel.Rules.RulesV1 Rules);

    internal static async Task<RulesV1Snapshot> LoadRulesSnapshotAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        return new RulesV1Snapshot(rules);
    }

    internal static GetAthleteProfileResponse MapFallback(
        Guid saveId,
        SaveAthleteEntity athlete,
        AthleteProjectionUpdater.AthleteHistory history,
        SimulationKernel.Rules.RulesV1 rules,
        IReadOnlyList<AthleteHonourDto>? honours = null,
        IReadOnlyList<AthleteMovementDto>? movements = null,
        IReadOnlyList<AthleteCupSelectionDto>? selections = null)
    {
        Dictionary<int, SeasonMembershipEntity> membershipBySeason = history.Memberships.ToDictionary(e => e.SeasonId);
        Dictionary<int, SeasonStandingEntity> finalBySeason = history.SeasonRows.ToDictionary(e => e.SeasonId);
        List<AthleteSeasonSummaryEntity> summaries = [];
        foreach (SeasonEntity season in history.Seasons.OrderBy(s => s.SeasonNumber))
        {
            membershipBySeason.TryGetValue(season.Id, out SeasonMembershipEntity? membership);
            finalBySeason.TryGetValue(season.Id, out SeasonStandingEntity? final);
            List<StageStandingEntity> seasonStages = history.StageRows.Where(r => r.SeasonId == season.Id).ToList();
            summaries.Add(AthleteProjectionUpdater.BuildSeasonSummary(
                season, membership, final, seasonStages, history.LeaguesById));
        }

        AthleteCareerEntity career = AthleteProjectionUpdater.BuildCareer(athlete.Id, history, rules);
        return MapPersisted(saveId, athlete, career, summaries, honours, movements, selections);
    }

    internal static async Task<List<AthleteHonourDto>> LoadHonoursAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<HonourEntity> rows = await context.Honours
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.LeagueName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        int leaguePersisted = rows.Count(e => Records.HonourKindMapper.IsLeagueHonourKind(e.Kind));
        int cupPersisted = rows.Count - leaguePersisted;
        int expectedLeague = await context.SeasonStandings
            .AsNoTracking()
            .CountAsync(e => e.SaveAthleteId == athleteId && e.SeasonRank >= 1 && e.SeasonRank <= 3, cancellationToken)
            .ConfigureAwait(false);
        int expectedCups = await CountExpectedCupPodiumsForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        if (leaguePersisted > expectedLeague || cupPersisted > expectedCups)
        {
            throw new InvalidOperationException($"Persisted honours for athlete {athleteId} exceed authoritative podiums; sporting state is corrupt.");
        }

        if (leaguePersisted == expectedLeague && cupPersisted == expectedCups)
        {
            return MapPersistedHonours(rows);
        }

        return await DerivePodiumHonoursAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
    }

    internal static List<AthleteHonourDto> MapPersistedHonours(List<HonourEntity> rows)
    {
        List<AthleteHonourDto> honours = new(rows.Count);
        foreach (HonourEntity row in rows.OrderBy(e => e.SeasonNumber).ThenBy(e => e.LeagueName, StringComparer.Ordinal))
        {
            string kind = Enum.IsDefined(typeof(Records.HonourKind), row.Kind)
                ? ((Records.HonourKind)row.Kind).ToString()
                : $"Honour{row.Kind}";
            honours.Add(new AthleteHonourDto(row.SeasonNumber, row.LeagueName, kind));
        }

        return honours;
    }

    internal static async Task<int> CountExpectedCupPodiumsForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        int individual = await context.ColorCupIndividualStandings
            .AsNoTracking()
            .CountAsync(e => e.SaveAthleteId == athleteId && e.CupRank >= 1 && e.CupRank <= 3, cancellationToken)
            .ConfigureAwait(false);
        int colorTeams = await CountColorTeamPodiumsForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        int typeTeams = await CountTypeTeamPodiumsForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        return checked(individual + colorTeams + typeTeams);
    }

    internal static async Task<int> CountColorTeamPodiumsForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (legs.Count == 0)
        {
            return 0;
        }

        HashSet<int> seasonIds = legs.Select(l => l.SourceSeasonId).ToHashSet();
        List<ColorCupTeamStandingEntity> teams = await context.ColorCupTeamStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SourceSeasonId) && e.TeamRank >= 1 && e.TeamRank <= 3)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        int count = 0;
        foreach (ColorCupTeamGroupStandingEntity leg in legs)
        {
            bool podium = teams.Any(t => t.SourceSeasonId == leg.SourceSeasonId && t.SportingColor == leg.SportingColor);
            if (podium)
            {
                count++;
            }
        }

        return count;
    }

    internal static async Task<int> CountTypeTeamPodiumsForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (legs.Count == 0)
        {
            return 0;
        }

        HashSet<int> seasonIds = legs.Select(l => l.SourceSeasonId).ToHashSet();
        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SourceSeasonId) && e.TeamRank >= 1 && e.TeamRank <= 3)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        int count = 0;
        foreach (TypeCupTeamGroupStandingEntity leg in legs)
        {
            bool podium = teams.Any(t => t.SourceSeasonId == leg.SourceSeasonId && string.Equals(t.CreatureType, leg.CreatureType, StringComparison.Ordinal));
            if (podium)
            {
                count++;
            }
        }

        return count;
    }

    internal static async Task<List<AthleteHonourDto>> DerivePodiumHonoursAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<AthleteHonourDto> honours = [];
        honours.AddRange(await DeriveLeaguePodiumsForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false));
        honours.AddRange(await DeriveIndividualPodiumsForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false));
        honours.AddRange(await DeriveColorTeamPodiumsForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false));
        honours.AddRange(await DeriveTypeTeamPodiumsForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false));
        return honours
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.LeagueName, StringComparer.Ordinal)
            .ThenBy(e => e.HonourKind, StringComparer.Ordinal)
            .ToList();
    }

    internal static async Task<List<AthleteHonourDto>> DeriveLeaguePodiumsForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingEntity> podiums = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId && e.SeasonRank >= 1 && e.SeasonRank <= 3)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (podiums.Count == 0)
        {
            return [];
        }

        HashSet<int> seasonIds = podiums.Select(p => p.SeasonId).ToHashSet();
        HashSet<int> leagueIds = podiums.Select(p => p.LeagueId).ToHashSet();
        Dictionary<int, SeasonEntity> seasons = await context.Seasons
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Where(e => leagueIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        List<AthleteHonourDto> honours = new(podiums.Count);
        foreach (SeasonStandingEntity podium in podiums)
        {
            if (!seasons.TryGetValue(podium.SeasonId, out SeasonEntity? season))
            {
                throw new InvalidOperationException($"Season standing {podium.Id} references unknown season {podium.SeasonId}.");
            }

            if (!leagues.TryGetValue(podium.LeagueId, out LeagueEntity? league))
            {
                throw new InvalidOperationException($"Season standing {podium.Id} references unknown league {podium.LeagueId}.");
            }

            Records.HonourKind kind = Records.HonourKindMapper.FromLeagueRank(league.Kind, podium.SeasonRank);
            honours.Add(new AthleteHonourDto(season.SeasonNumber, league.Name, kind.ToString()));
        }

        return honours;
    }

    internal static async Task<List<AthleteHonourDto>> DeriveIndividualPodiumsForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<ColorCupIndividualStandingEntity> rows = await context.ColorCupIndividualStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId && e.CupRank >= 1 && e.CupRank <= 3)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<AthleteHonourDto> honours = new(rows.Count);
        foreach (ColorCupIndividualStandingEntity row in rows)
        {
            Records.HonourKind kind = Records.HonourKindMapper.FromColorCupIndividualRank(row.CupRank);
            honours.Add(new AthleteHonourDto(row.SourceSeasonNumber, "Color Cup", kind.ToString()));
        }

        return honours;
    }

    internal static async Task<List<AthleteHonourDto>> DeriveColorTeamPodiumsForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (legs.Count == 0)
        {
            return [];
        }

        HashSet<int> seasonIds = legs.Select(l => l.SourceSeasonId).ToHashSet();
        List<ColorCupTeamStandingEntity> teams = await context.ColorCupTeamStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SourceSeasonId) && e.TeamRank >= 1 && e.TeamRank <= 3)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<AthleteHonourDto> honours = [];
        foreach (ColorCupTeamGroupStandingEntity leg in legs)
        {
            ColorCupTeamStandingEntity? team = teams.FirstOrDefault(t => t.SourceSeasonId == leg.SourceSeasonId && t.SportingColor == leg.SportingColor);
            if (team is null)
            {
                continue;
            }

            Records.HonourKind kind = Records.HonourKindMapper.FromColorCupTeamRank(team.TeamRank);
            honours.Add(new AthleteHonourDto(team.SourceSeasonNumber, "Color Cup Team", kind.ToString()));
        }

        return honours;
    }

    internal static async Task<List<AthleteHonourDto>> DeriveTypeTeamPodiumsForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (legs.Count == 0)
        {
            return [];
        }

        HashSet<int> seasonIds = legs.Select(l => l.SourceSeasonId).ToHashSet();
        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SourceSeasonId) && e.TeamRank >= 1 && e.TeamRank <= 3)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<AthleteHonourDto> honours = [];
        foreach (TypeCupTeamGroupStandingEntity leg in legs)
        {
            TypeCupTeamStandingEntity? team = teams.FirstOrDefault(t => t.SourceSeasonId == leg.SourceSeasonId && string.Equals(t.CreatureType, leg.CreatureType, StringComparison.Ordinal));
            if (team is null)
            {
                continue;
            }

            Records.HonourKind kind = Records.HonourKindMapper.FromTypeCupTeamRank(team.TeamRank);
            honours.Add(new AthleteHonourDto(team.SourceSeasonNumber, "Type Cup Team", kind.ToString()));
        }

        return honours;
    }

    internal static async Task<List<AthleteMovementDto>> LoadMovementsAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<MovementEntity> rows = await context.Movements
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .OrderBy(e => e.ToSeasonId)
            .ThenBy(e => e.Kind)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        Dictionary<int, int> seasonNumbers = await context.Seasons
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> leagueNames = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        List<AthleteMovementDto> movements = new(rows.Count);
        foreach (MovementEntity row in rows)
        {
            seasonNumbers.TryGetValue(row.FromSeasonId, out int fromSeason);
            seasonNumbers.TryGetValue(row.ToSeasonId, out int toSeason);
            string fromLeague = row.FromLeagueId == 0
                ? "Common pool"
                : leagueNames.TryGetValue(row.FromLeagueId, out string? fromName) ? fromName : $"League {row.FromLeagueId}";
            string toLeague = row.ToLeagueId == 0
                ? "Common pool"
                : leagueNames.TryGetValue(row.ToLeagueId, out string? toName) ? toName : $"League {row.ToLeagueId}";
            string kind = Enum.IsDefined(typeof(MovementKind), row.Kind)
                ? ((MovementKind)row.Kind).ToString()
                : $"Movement{row.Kind}";
            movements.Add(new AthleteMovementDto(
                fromSeason,
                toSeason,
                fromLeague,
                toLeague,
                kind,
                row.FromSeasonRank));
        }

        return movements
            .OrderBy(e => e.ToSeasonNumber)
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ToList();
    }

    internal static async Task<List<AthleteCupSelectionDto>> LoadCupSelectionsAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<ColorCupSelectionEntity> colorRows = await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .OrderBy(e => e.SourceSeasonNumber)
            .ThenBy(e => e.SelectionRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupSelectionEntity> typeRows = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .OrderBy(e => e.SourceSeasonNumber)
            .ThenBy(e => e.SelectionRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<AthleteCupSelectionDto> selections = new(colorRows.Count + typeRows.Count);
        foreach (ColorCupSelectionEntity row in colorRows)
        {
            string team = Enum.IsDefined(typeof(SportingColor), row.SportingColor)
                ? ((SportingColor)row.SportingColor).ToString()
                : $"Color{row.SportingColor}";
            selections.Add(new AthleteCupSelectionDto("ColorCup", row.SourceSeasonNumber, team, row.SelectionRank));
        }

        foreach (TypeCupSelectionEntity row in typeRows)
        {
            selections.Add(new AthleteCupSelectionDto("TypeCup", row.SourceSeasonNumber, row.CreatureType, row.SelectionRank));
        }

        return selections
            .OrderBy(e => e.SourceSeasonNumber)
            .ThenBy(e => e.CupKind, StringComparer.Ordinal)
            .ThenBy(e => e.SelectionRank)
            .ToList();
    }

    internal static AthleteCardDto MapCard(SaveAthleteEntity athlete)
    {
        List<string>? types = JsonSerializer.Deserialize<List<string>>(athlete.CreatureTypesJson);
        SportingColor color = (SportingColor)athlete.SportingColor;
        return new AthleteCardDto(
            athlete.Name,
            athlete.SportingColor,
            color.ToString(),
            types ?? [],
            athlete.FrontColors,
            athlete.ManaCost,
            athlete.TypeLine,
            athlete.ImageUrl,
            athlete.SetCode,
            athlete.IsArtifact,
            athlete.HasDevoid,
            athlete.HasHybridMana,
            string.IsNullOrWhiteSpace(athlete.TypeCupNationality) ? null : athlete.TypeCupNationality.Trim());
    }

    internal static AthleteCareerDto MapCareer(AthleteCareerEntity career)
    {
        checked
        {
            int podiums = career.StageWins + career.StageSeconds + career.StageThirds;
            return new AthleteCareerDto(
                career.SeasonsActive,
                career.IsActive,
                career.CurrentLeagueId,
                career.CurrentLeagueName,
                career.CurrentLeagueKind,
                career.RoundWins,
                career.StageWins,
                career.StageSeconds,
                career.StageThirds,
                podiums,
                career.BestSeasonFinish,
                career.BestSeasonNumber,
                career.LifetimeEarnedBonusThousandths,
                career.CurrentEffectiveBonusThousandths,
                career.LastSeasonNumber,
                career.LastStageNumber);
        }
    }

    internal static AthleteSeasonDto MapSeason(AthleteSeasonSummaryEntity summary)
    {
        return new AthleteSeasonDto(
            summary.SeasonNumber,
            summary.SeasonId,
            summary.WasActive,
            summary.LeagueId,
            summary.LeagueName,
            summary.LeagueKind,
            summary.RoundWins,
            summary.StageWins,
            summary.StageSeconds,
            summary.StageThirds,
            summary.SeasonRank,
            summary.IsChampion,
            summary.EarnedBonusThousandths,
            summary.TotalChampionshipPointsThousandths,
            summary.TotalStageScoreThousandths,
            summary.TotalBaseScoreThousandths);
    }
}
