using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Saves;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Seasons.StartNextSeason;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Starts the next season only
/// from a fully valid 32-per-league roster after movement, qualifier,
/// rebalancing and the required post-season Cup (odd Color with selection,
/// individual and team; even Type with selection and team) have resolved in
/// canonical order. Season 1 uses the special inaugural chain (no qualifier);
/// Season 2+ requires automatic movement plus qualifier winners
/// (16 safe + 8 champions + 8 winners). Cups use the completed source season
/// plus current-season effective bonus values before season aging. Finalizes
/// bonus season aging so next-season effective contributions use the versioned
/// 80/60/40/20/0 weights (Stage 32 enters at 80%). Advances
/// <c>SaveMetadata.CurrentSeason</c> and returns the phase to SeasonInProgress
/// in the same transaction as refreshed projections. Consumes no sporting RNG.
/// Holds one per-save lock; read-only status queries never lock.
/// </summary>
public sealed class StartNextSeasonHandler
{
    private readonly SaveStore _store;

    public StartNextSeasonHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<StartNextSeasonResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<StartNextSeasonResponse> StartUnderLockAsync(
        Guid saveId,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        int previousCurrent = metadata.CurrentSeason;

        SeasonEntity source = await LoadSourceSeasonAsync(context, metadata, cancellationToken).ConfigureAwait(false);
        SeasonEntity next = await LoadNextSeasonAsync(context, source, cancellationToken).ConfigureAwait(false);
        await EnsureNoFurtherSuccessorAsync(context, next, cancellationToken).ConfigureAwait(false);

        bool isInaugural = source.SeasonNumber == 1 && !source.HasSuperleague;
        if (!isInaugural && !source.HasSuperleague)
        {
            throw new InvalidOperationException($"Season {source.SeasonNumber} has no Superleague and is not Season 1.");
        }

        List<LeagueEntity> sourceLeagues = await LoadSeasonLeaguesAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<LeagueEntity> nextLeagues = await LoadSeasonLeaguesAsync(context, next, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> nextMemberships = await LoadMembershipsAsync(context, next, rules, cancellationToken).ConfigureAwait(false);

        await EnsurePrerequisitesAsync(context, source, next, isInaugural, rules, cancellationToken).ConfigureAwait(false);
        StartNextSeasonInvariants.ValidateRosterCounts(next, nextLeagues, nextMemberships, rules);
        await ValidateSuperCompositionAsync(context, source, next, nextLeagues, nextMemberships, isInaugural, rules, cancellationToken).ConfigureAwait(false);
        StartNextSeasonInvariants.ValidateBonusAging(rules, source, sourceLeagues);
        await EnsureSourceBonusHistoryCompleteAsync(context, source, sourceLeagues, rules, cancellationToken).ConfigureAwait(false);
        await EnsureCupCompleteAsync(context, source, cancellationToken).ConfigureAwait(false);

        metadata.CurrentSeason = next.SeasonNumber;
        metadata.Phase = SavePhaseParser.ToText(SavePhase.SeasonInProgress);

        await Features.Athletes.Projections.AthleteProjectionUpdater.RebuildAllAsync(context, rules, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await ValidateStartedAsync(context, metadata, next, nextLeagues, rules, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(_store, saveId, source, next, previousCurrent, isInaugural, rules, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SeasonEntity> LoadSourceSeasonAsync(
        SaveDbContext context, SaveMetadataEntity metadata, CancellationToken cancellationToken)
    {
        SeasonEntity? season = await context.Seasons
            .SingleOrDefaultAsync(e => e.SeasonNumber == metadata.CurrentSeason, cancellationToken)
            .ConfigureAwait(false);
        if (season is null)
        {
            throw new InvalidOperationException($"Save has no season {metadata.CurrentSeason}.");
        }

        if (!season.IsComplete)
        {
            throw new StartNextSeasonConflictException(
                $"Season {season.SeasonNumber} is still in progress; the next season starts only after Stage 32 finalization plus movement, qualifier and rebalancing.");
        }

        return season;
    }

    internal static async Task<SeasonEntity> LoadNextSeasonAsync(
        SaveDbContext context, SeasonEntity source, CancellationToken cancellationToken)
    {
        SeasonEntity? next = await context.Seasons
            .SingleOrDefaultAsync(e => e.SeasonNumber == source.SeasonNumber + 1, cancellationToken)
            .ConfigureAwait(false);
        if (next is null)
        {
            throw new StartNextSeasonConflictException(
                $"Season {source.SeasonNumber} has no pending next season; resolve postseason movement first.");
        }

        if (!next.HasSuperleague)
        {
            throw new InvalidOperationException($"Season {next.SeasonNumber} must have a Superleague.");
        }

        if (next.IsComplete)
        {
            throw new StartNextSeasonConflictException(
                $"Season {next.SeasonNumber} is already complete; it has already been started.");
        }

        return next;
    }

    internal static async Task EnsureNoFurtherSuccessorAsync(
        SaveDbContext context, SeasonEntity next, CancellationToken cancellationToken)
    {
        bool hasFurther = await context.Seasons.AnyAsync(
            e => e.SeasonNumber == next.SeasonNumber + 1, cancellationToken).ConfigureAwait(false);
        if (hasFurther)
        {
            throw new InvalidOperationException(
                $"Season {next.SeasonNumber + 1} already exists; start seasons strictly in order.");
        }
    }

    internal static async Task<List<LeagueEntity>> LoadSeasonLeaguesAsync(
        SaveDbContext context, SeasonEntity season, CancellationToken cancellationToken)
    {
        return await context.Leagues
            .Where(e => e.SeasonId == season.Id)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<List<SeasonMembershipEntity>> LoadMembershipsAsync(
        SaveDbContext context, SeasonEntity season, RulesV1 rules, CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => e.SeasonId == season.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Season {season.SeasonNumber} must have exactly {rules.TotalAthletesInSave} memberships, was {memberships.Count}.");
        }

        return memberships;
    }

    internal static async Task EnsurePrerequisitesAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        bool isInaugural,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        if (isInaugural)
        {
            int inaugural = await context.Movements.CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.InauguralPromotion,
                cancellationToken).ConfigureAwait(false);
            if (inaugural != rules.SuperleagueSize)
            {
                throw new StartNextSeasonConflictException(
                    "The inaugural Superleague must be resolved before the next season can start.");
            }
        }
        else
        {
            int promotions = await context.Movements.CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.AutomaticPromotion,
                cancellationToken).ConfigureAwait(false);
            int relegations = await context.Movements.CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.AutomaticRelegation,
                cancellationToken).ConfigureAwait(false);
            int incumbents = await context.Movements.CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.QualifierIncumbent,
                cancellationToken).ConfigureAwait(false);
            int challengers = await context.Movements.CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.QualifierChallenger,
                cancellationToken).ConfigureAwait(false);
            if (promotions != rules.FeederAutoPromotedCount
                || relegations != rules.SuperleagueRelegatedCount
                || incumbents != rules.SuperleagueQualifierIncumbentCount
                || challengers != rules.FeederQualifierCount)
            {
                throw new StartNextSeasonConflictException(
                    "Automatic Superleague movement must be resolved before the next season can start.");
            }

            int qualifierStandings = await context.QualifierStandings.CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
            int qualifierRounds = await context.QualifierRounds.CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
            if (qualifierStandings != rules.QualifierSize || qualifierRounds != rules.QualifierRounds)
            {
                throw new StartNextSeasonConflictException(
                    "The Superleague qualifier must be resolved before the next season can start.");
            }
        }

