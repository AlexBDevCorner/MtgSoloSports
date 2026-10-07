using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Athletes.Projections;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads one athlete's career
/// profile from transactional projections plus the save-owned card snapshot,
/// plus official honours, postseason movements, Cup selections and Cup history for the
/// spectator profile view. Never decompresses round payloads; the normal path
/// touches only <c>SaveAthletes</c> + <c>AthleteCareers</c> +
/// <c>AthleteSeasonSummaries</c> + <c>Honours</c> + <c>Movements</c> +
/// Cup selection/standing tables.
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
        List<AthleteCupHistoryDto> cupHistory = await LoadCupHistoryAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        Dictionary<int, (string Level, int Division)> levels = await LoadLeagueLevelsAsync(
            context, summaries, cancellationToken).ConfigureAwait(false);
        return MapPersisted(saveId, athlete, career, summaries, honours, movements, selections, cupHistory, levels);
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
        // Cheap completeness gate: COUNT(*) avoids materializing every season id
        // on the hot profile path. Only when counts match do we probe for a
        // missing season with a single indexed EXISTS query over the athlete's
        // own small covered set.
        int seasonCount = await context.Seasons
            .AsNoTracking()
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
        if (summaries.Count != seasonCount)
        {
            return true;
        }

        HashSet<int> covered = summaries.Select(s => s.SeasonId).ToHashSet();
        return await context.Seasons
            .AsNoTracking()
            .AnyAsync(e => !covered.Contains(e.Id), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static GetAthleteProfileResponse MapPersisted(
        Guid saveId,
        SaveAthleteEntity athlete,
        AthleteCareerEntity career,
        List<AthleteSeasonSummaryEntity> summaries,
        IReadOnlyList<AthleteHonourDto>? honours = null,
        IReadOnlyList<AthleteMovementDto>? movements = null,
        IReadOnlyList<AthleteCupSelectionDto>? selections = null,
        IReadOnlyList<AthleteCupHistoryDto>? cupHistory = null,
        IReadOnlyDictionary<int, (string Level, int Division)>? levelsByLeague = null)
    {
        AthleteCardDto card = MapCard(athlete);
        AthleteCareerDto careerDto = MapCareer(career);
        List<AthleteSeasonDto> seasons = summaries
            .OrderBy(s => s.SeasonNumber)
            .Select(s => MapSeason(s, levelsByLeague))
            .ToList();
        return new GetAthleteProfileResponse(
            saveId,
            athlete.Id,
            card,
            careerDto,
            seasons,
            honours ?? Array.Empty<AthleteHonourDto>(),
            movements ?? Array.Empty<AthleteMovementDto>(),
            selections ?? Array.Empty<AthleteCupSelectionDto>(),
            cupHistory ?? Array.Empty<AthleteCupHistoryDto>());
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
        List<AthleteCupHistoryDto> cupHistory = await LoadCupHistoryAsync(context, athlete.Id, cancellationToken).ConfigureAwait(false);
        return MapFallback(saveId, athlete, history, snapshot.Rules, honours, movements, selections, cupHistory);
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
        IReadOnlyList<AthleteCupSelectionDto>? selections = null,
        IReadOnlyList<AthleteCupHistoryDto>? cupHistory = null)
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
        return MapPersisted(saveId, athlete, career, summaries, honours, movements, selections, cupHistory, ToLevels(history.LeaguesById));
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
        // Official podiums only: legacy single-field rows plus Final rows.
        // Qualification group tables never produce medals/honours.
        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SourceSeasonId) && e.TeamRank >= 1 && e.TeamRank <= 3
                && (e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                    || e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        int count = 0;
        foreach (TypeCupTeamGroupStandingEntity leg in legs)
        {
            // A leg counts only when its own stage is honours-eligible and its
            // team holds the matching official podium: legacy legs match legacy
            // standings, Final legs match Final standings. Qualification legs
            // never count, even when their group table ranks 1-3.
            if (leg.TournamentPhase != (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                && leg.TournamentPhase != (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final)
            {
                continue;
            }

            bool podium = teams.Any(t => t.SourceSeasonId == leg.SourceSeasonId
                && t.TournamentPhase == leg.TournamentPhase
                && t.QualificationGroup == leg.QualificationGroup
                && string.Equals(t.CreatureType, leg.CreatureType, StringComparison.Ordinal));
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
        // Official podiums only: legacy plus Final. Qualification tables never
        // produce honours, even for group winners.
        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SourceSeasonId) && e.TeamRank >= 1 && e.TeamRank <= 3
                && (e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                    || e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<AthleteHonourDto> honours = [];
        foreach (TypeCupTeamGroupStandingEntity leg in legs)
        {
            if (leg.TournamentPhase != (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                && leg.TournamentPhase != (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final)
            {
                continue;
            }

            TypeCupTeamStandingEntity? team = teams.FirstOrDefault(t => t.SourceSeasonId == leg.SourceSeasonId
                && t.TournamentPhase == leg.TournamentPhase
                && t.QualificationGroup == leg.QualificationGroup
                && string.Equals(t.CreatureType, leg.CreatureType, StringComparison.Ordinal));
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

        // Targeted lookups: only the seasons/leagues referenced by this
        // athlete's own movement rows, never the full save dictionaries.
        HashSet<int> seasonIds = rows.SelectMany(r => new[] { r.FromSeasonId, r.ToSeasonId }).ToHashSet();
        Dictionary<int, int> seasonNumbers = await context.Seasons
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber, cancellationToken)
            .ConfigureAwait(false);
        HashSet<int> leagueIds = rows
            .SelectMany(r => new[] { r.FromLeagueId, r.ToLeagueId })
            .Where(id => id != 0)
            .ToHashSet();
        Dictionary<int, LeagueEntity> leaguesById = leagueIds.Count == 0
            ? new Dictionary<int, LeagueEntity>()
            : await context.Leagues
                .AsNoTracking()
                .Where(e => leagueIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, cancellationToken)
                .ConfigureAwait(false);
        Dictionary<int, string> leagueNames = leaguesById.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        List<AthleteMovementDto> movements = new(rows.Count);
        foreach (MovementEntity row in rows)
        {
            seasonNumbers.TryGetValue(row.FromSeasonId, out int fromSeason);
            seasonNumbers.TryGetValue(row.ToSeasonId, out int toSeason);
            movements.Add(MapMovement(row, fromSeason, toSeason, leagueNames, leaguesById));
        }

        return movements
            .OrderBy(e => e.ToSeasonNumber)
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ToList();
    }

    internal static AthleteMovementDto MapMovement(
        MovementEntity row,
        int fromSeason,
        int toSeason,
        IReadOnlyDictionary<int, string> leagueNames,
        IReadOnlyDictionary<int, LeagueEntity> leaguesById)
    {
        string fromLeague = row.FromLeagueId == 0
            ? "Common pool"
            : leagueNames.TryGetValue(row.FromLeagueId, out string? fromName) ? fromName : $"League {row.FromLeagueId}";
        string toLeague = row.ToLeagueId == 0
            ? "Common pool"
            : leagueNames.TryGetValue(row.ToLeagueId, out string? toName) ? toName : $"League {row.ToLeagueId}";
        string? fromLevel = row.FromLeagueId != 0 && leaguesById.TryGetValue(row.FromLeagueId, out LeagueEntity? from)
            ? LeagueEntityLevels.GetLevel(from).ToString()
            : null;
        string? toLevel = row.ToLeagueId != 0 && leaguesById.TryGetValue(row.ToLeagueId, out LeagueEntity? to)
            ? LeagueEntityLevels.GetLevel(to).ToString()
            : null;
        string kind = Enum.IsDefined(typeof(MovementKind), row.Kind)
            ? ((MovementKind)row.Kind).ToString()
            : $"Movement{row.Kind}";
        return new AthleteMovementDto(
            fromSeason,
            toSeason,
            fromLeague,
            toLeague,
            kind,
            row.FromSeasonRank,
            fromLevel,
            toLevel);
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

    internal static async Task<List<AthleteCupHistoryDto>> LoadCupHistoryAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<ColorCupIndividualStandingEntity> individuals = await context.ColorCupIndividualStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupTeamGroupStandingEntity> colorLegs = await context.ColorCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamGroupStandingEntity> typeLegs = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ColorCupTeamStandingEntity> colorTeams = [];
        if (colorLegs.Count > 0)
        {
            HashSet<int> seasonIds = colorLegs.Select(l => l.SourceSeasonId).ToHashSet();
            colorTeams = await context.ColorCupTeamStandings
                .AsNoTracking()
                .Where(e => seasonIds.Contains(e.SourceSeasonId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        List<TypeCupTeamStandingEntity> typeTeams = [];
        if (typeLegs.Count > 0)
        {
            HashSet<int> seasonIds = typeLegs.Select(l => l.SourceSeasonId).ToHashSet();
            typeTeams = await context.TypeCupTeamStandings
                .AsNoTracking()
                .Where(e => seasonIds.Contains(e.SourceSeasonId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return BuildCupHistory(individuals, colorLegs, colorTeams, typeLegs, typeTeams);
    }

    internal static List<AthleteCupHistoryDto> BuildCupHistory(
        IReadOnlyList<ColorCupIndividualStandingEntity> individuals,
        IReadOnlyList<ColorCupTeamGroupStandingEntity> colorLegs,
        IReadOnlyList<ColorCupTeamStandingEntity> colorTeams,
        IReadOnlyList<TypeCupTeamGroupStandingEntity> typeLegs,
        IReadOnlyList<TypeCupTeamStandingEntity> typeTeams)
    {
        ArgumentNullException.ThrowIfNull(individuals);
        ArgumentNullException.ThrowIfNull(colorLegs);
        ArgumentNullException.ThrowIfNull(colorTeams);
        ArgumentNullException.ThrowIfNull(typeLegs);
        ArgumentNullException.ThrowIfNull(typeTeams);
        List<AthleteCupHistoryDto> history = new(individuals.Count + colorLegs.Count + typeLegs.Count);
        history.AddRange(individuals.Select(MapIndividualRow));
        foreach (ColorCupTeamGroupStandingEntity leg in colorLegs)
        {
            AthleteCupHistoryDto? entry = MapColorTeamRow(leg, colorTeams);
            if (entry is not null)
            {
                history.Add(entry);
            }
        }

        foreach (TypeCupTeamGroupStandingEntity leg in typeLegs)
        {
            AthleteCupHistoryDto? entry = MapTypeTeamRow(leg, typeTeams);
            if (entry is not null)
            {
                history.Add(entry);
            }
        }

        return OrderCupHistory(history);
    }

    internal static AthleteCupHistoryDto MapIndividualRow(ColorCupIndividualStandingEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.SourceSeasonNumber <= 0)
        {
            throw new InvalidOperationException($"Color Cup individual standing {row.Id} references invalid season number {row.SourceSeasonNumber}.");
        }

        if (row.CupRank <= 0)
        {
            throw new InvalidOperationException($"Color Cup individual standing {row.Id} has invalid rank {row.CupRank}.");
        }

        SportingColor color = (SportingColor)row.SportingColor;
        string teamKey = CupTeamKeys.ColorKey(color);
        string teamName = CupTeamKeys.ColorName(color);
        return new AthleteCupHistoryDto(
            row.SourceSeasonNumber,
            CupTeamKeys.ColorCup,
            "Individual",
            "Color Cup",
            teamKey,
            teamName,
            row.CupRank,
            ((ColorCupMedal)row.Medal).ToString(),
            row.CupScoreThousandths,
            GroupRank: null,
            GroupNumber: null);
    }

    internal static AthleteCupHistoryDto? MapColorTeamRow(
        ColorCupTeamGroupStandingEntity leg,
        IReadOnlyList<ColorCupTeamStandingEntity> colorTeams)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(colorTeams);
        ColorCupTeamStandingEntity? team = colorTeams.FirstOrDefault(candidate =>
            candidate.SourceSeasonId == leg.SourceSeasonId &&
            candidate.SportingColor == leg.SportingColor);
        if (team is null)
        {
            return null;
        }

        if (leg.SourceSeasonNumber <= 0 || team.SourceSeasonNumber <= 0)
        {
            throw new InvalidOperationException($"Color Cup team leg {leg.Id} references an invalid season number.");
        }

        if (team.TeamRank <= 0)
        {
            throw new InvalidOperationException($"Color Cup team standing {team.Id} has invalid rank {team.TeamRank}.");
        }

        if (leg.SourceSeasonNumber != team.SourceSeasonNumber)
        {
            throw new InvalidOperationException($"Color Cup team leg {leg.Id} season number {leg.SourceSeasonNumber} disagrees with team standing {team.SourceSeasonNumber}.");
        }

        SportingColor color = (SportingColor)leg.SportingColor;
        string teamKey = CupTeamKeys.ColorKey(color);
        string teamName = CupTeamKeys.ColorName(color);
        return new AthleteCupHistoryDto(
            team.SourceSeasonNumber,
            CupTeamKeys.ColorCup,
            "Team",
            "Color Cup Team",
            teamKey,
            teamName,
            team.TeamRank,
            ((ColorCupMedal)team.Medal).ToString(),
            team.TeamScoreThousandths,
            leg.GroupRank,
            leg.GroupNumber);
    }

    internal static AthleteCupHistoryDto? MapTypeTeamRow(
        TypeCupTeamGroupStandingEntity leg,
        IReadOnlyList<TypeCupTeamStandingEntity> typeTeams)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(typeTeams);
        // Phase-aware match: a leg belongs to exactly one tournament stage.
        // Qualification legs match their own group's qualification table so
        // eliminated teams retain participation; Final legs match the Final.
        // Legacy rows (phase 0) match by season+type as before.
        TypeCupTeamStandingEntity? team = typeTeams.FirstOrDefault(candidate =>
            candidate.SourceSeasonId == leg.SourceSeasonId &&
            candidate.TournamentPhase == leg.TournamentPhase &&
            candidate.QualificationGroup == leg.QualificationGroup &&
            string.Equals(candidate.CreatureType, leg.CreatureType, StringComparison.Ordinal));
        if (team is null)
        {
            return null;
        }

        if (leg.SourceSeasonNumber <= 0 || team.SourceSeasonNumber <= 0)
        {
            throw new InvalidOperationException($"Type Cup team leg {leg.Id} references an invalid season number.");
        }

        if (team.TeamRank <= 0)
        {
            throw new InvalidOperationException($"Type Cup team standing {team.Id} has invalid rank {team.TeamRank}.");
        }

        if (leg.SourceSeasonNumber != team.SourceSeasonNumber)
        {
            throw new InvalidOperationException($"Type Cup team leg {leg.Id} season number {leg.SourceSeasonNumber} disagrees with team standing {team.SourceSeasonNumber}.");
        }

        if (string.IsNullOrWhiteSpace(leg.CreatureType))
        {
            throw new InvalidOperationException($"Type Cup team leg {leg.Id} has no creature type.");
        }

        return new AthleteCupHistoryDto(
            team.SourceSeasonNumber,
            CupTeamKeys.TypeCup,
            "Team",
            "Type Cup Team",
            leg.CreatureType,
            leg.CreatureType,
            team.TeamRank,
            ((TypeCupMedal)team.Medal).ToString(),
            team.TeamScoreThousandths,
            leg.GroupRank,
            leg.GroupNumber,
            leg.TournamentPhase,
            leg.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification
                ? leg.QualificationGroup
                : null,
            TournamentStageFor(leg.TournamentPhase, leg.QualificationGroup));
    }

    internal static string TournamentStageFor(int phase, int qualificationGroup)
    {
        if (phase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final)
        {
            return "Final";
        }

        if (phase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification)
        {
            return $"Qualification Group {QualificationGroupLetter(qualificationGroup)}";
        }

        return "Single field";
    }

    internal static string QualificationGroupLetter(int qualificationGroup)
    {
        // Data-driven 1-based letter: 1 -> A, 2 -> B, ... 27 -> AA. Persisted
        // group numbers stay authoritative; the letter is display only.
        if (qualificationGroup < 1)
        {
            return qualificationGroup.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        System.Text.StringBuilder builder = new();
        int value = qualificationGroup;
        while (value > 0)
        {
            value--;
            builder.Insert(0, (char)('A' + (value % 26)));
            value /= 26;
        }

        return builder.ToString();
    }

    internal static List<AthleteCupHistoryDto> OrderCupHistory(List<AthleteCupHistoryDto> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return history
            .OrderByDescending(e => e.SourceSeasonNumber)
            .ThenBy(e => e.Cup, StringComparer.Ordinal)
            .ThenBy(e => e.Event, StringComparer.Ordinal)
            .ThenBy(e => e.TeamKey, StringComparer.Ordinal)
            .ThenBy(e => e.TournamentPhase ?? -1)
            .ThenBy(e => e.QualificationGroup ?? 0)
            .ThenBy(e => e.Place)
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

    internal static AthleteSeasonDto MapSeason(
        AthleteSeasonSummaryEntity summary,
        IReadOnlyDictionary<int, (string Level, int Division)>? levelsByLeague = null)
    {
        string? level = null;
        int? division = null;
        if (summary.LeagueId.HasValue && levelsByLeague is not null
            && levelsByLeague.TryGetValue(summary.LeagueId.Value, out (string Level, int Division) resolved))
        {
            level = resolved.Level;
            division = resolved.Division;
        }

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
            summary.TotalBaseScoreThousandths,
            level,
            division);
    }

    /// <summary>
    /// Targeted tier lookup for one athlete's season leagues: only the league
    /// rows referenced by the athlete's own season summaries, never the full
    /// save dictionary. Tiers derive from league rows, never from names.
    /// </summary>
    internal static async Task<Dictionary<int, (string Level, int Division)>> LoadLeagueLevelsAsync(
        SaveDbContext context,
        List<AthleteSeasonSummaryEntity> summaries,
        CancellationToken cancellationToken)
    {
        HashSet<int> leagueIds = summaries
            .Where(s => s.LeagueId.HasValue)
            .Select(s => s.LeagueId!.Value)
            .ToHashSet();
        if (leagueIds.Count == 0)
        {
            return new Dictionary<int, (string Level, int Division)>();
        }

        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Where(e => leagueIds.Contains(e.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToLevels(leagues.ToDictionary(e => e.Id));
    }

    internal static Dictionary<int, (string Level, int Division)> ToLevels(Dictionary<int, LeagueEntity> leaguesById)
    {
        Dictionary<int, (string Level, int Division)> levels = new(leaguesById.Count);
        foreach ((int id, LeagueEntity league) in leaguesById)
        {
            levels[id] = (LeagueEntityLevels.GetLevel(league).ToString(), league.FeederDivision);
        }

        return levels;
    }
}
