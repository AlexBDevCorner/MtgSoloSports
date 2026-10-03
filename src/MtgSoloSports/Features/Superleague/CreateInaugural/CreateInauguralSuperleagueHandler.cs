using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.CreateInaugural;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Implements the special
/// Season 1 transition as an explicit lifecycle slice: after Season 1 final
/// feeder standings, places 1-4 from each of the eight leagues enter the first
/// 32-athlete Season 2 Superleague. Promoted athletes leave their feeder
/// membership, keep all bonus history (stage/standing rows are untouched), and
/// feeder vacancies stay empty for the rebalancing flow (pool set identical).
/// Persists 32 <see cref="MovementKind.InauguralPromotion"/> movement records,
/// full Season 2 leagues/memberships and refreshed athlete projections in one
/// transaction. No sporting RNG is consumed: selection reads deterministic
/// final ranks. <c>SaveMetadata.CurrentSeason</c> stays at 1; a later slice
/// advances the current season once rebalancing yields valid 32-per-league
/// rosters. Holds one per-save lock; read-only roster queries never lock.
/// </summary>
public sealed class CreateInauguralSuperleagueHandler
{
    public const string SuperleagueName = "Superleague";

    private readonly SaveStore _store;

    public CreateInauguralSuperleagueHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<CreateInauguralSuperleagueResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<CreateInauguralSuperleagueResponse> CreateUnderLockAsync(
        Guid saveId,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity seasonOne = await LoadSeasonOneAsync(context, cancellationToken).ConfigureAwait(false);
        await EnsureSeasonOneCompleteAsync(seasonOne).ConfigureAwait(false);
        await EnsureSeasonTwoAbsentAsync(context, cancellationToken).ConfigureAwait(false);

        List<LeagueEntity> feedersOne = await LoadFeedersOneAsync(context, seasonOne, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonStandingEntity> standings = await LoadStandingsAsync(context, seasonOne, feedersOne, rules, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<InauguralSuperleagueSelection.InauguralPick> picks =
            InauguralSuperleagueSelection.Select(standings, feedersOne, rules);
        InauguralSuperleagueInvariants.ValidateSelection(picks, feedersOne, rules);

        List<SeasonMembershipEntity> membershipsOne = await LoadMembershipsAsync(context, seasonOne, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete = membershipsOne.ToDictionary(m => m.SaveAthleteId);

        SeasonEntity seasonTwo = await CreateSeasonTwoAsync(context, cancellationToken).ConfigureAwait(false);
        List<LeagueEntity> feedersTwo = await CreateFeedersTwoAsync(context, seasonTwo, cancellationToken).ConfigureAwait(false);
        LeagueEntity superleague = await CreateSuperleagueAsync(context, seasonTwo, cancellationToken).ConfigureAwait(false);

        List<SeasonMembershipEntity> membershipsTwo = BuildSeasonTwoMemberships(
            membershipsOne, membershipByAthlete, picks, seasonTwo, feedersTwo, superleague);
        foreach (SeasonMembershipEntity membership in membershipsTwo)
        {
            context.SeasonMemberships.Add(membership);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        List<MovementEntity> movements = BuildMovements(picks, membershipByAthlete, seasonOne, seasonTwo, superleague);
        foreach (MovementEntity movement in movements)
        {
            context.Movements.Add(movement);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        List<SeasonMembershipEntity> persistedTwo = await context.SeasonMemberships
            .Where(e => e.SeasonId == seasonTwo.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<MovementEntity> persistedMovements = await context.Movements
            .Where(e => e.ToSeasonId == seasonTwo.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        InauguralSuperleagueInvariants.ValidateCreated(
            seasonOne, seasonTwo, superleague, feedersTwo, membershipsOne, persistedTwo, persistedMovements, rules);

        await EmitInauguralStoriesAsync(context, seasonOne, seasonTwo, feedersOne, superleague, picks, cancellationToken).ConfigureAwait(false);

        metadata.Phase = Features.Saves.SavePhaseParser.ToText(Features.Saves.SavePhase.InauguralMovementResolved);
        await Features.Athletes.Projections.AthleteProjectionUpdater.RebuildAllAsync(context, rules, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(_store, saveId, seasonOne, seasonTwo, superleague, feedersTwo, picks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits inaugural promotion plus first-Superleague-appearance story events
    /// transactionally with the Season 1 transition. Season 1 has no Superleague
    /// so every promoted athlete is a first appearance; the prior-membership
    /// check keeps the rule explicit and idempotent via dedup keys.
    /// </summary>
    internal static async Task EmitInauguralStoriesAsync(
        SaveDbContext context,
        SeasonEntity seasonOne,
        SeasonEntity seasonTwo,
        List<LeagueEntity> feedersOne,
        LeagueEntity superleague,
        IReadOnlyList<InauguralSuperleagueSelection.InauguralPick> picks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(seasonOne);
        ArgumentNullException.ThrowIfNull(seasonTwo);
        ArgumentNullException.ThrowIfNull(feedersOne);
        ArgumentNullException.ThrowIfNull(superleague);
        ArgumentNullException.ThrowIfNull(picks);
        Dictionary<int, string> feederNames = feedersOne.ToDictionary(l => l.Id, l => l.Name);
        Dictionary<int, string> athleteNames = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        string movementDedup = Features.Stories.StoryEventEmitter.MovementDedup(seasonOne.SeasonNumber, seasonTwo.SeasonNumber);
        bool emitted = false;
        foreach (InauguralSuperleagueSelection.InauguralPick pick in picks.OrderBy(p => p.FromLeagueId).ThenBy(p => p.FromSeasonRank))
        {
            athleteNames.TryGetValue(pick.SaveAthleteId, out string? name);
            string athleteName = name ?? $"Athlete {pick.SaveAthleteId}";
            feederNames.TryGetValue(pick.FromLeagueId, out string? fromName);
            emitted |= await Features.Stories.StoryEventEmitter.TryEmitAsync(
                context,
                pick.SaveAthleteId,
                Features.Stories.StoryEventType.Promotion,
                movementDedup,
                seasonTwo.SeasonNumber,
                null,
                new Features.Stories.StoryEventPayload(
                    athleteName,
                    seasonTwo.SeasonNumber,
                    FromLeagueName: fromName ?? $"League {pick.FromLeagueId}",
                    ToLeagueName: superleague.Name,
                    FromSeasonNumber: seasonOne.SeasonNumber,
                    ToSeasonNumber: seasonTwo.SeasonNumber,
                    FromSeasonRank: pick.FromSeasonRank),
                cancellationToken).ConfigureAwait(false);
            bool hadPrior = await Features.Stories.StoryEventEmitter.HasPriorSuperleagueAppearanceAsync(
                context, pick.SaveAthleteId, seasonTwo.Id, cancellationToken).ConfigureAwait(false);
            if (!hadPrior)
            {
                emitted |= await Features.Stories.StoryEventEmitter.TryEmitAsync(
                    context,
                    pick.SaveAthleteId,
                    Features.Stories.StoryEventType.FirstSuperleagueAppearance,
                    Features.Stories.StoryEventEmitter.FirstDedup,
                    seasonTwo.SeasonNumber,
                    null,
                    new Features.Stories.StoryEventPayload(
                        athleteName,
                        seasonTwo.SeasonNumber,
                        ToLeagueName: superleague.Name,
                        FromSeasonNumber: seasonOne.SeasonNumber,
                        ToSeasonNumber: seasonTwo.SeasonNumber),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (emitted)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<SeasonEntity> LoadSeasonOneAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        SeasonEntity? season = await context.Seasons
            .SingleOrDefaultAsync(e => e.SeasonNumber == 1, cancellationToken)
            .ConfigureAwait(false);
        if (season is null)
        {
            throw new InvalidOperationException("Save has no Season 1.");
        }

        if (season.HasSuperleague)
        {
            throw new InvalidOperationException("Season 1 must have no Superleague.");
        }

        return season;
    }

    internal static Task EnsureSeasonOneCompleteAsync(SeasonEntity seasonOne)
    {
        ArgumentNullException.ThrowIfNull(seasonOne);
        if (!seasonOne.IsComplete)
        {
            throw new CreateInauguralSuperleagueConflictException(
                "Season 1 is not yet complete; the inaugural Superleague requires final feeder standings.");
        }

        return Task.CompletedTask;
    }

    internal static async Task EnsureSeasonTwoAbsentAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        bool exists = await context.Seasons.AnyAsync(e => e.SeasonNumber == 2, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            throw new CreateInauguralSuperleagueConflictException(
                "The inaugural Superleague has already been created for Season 2.");
        }

        bool hasMovements = await context.Movements
            .AnyAsync(e => e.Kind == (int)MovementKind.InauguralPromotion, cancellationToken)
            .ConfigureAwait(false);
        if (hasMovements)
        {
            throw new CreateInauguralSuperleagueConflictException(
                "The inaugural Superleague has already been created for Season 2.");
        }
    }

    internal static async Task<List<LeagueEntity>> LoadFeedersOneAsync(
        SaveDbContext context,
        SeasonEntity seasonOne,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> leagues = await context.Leagues
            .Where(e => e.SeasonId == seasonOne.Id)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (leagues.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season 1 must have exactly {rules.RegularLeagueCount} leagues, was {leagues.Count}.");
        }

        foreach (LeagueEntity league in leagues)
        {
            if (league.Kind != (int)LeagueKind.Feeder)
            {
                throw new InvalidOperationException($"Season 1 league '{league.Name}' must be a feeder league.");
            }
        }

        return leagues;
    }

    internal static async Task<List<SeasonStandingEntity>> LoadStandingsAsync(
        SaveDbContext context,
        SeasonEntity seasonOne,
        List<LeagueEntity> feedersOne,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingEntity> standings = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == seasonOne.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        int expected = rules.RegularLeagueCount * rules.LeagueSize;
        if (standings.Count != expected)
        {
            throw new InvalidOperationException(
                $"Season 1 must have exactly {expected} final standings, was {standings.Count}.");
        }

        foreach (LeagueEntity league in feedersOne)
        {
            int count = standings.Count(r => r.LeagueId == league.Id);
            if (count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must have exactly {rules.LeagueSize} final standings, was {count}.");
            }
        }

        return standings;
    }

    internal static async Task<List<SeasonMembershipEntity>> LoadMembershipsAsync(
        SaveDbContext context,
        SeasonEntity seasonOne,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => e.SeasonId == seasonOne.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Season 1 must have exactly {rules.TotalAthletesInSave} memberships, was {memberships.Count}.");
        }

        return memberships;
    }

    internal static async Task<SeasonEntity> CreateSeasonTwoAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        SeasonEntity season = new() { SeasonNumber = 2, HasSuperleague = true, IsComplete = false };
        context.Seasons.Add(season);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return season;
    }

    internal static async Task<List<LeagueEntity>> CreateFeedersTwoAsync(
        SaveDbContext context,
        SeasonEntity seasonTwo,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> leagues = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            leagues.Add(new LeagueEntity
            {
                SeasonId = seasonTwo.Id,
                SportingColor = (int)color,
                Kind = (int)LeagueKind.Feeder,
                Name = $"{color} League",
            });
        }

        foreach (LeagueEntity league in leagues)
        {
            context.Leagues.Add(league);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return leagues;
    }

    internal static async Task<LeagueEntity> CreateSuperleagueAsync(
        SaveDbContext context,
        SeasonEntity seasonTwo,
        CancellationToken cancellationToken)
    {
        // SportingColor is ignored for the Superleague (Kind distinguishes it
        // from the White feeder); membership rows carry each athlete's color
        // for future returning-color logic.
        LeagueEntity league = new()
        {
            SeasonId = seasonTwo.Id,
            SportingColor = (int)SportingColor.White,
            Kind = (int)LeagueKind.Superleague,
            Name = SuperleagueName,
        };
        context.Leagues.Add(league);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return league;
    }

    internal static List<SeasonMembershipEntity> BuildSeasonTwoMemberships(
        List<SeasonMembershipEntity> membershipsOne,
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete,
        IReadOnlyList<InauguralSuperleagueSelection.InauguralPick> picks,
        SeasonEntity seasonTwo,
        List<LeagueEntity> feedersTwo,
        LeagueEntity superleague)
    {
        HashSet<int> promoted = picks.Select(p => p.SaveAthleteId).ToHashSet();
        Dictionary<int, int> feederByColor = feedersTwo.ToDictionary(l => l.SportingColor, l => l.Id);
        List<SeasonMembershipEntity> memberships = new(membershipsOne.Count);
        foreach (SeasonMembershipEntity source in membershipsOne.OrderBy(m => m.SaveAthleteId))
        {
            int? leagueId = ResolveSeasonTwoLeague(source, promoted, feederByColor, superleague, membershipByAthlete);
            memberships.Add(new SeasonMembershipEntity
            {
                SeasonId = seasonTwo.Id,
                LeagueId = leagueId,
                SaveAthleteId = source.SaveAthleteId,
                SportingColor = source.SportingColor,
                DrawIndex = source.DrawIndex,
            });
        }

        return memberships;
    }

    internal static int? ResolveSeasonTwoLeague(
        SeasonMembershipEntity source,
        HashSet<int> promoted,
        Dictionary<int, int> feederByColor,
        LeagueEntity superleague,
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(promoted);
        ArgumentNullException.ThrowIfNull(feederByColor);
        ArgumentNullException.ThrowIfNull(superleague);
        ArgumentNullException.ThrowIfNull(membershipByAthlete);

        if (promoted.Contains(source.SaveAthleteId))
        {
            return superleague.Id;
        }

        if (source.LeagueId is null)
        {
            return null;
        }

        if (!feederByColor.TryGetValue(source.SportingColor, out int feederId))
        {
            throw new InvalidOperationException($"Unknown sporting color {source.SportingColor}.");
        }

        return feederId;
    }

    internal static List<MovementEntity> BuildMovements(
        IReadOnlyList<InauguralSuperleagueSelection.InauguralPick> picks,
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete,
        SeasonEntity seasonOne,
        SeasonEntity seasonTwo,
        LeagueEntity superleague)
    {
        List<MovementEntity> movements = new(picks.Count);
        foreach (InauguralSuperleagueSelection.InauguralPick pick in picks.OrderBy(p => p.FromLeagueId).ThenBy(p => p.FromSeasonRank))
        {
            if (!membershipByAthlete.TryGetValue(pick.SaveAthleteId, out SeasonMembershipEntity? source))
            {
                throw new InvalidOperationException($"Promoted athlete {pick.SaveAthleteId} has no Season 1 membership.");
            }

            movements.Add(new MovementEntity
            {
                SaveAthleteId = pick.SaveAthleteId,
                FromSeasonId = seasonOne.Id,
                ToSeasonId = seasonTwo.Id,
                FromLeagueId = pick.FromLeagueId,
                ToLeagueId = superleague.Id,
                Kind = (int)MovementKind.InauguralPromotion,
                FromSeasonRank = pick.FromSeasonRank,
                SportingColor = source.SportingColor,
            });
        }

        return movements;
    }

    internal static async Task<CreateInauguralSuperleagueResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity seasonOne,
        SeasonEntity seasonTwo,
        LeagueEntity superleague,
        List<LeagueEntity> feedersTwo,
        IReadOnlyList<InauguralSuperleagueSelection.InauguralPick> picks,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string?> images = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.ImageUrl, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, LeagueEntity> feedersOneById = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == seasonOne.Id)
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);

        List<InauguralSuperleagueMember> members = MapMembers(picks, names, images, feedersOneById);

        List<InauguralFeederRetention> retention = new(feedersTwo.Count);
        foreach (LeagueEntity feeder in feedersTwo.OrderBy(l => l.SportingColor))
        {
            int retained = await context.SeasonMemberships
                .CountAsync(e => e.SeasonId == seasonTwo.Id && e.LeagueId == feeder.Id, cancellationToken)
                .ConfigureAwait(false);
            retention.Add(new InauguralFeederRetention(
                feeder.Id,
                feeder.Name,
                ((SportingColor)feeder.SportingColor).ToString(),
                retained));
        }

        int pool = await context.SeasonMemberships
            .CountAsync(e => e.SeasonId == seasonTwo.Id && e.LeagueId == null, cancellationToken)
            .ConfigureAwait(false);
        int movements = await context.Movements
            .CountAsync(e => e.ToSeasonId == seasonTwo.Id, cancellationToken)
            .ConfigureAwait(false);

        return new CreateInauguralSuperleagueResponse(
            saveId,
            seasonOne.SeasonNumber,
            seasonTwo.SeasonNumber,
            superleague.Id,
            superleague.Name,
            members,
            retention,
            pool,
            movements);
    }

    internal static List<InauguralSuperleagueMember> MapMembers(
        IReadOnlyList<InauguralSuperleagueSelection.InauguralPick> picks,
        Dictionary<int, string> names,
        Dictionary<int, string?> images,
        Dictionary<int, LeagueEntity> feedersOneById)
    {
        List<InauguralSuperleagueMember> members = new(picks.Count);
        foreach (InauguralSuperleagueSelection.InauguralPick pick in picks.OrderBy(p => p.FromLeagueId).ThenBy(p => p.FromSeasonRank))
        {
            names.TryGetValue(pick.SaveAthleteId, out string? name);
            images.TryGetValue(pick.SaveAthleteId, out string? imageUrl);
            feedersOneById.TryGetValue(pick.FromLeagueId, out LeagueEntity? source);
            members.Add(new InauguralSuperleagueMember(
                pick.SaveAthleteId,
                name ?? $"Athlete {pick.SaveAthleteId}",
                ((SportingColor)(source?.SportingColor ?? 0)).ToString(),
                pick.FromLeagueId,
                source?.Name ?? $"League {pick.FromLeagueId}",
                pick.FromSeasonRank,
                imageUrl));
        }

        return members;
    }
}