        int rebalance = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && (e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement),
            cancellationToken).ConfigureAwait(false);
        if (rebalance == 0)
        {
            throw new StartNextSeasonConflictException(
                "Feeder rebalancing must be resolved before the next season can start.");
        }
    }

    internal static async Task EnsureCupCompleteAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        string expectedCup = CupExtensionPoint.ExpectedCupForSource(source.SeasonNumber);
        CupExtensionPoint.CupState state = await CupExtensionPoint
            .LoadCupStateAsync(context, source, expectedCup, cancellationToken)
            .ConfigureAwait(false);
        if (state.Complete)
        {
            return;
        }

        throw new StartNextSeasonConflictException(
            $"Post-season {expectedCup} for Season {source.SeasonNumber} must be complete before the next season can start " +
            $"(selection {state.SelectionResolved}, individual {state.IndividualResolved}, team {state.TeamResolved}). " +
            "Advance through the Cup Next Event boundaries so the field and results stay inspectable.");
    }

    internal static async Task ValidateSuperCompositionAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        List<LeagueEntity> nextLeagues,
        List<SeasonMembershipEntity> nextMemberships,
        bool isInaugural,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        LeagueEntity superleague = nextLeagues.SingleOrDefault(l => l.Kind == (int)LeagueKind.Superleague)
            ?? throw new InvalidOperationException($"Season {next.SeasonNumber} has no Superleague.");
        HashSet<int> superAthletes = nextMemberships
            .Where(m => m.LeagueId == superleague.Id)
            .Select(m => m.SaveAthleteId)
            .ToHashSet();

        if (isInaugural)
        {
            List<int> picks = await context.Movements
                .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.InauguralPromotion)
                .Select(e => e.SaveAthleteId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            StartNextSeasonInvariants.ValidateSuperCompositionInaugural(superAthletes, picks.ToHashSet(), rules);
            return;
        }

        LeagueEntity sourceSuperleague = await context.Leagues.SingleOrDefaultAsync(
            e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Season {source.SeasonNumber} has no Superleague.");
        List<int> safe = await context.SeasonStandings
            .Where(e => e.SeasonId == source.Id && e.LeagueId == sourceSuperleague.Id && e.SeasonRank >= 1 && e.SeasonRank <= rules.SuperleagueSafeCount)
            .Select(e => e.SaveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<int> promoted = await context.Movements
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.AutomaticPromotion)
            .Select(e => e.SaveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<int> qualified = await context.QualifierStandings
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.IsQualified)
            .Select(e => e.SaveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        StartNextSeasonInvariants.ValidateSuperCompositionNormal(
            superAthletes, safe.ToHashSet(), promoted.ToHashSet(), qualified.ToHashSet(), rules);
    }

    internal static async Task EnsureSourceBonusHistoryCompleteAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<LeagueEntity> sourceLeagues,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        foreach (LeagueEntity league in sourceLeagues)
        {
            int stages = await context.StageStandings.CountAsync(
                e => e.SeasonId == source.Id && e.LeagueId == league.Id, cancellationToken).ConfigureAwait(false);
            if (stages != rules.StagesPerSeason * rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must hold complete stage bonus history ({rules.StagesPerSeason * rules.LeagueSize} standings) to finalize aging, was {stages}.");
            }

            int seasons = await context.SeasonStandings.CountAsync(
                e => e.SeasonId == source.Id && e.LeagueId == league.Id, cancellationToken).ConfigureAwait(false);
            if (seasons != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must hold complete season standings ({rules.LeagueSize}) to finalize aging, was {seasons}.");
            }
        }
    }

    internal static async Task ValidateStartedAsync(
        SaveDbContext context,
        SaveMetadataEntity metadata,
        SeasonEntity next,
        List<LeagueEntity> nextLeagues,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        if (metadata.CurrentSeason != next.SeasonNumber)
        {
            throw new InvalidOperationException($"Save must point at season {next.SeasonNumber} after start, was {metadata.CurrentSeason}.");
        }

        if (!string.Equals(metadata.Phase, SavePhaseParser.ToText(SavePhase.SeasonInProgress), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Save must return to phase SeasonInProgress after start, was '{metadata.Phase}'.");
        }

        foreach (LeagueEntity league in nextLeagues)
        {
            int count = await context.SeasonMemberships.CountAsync(
                e => e.SeasonId == next.Id && e.LeagueId == league.Id, cancellationToken).ConfigureAwait(false);
            if (count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must contain exactly {rules.LeagueSize} athletes after start, was {count}.");
            }
        }
    }

    internal static async Task<StartNextSeasonResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        int previousCurrent,
        bool isInaugural,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await context.SaveMetadata.AsNoTracking().SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        List<LeagueEntity> leagues = await context.Leagues.AsNoTracking()
            .Where(e => e.SeasonId == next.Id)
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.SportingColor)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        LeagueEntity superleague = leagues.Single(l => l.Kind == (int)LeagueKind.Superleague);
        List<NextSeasonRosterEntry> entries = new(leagues.Count);
        foreach (LeagueEntity league in leagues)
        {
            int count = await context.SeasonMemberships.CountAsync(
                e => e.SeasonId == next.Id && e.LeagueId == league.Id, cancellationToken).ConfigureAwait(false);
            entries.Add(new NextSeasonRosterEntry(
                league.Id,
                league.Name,
                ((LeagueKind)league.Kind).ToString(),
                league.Kind == (int)LeagueKind.Superleague ? "Superleague" : ((SportingColor)league.SportingColor).ToString(),
                count));
        }

        int active = await context.SeasonMemberships.CountAsync(
            e => e.SeasonId == next.Id && e.LeagueId != null, cancellationToken).ConfigureAwait(false);
        int pool = await context.SeasonMemberships.CountAsync(
            e => e.SeasonId == next.Id && e.LeagueId == null, cancellationToken).ConfigureAwait(false);
        int movements = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        _ = metadata;

        return new StartNextSeasonResponse(
            saveId,
            source.SeasonNumber,
            next.SeasonNumber,
            previousCurrent,
            next.SeasonNumber,
            SavePhaseParser.ToText(SavePhase.SeasonInProgress),
            isInaugural,
            CupExtensionPoint.ExpectedCupForSource(source.SeasonNumber),
            superleague.Id,
            superleague.Name,
            entries,
            active,
            pool,
            movements,
            [.. rules.BonusAgeWeightsThousandths]);
    }
}
