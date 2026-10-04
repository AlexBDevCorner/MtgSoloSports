using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Athletes.GetRecordHoldings;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Serves the career records
/// held by one athlete, using the same career-record semantics as the Records
/// page. Ties share records; vacant records (maximum zero or below) report no
/// holders and are never returned here.
/// Read-path design: narrow athlete-filtered probes plus SQL GROUP BY / MAX
/// aggregates over normalized standings, memberships, movements, qualifier
/// standings and career projections. Never touches round payloads
/// (<c>Rounds.PayloadJson</c>, <c>QualifierRounds.PayloadJson</c>,
/// Cup round payloads), never invokes <c>ScoreRecordLoader</c> or
/// <c>GetRecordsHandler</c>, and never materializes whole-save history only to
/// filter one athlete. Stage-win streak maxima use only stage-win rows
/// (<c>StageRank == 1</c>): a non-win between two wins always breaks stage
/// consecutiveness, so the longest run over wins-only ordered positions equals
/// the longest run over the full active sequence. Title streak and tenure
/// maxima likewise use only champion rows and Superleague memberships.
/// All sporting math stays integer-only. Read-only: no lock, no RNG, no mutation.
/// </summary>
public sealed class GetAthleteRecordHoldingsHandler
{
    private readonly SaveStore _store;

    public GetAthleteRecordHoldingsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetAthleteRecordHoldingsResponse> HandleAsync(Guid saveId, int athleteId, CancellationToken cancellationToken = default)
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
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);

        bool exists = await context.SaveAthletes
            .AsNoTracking()
            .AnyAsync(e => e.Id == athleteId, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            throw new AthleteRecordHoldingsNotFoundException($"Athlete {athleteId} does not exist in save '{saveId:D}'.");
        }

        AthleteValues athlete = await LoadAthleteValuesAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        GlobalMaxima maxima = await LoadGlobalMaximaAsync(context, cancellationToken).ConfigureAwait(false);
        return Map(saveId, athleteId, athlete, maxima);
    }

    internal sealed record AthleteValues(
        int FeederTitles,
        int SuperleagueTitles,
        int TotalTitles,
        int StageWins,
        int RoundWins,
        int SuperleagueAppearances,
        int TotalAppearances,
        int LongestTenure,
        int Promotions,
        int Relegations,
        int EffectiveBonus,
        int TitleStreak,
        int StageWinStreak);

    internal sealed record GlobalMaxima(
        int FeederTitles,
        int SuperleagueTitles,
        int TotalTitles,
        int StageWins,
        int RoundWins,
        int SuperleagueAppearances,
        int TotalAppearances,
        int LongestTenure,
        int Promotions,
        int Relegations,
        int EffectiveBonus,
        int TitleStreak,
        int StageWinStreak);

    internal static GetAthleteRecordHoldingsResponse Map(
        Guid saveId,
        int athleteId,
        AthleteValues athlete,
        GlobalMaxima maxima)
    {
        ArgumentNullException.ThrowIfNull(athlete);
        ArgumentNullException.ThrowIfNull(maxima);
        List<RecordHoldingEntry> holdings = [];
        TryAdd(holdings, RecordKey.FeederTitles, athlete.FeederTitles, maxima.FeederTitles);
        TryAdd(holdings, RecordKey.SuperleagueTitles, athlete.SuperleagueTitles, maxima.SuperleagueTitles);
        TryAdd(holdings, RecordKey.TotalTitles, athlete.TotalTitles, maxima.TotalTitles);
        TryAdd(holdings, RecordKey.StageWins, athlete.StageWins, maxima.StageWins);
        TryAdd(holdings, RecordKey.RoundWins, athlete.RoundWins, maxima.RoundWins);
        TryAdd(holdings, RecordKey.SuperleagueAppearances, athlete.SuperleagueAppearances, maxima.SuperleagueAppearances);
        TryAdd(holdings, RecordKey.TotalAppearances, athlete.TotalAppearances, maxima.TotalAppearances);
        TryAdd(holdings, RecordKey.LongestSuperleagueTenure, athlete.LongestTenure, maxima.LongestTenure);
        TryAdd(holdings, RecordKey.Promotions, athlete.Promotions, maxima.Promotions);
        TryAdd(holdings, RecordKey.Relegations, athlete.Relegations, maxima.Relegations);
        TryAdd(holdings, RecordKey.HighestEffectiveBonus, athlete.EffectiveBonus, maxima.EffectiveBonus);
        TryAdd(holdings, RecordKey.LongestTitleStreak, athlete.TitleStreak, maxima.TitleStreak);
        TryAdd(holdings, RecordKey.LongestStageWinStreak, athlete.StageWinStreak, maxima.StageWinStreak);
        return new GetAthleteRecordHoldingsResponse(saveId, athleteId, holdings);
    }

    internal static void TryAdd(List<RecordHoldingEntry> holdings, string recordKey, int athleteValue, int globalMax)
    {
        ArgumentNullException.ThrowIfNull(holdings);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        if (globalMax <= 0)
        {
            return;
        }

        if (athleteValue != globalMax)
        {
            return;
        }

        holdings.Add(new RecordHoldingEntry(
            recordKey,
            Stories.StoryEventRenderer.FormatRecordKey(recordKey),
            globalMax,
            Stories.StoryEventRenderer.FormatRecordValue(recordKey, globalMax),
            RecordKey.IsBonus(recordKey)));
    }

    internal static async Task<AthleteValues> LoadAthleteValuesAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        CareerProbe career = await LoadCareerProbeAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        TitleCounts titles = await LoadTitleCountsForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        AppearanceCounts appearances = await LoadAppearanceCountsForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        int tenure = await LoadTenureForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        (int promotions, int relegations) = await LoadPromotionCountsForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        int titleStreak = await LoadTitleStreakForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        int stageStreak = await LoadStageStreakForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        return new AthleteValues(
            titles.Feeder,
            titles.Super,
            titles.Total,
            career.StageWins,
            career.RoundWins,
            appearances.Super,
            appearances.Total,
            tenure,
            promotions,
            relegations,
            career.EffectiveBonus,
            titleStreak,
            stageStreak);
    }

    internal static async Task<GlobalMaxima> LoadGlobalMaximaAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        CareerMaxima careers = await LoadCareerMaximaAsync(context, cancellationToken).ConfigureAwait(false);
        TitleMaxima titles = await LoadTitleMaximaAsync(context, cancellationToken).ConfigureAwait(false);
        AppearanceMaxima appearances = await LoadAppearanceMaximaAsync(context, cancellationToken).ConfigureAwait(false);
        int tenure = await LoadTenureMaximaAsync(context, cancellationToken).ConfigureAwait(false);
        (int promotions, int relegations) = await LoadPromotionMaximaAsync(context, cancellationToken).ConfigureAwait(false);
        int titleStreak = await LoadTitleStreakMaximaAsync(context, cancellationToken).ConfigureAwait(false);
        int stageStreak = await LoadStageStreakMaximaAsync(context, cancellationToken).ConfigureAwait(false);
        return new GlobalMaxima(
            titles.Feeder,
            titles.Super,
            titles.Total,
            careers.StageWins,
            careers.RoundWins,
            appearances.Super,
            appearances.Total,
            tenure,
            promotions,
            relegations,
            careers.EffectiveBonus,
            titleStreak,
            stageStreak);
    }

    internal sealed record CareerProbe(int StageWins, int RoundWins, int EffectiveBonus);

    internal sealed record CareerMaxima(int StageWins, int RoundWins, int EffectiveBonus);

    internal sealed record TitleCounts(int Feeder, int Super, int Total);

    internal sealed record TitleMaxima(int Feeder, int Super, int Total);

    internal sealed record AppearanceCounts(int Super, int Total);

    internal sealed record AppearanceMaxima(int Super, int Total);

    internal static async Task<CareerProbe> LoadCareerProbeAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        var row = await context.AthleteCareers
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .Select(e => new { e.StageWins, e.RoundWins, e.CurrentEffectiveBonusThousandths })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return new CareerProbe(0, 0, 0);
        }

        if (row.StageWins < 0 || row.RoundWins < 0)
        {
            throw new InvalidOperationException($"Athlete {athleteId} has corrupt negative sporting value.");
        }

        return new CareerProbe(row.StageWins, row.RoundWins, row.CurrentEffectiveBonusThousandths);
    }

    internal static async Task<CareerMaxima> LoadCareerMaximaAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        int? stageWins = await context.AthleteCareers
            .AsNoTracking()
            .MaxAsync(e => (int?)e.StageWins, cancellationToken)
            .ConfigureAwait(false);
        int? roundWins = await context.AthleteCareers
            .AsNoTracking()
            .MaxAsync(e => (int?)e.RoundWins, cancellationToken)
            .ConfigureAwait(false);
        int? bonus = await context.AthleteCareers
            .AsNoTracking()
            .MaxAsync(e => (int?)e.CurrentEffectiveBonusThousandths, cancellationToken)
            .ConfigureAwait(false);
        return new CareerMaxima(stageWins ?? 0, roundWins ?? 0, bonus ?? 0);
    }

    private const int SuperleagueKind = (int)LeagueKind.Superleague;

    internal static async Task<TitleCounts> LoadTitleCountsForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        int feeder = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId && e.IsChampion)
            .Join(context.Leagues.AsNoTracking(),
                standing => standing.LeagueId,
                league => league.Id,
                (standing, league) => league.Kind)
            .CountAsync(kind => kind != SuperleagueKind, cancellationToken)
            .ConfigureAwait(false);
        int super = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId && e.IsChampion)
            .Join(context.Leagues.AsNoTracking(),
                standing => standing.LeagueId,
                league => league.Id,
                (standing, league) => league.Kind)
            .CountAsync(kind => kind == SuperleagueKind, cancellationToken)
            .ConfigureAwait(false);
        int total = checked(feeder + super);
        return new TitleCounts(feeder, super, total);
    }

    internal static async Task<TitleMaxima> LoadTitleMaximaAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<TitleGroup> groups = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.IsChampion)
            .Join(context.Leagues.AsNoTracking(),
                standing => standing.LeagueId,
                league => league.Id,
                (standing, league) => new { standing.SaveAthleteId, league.Kind })
            .GroupBy(x => x.SaveAthleteId)
            .Select(g => new TitleGroup(
                g.Key,
                g.Count(x => x.Kind != SuperleagueKind),
                g.Count(x => x.Kind == SuperleagueKind),
                g.Count()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (groups.Count == 0)
        {
            return new TitleMaxima(0, 0, 0);
        }

        return new TitleMaxima(
            groups.Max(g => g.Feeder),
            groups.Max(g => g.Super),
            groups.Max(g => g.Total));
    }

    internal sealed record TitleGroup(int SaveAthleteId, int Feeder, int Super, int Total);

    internal static async Task<AppearanceCounts> LoadAppearanceCountsForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        int super = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId && e.LeagueId != null)
            .Join(context.Leagues.AsNoTracking(),
                membership => membership.LeagueId!.Value,
                league => league.Id,
                (membership, league) => league.Kind)
            .CountAsync(kind => kind == SuperleagueKind, cancellationToken)
            .ConfigureAwait(false);
        int total = await context.SeasonMemberships
            .AsNoTracking()
            .CountAsync(e => e.SaveAthleteId == athleteId && e.LeagueId != null, cancellationToken)
            .ConfigureAwait(false);
        return new AppearanceCounts(super, total);
    }

    internal static async Task<AppearanceMaxima> LoadAppearanceMaximaAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<AppearanceGroup> groups = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.LeagueId != null)
            .Join(context.Leagues.AsNoTracking(),
                membership => membership.LeagueId!.Value,
                league => league.Id,
                (membership, league) => new { membership.SaveAthleteId, league.Kind })
            .GroupBy(x => x.SaveAthleteId)
            .Select(g => new AppearanceGroup(
                g.Key,
                g.Count(x => x.Kind == SuperleagueKind),
                g.Count()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (groups.Count == 0)
        {
            return new AppearanceMaxima(0, 0);
        }

        return new AppearanceMaxima(
            groups.Max(g => g.Super),
            groups.Max(g => g.Total));
    }

    internal sealed record AppearanceGroup(int SaveAthleteId, int Super, int Total);

    internal static async Task<int> LoadTenureForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<int> seasons = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId && e.LeagueId != null)
            .Join(context.Leagues.AsNoTracking(),
                membership => membership.LeagueId!.Value,
                league => league.Id,
                (membership, league) => new { membership.SeasonId, league.Kind })
            .Where(x => x.Kind == SuperleagueKind)
            .Join(context.Seasons.AsNoTracking(),
                x => x.SeasonId,
                season => season.Id,
                (x, season) => season.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return LongestRun(seasons);
    }

    internal static async Task<int> LoadTenureMaximaAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<TenureRow> rows = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.LeagueId != null)
            .Join(context.Leagues.AsNoTracking(),
                membership => membership.LeagueId!.Value,
                league => league.Id,
                (membership, league) => new { membership.SaveAthleteId, membership.SeasonId, league.Kind })
            .Where(x => x.Kind == SuperleagueKind)
            .Join(context.Seasons.AsNoTracking(),
                x => x.SeasonId,
                season => season.Id,
                (x, season) => new TenureRow(x.SaveAthleteId, season.SeasonNumber))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return 0;
        }

        return rows
            .GroupBy(r => r.SaveAthleteId)
            .Max(g => LongestRun(g.Select(r => r.SeasonNumber).ToList()));
    }

    internal sealed record TenureRow(int SaveAthleteId, int SeasonNumber);

    internal static int LongestRun(List<int> seasonNumbers)
    {
        ArgumentNullException.ThrowIfNull(seasonNumbers);
        if (seasonNumbers.Count == 0)
        {
            return 0;
        }

        seasonNumbers.Sort();
        int best = 0;
        int run = 0;
        int previous = int.MinValue;
        foreach (int season in seasonNumbers)
        {
            run = season == previous + 1 ? run + 1 : 1;
            best = Math.Max(best, run);
            previous = season;
        }

        return best;
    }

    internal static async Task<(int Promotions, int Relegations)> LoadPromotionCountsForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        int movementPromos = await context.Movements
            .AsNoTracking()
            .CountAsync(e => e.SaveAthleteId == athleteId &&
                (e.Kind == (int)MovementKind.InauguralPromotion || e.Kind == (int)MovementKind.AutomaticPromotion),
                cancellationToken)
            .ConfigureAwait(false);
        int movementReleg = await context.Movements
            .AsNoTracking()
            .CountAsync(e => e.SaveAthleteId == athleteId && e.Kind == (int)MovementKind.AutomaticRelegation,
                cancellationToken)
            .ConfigureAwait(false);
        int qualifierPromos = await context.QualifierStandings
            .AsNoTracking()
            .CountAsync(e => e.SaveAthleteId == athleteId && e.Role == (int)QualifierRole.Challenger && e.IsQualified,
                cancellationToken)
            .ConfigureAwait(false);
        int qualifierReleg = await context.QualifierStandings
            .AsNoTracking()
            .CountAsync(e => e.SaveAthleteId == athleteId && e.Role == (int)QualifierRole.Incumbent && !e.IsQualified,
                cancellationToken)
            .ConfigureAwait(false);
        return (checked(movementPromos + qualifierPromos), checked(movementReleg + qualifierReleg));
    }

    internal static async Task<(int Promotions, int Relegations)> LoadPromotionMaximaAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<MovementProbe> movements = await context.Movements
            .AsNoTracking()
            .Select(e => new MovementProbe(e.SaveAthleteId, e.Kind))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<QualifierProbe> qualifiers = await context.QualifierStandings
            .AsNoTracking()
            .Select(e => new QualifierProbe(e.SaveAthleteId, e.Role, e.IsQualified))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (movements.Count == 0 && qualifiers.Count == 0)
        {
            return (0, 0);
        }

        Dictionary<int, int> promos = [];
        Dictionary<int, int> releg = [];
        foreach (MovementProbe movement in movements)
        {
            promos.TryAdd(movement.SaveAthleteId, 0);
            releg.TryAdd(movement.SaveAthleteId, 0);
            if (movement.Kind is (int)MovementKind.InauguralPromotion or (int)MovementKind.AutomaticPromotion)
            {
                promos[movement.SaveAthleteId] = checked(promos[movement.SaveAthleteId] + 1);
            }
            else if (movement.Kind == (int)MovementKind.AutomaticRelegation)
            {
                releg[movement.SaveAthleteId] = checked(releg[movement.SaveAthleteId] + 1);
            }
        }

        foreach (QualifierProbe qualifier in qualifiers)
        {
            promos.TryAdd(qualifier.SaveAthleteId, 0);
            releg.TryAdd(qualifier.SaveAthleteId, 0);
            if (qualifier.Role == (int)QualifierRole.Challenger && qualifier.IsQualified)
            {
                promos[qualifier.SaveAthleteId] = checked(promos[qualifier.SaveAthleteId] + 1);
            }
            else if (qualifier.Role == (int)QualifierRole.Incumbent && !qualifier.IsQualified)
            {
                releg[qualifier.SaveAthleteId] = checked(releg[qualifier.SaveAthleteId] + 1);
            }
        }

        return (promos.Count == 0 ? 0 : promos.Values.Max(), releg.Count == 0 ? 0 : releg.Values.Max());
    }

    internal sealed record MovementProbe(int SaveAthleteId, int Kind);

    internal sealed record QualifierProbe(int SaveAthleteId, int Role, bool IsQualified);

    internal static async Task<int> LoadTitleStreakForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<int> seasons = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId && e.IsChampion)
            .Join(context.Seasons.AsNoTracking(),
                standing => standing.SeasonId,
                season => season.Id,
                (standing, season) => season.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return LongestRun(seasons);
    }

    internal static async Task<int> LoadTitleStreakMaximaAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<TitleSeasonRow> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.IsChampion)
            .Join(context.Seasons.AsNoTracking(),
                standing => standing.SeasonId,
                season => season.Id,
                (standing, season) => new TitleSeasonRow(standing.SaveAthleteId, season.SeasonNumber))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return 0;
        }

        return rows
            .GroupBy(r => r.SaveAthleteId)
            .Max(g => LongestRun(g.Select(r => r.SeasonNumber).ToList()));
    }

    internal sealed record TitleSeasonRow(int SaveAthleteId, int SeasonNumber);

    internal static async Task<int> LoadStageStreakForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<StageWinPosition> wins = await LoadStageWinsForAthleteAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        return MeasureWinsOnlyStreak(wins);
    }

    internal static async Task<int> LoadStageStreakMaximaAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<StageWinRow> rows = await context.StageStandings
            .AsNoTracking()
            .Where(e => e.StageRank == 1)
            .Join(context.Seasons.AsNoTracking(),
                standing => standing.SeasonId,
                season => season.Id,
                (standing, season) => new StageWinRow(standing.SaveAthleteId, season.SeasonNumber, standing.StageNumber))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return 0;
        }

        foreach (StageWinRow row in rows)
        {
            if (row.StageNumber < 1 || row.StageNumber > 32)
            {
                throw new InvalidOperationException($"Stage standing for athlete {row.SaveAthleteId} has corrupt stage {row.StageNumber}.");
            }
        }

        return rows
            .GroupBy(r => r.SaveAthleteId)
            .Max(g => MeasureWinsOnlyStreak(g.Select(r => new StageWinPosition(r.SeasonNumber, r.StageNumber)).ToList()));
    }

    internal sealed record StageWinRow(int SaveAthleteId, int SeasonNumber, int StageNumber);

    internal sealed record StageWinPosition(int SeasonNumber, int StageNumber);

    internal static async Task<List<StageWinPosition>> LoadStageWinsForAthleteAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        List<StageWinPosition> wins = await context.StageStandings
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId && e.StageRank == 1)
            .Join(context.Seasons.AsNoTracking(),
                standing => standing.SeasonId,
                season => season.Id,
                (standing, season) => new StageWinPosition(season.SeasonNumber, standing.StageNumber))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (StageWinPosition win in wins)
        {
            if (win.StageNumber < 1 || win.StageNumber > 32)
            {
                throw new InvalidOperationException($"Stage standing for athlete {athleteId} has corrupt stage {win.StageNumber}.");
            }
        }

        return wins;
    }

    internal static int MeasureWinsOnlyStreak(List<StageWinPosition> wins)
    {
        ArgumentNullException.ThrowIfNull(wins);
        if (wins.Count == 0)
        {
            return 0;
        }

        List<StageWinPosition> ordered = wins
            .OrderBy(w => w.SeasonNumber)
            .ThenBy(w => w.StageNumber)
            .ToList();
        int best = 0;
        int run = 0;
        StageWinPosition? previous = null;
        foreach (StageWinPosition current in ordered)
        {
            run = previous is not null && IsConsecutiveStage(previous, current) ? run + 1 : 1;
            best = Math.Max(best, run);
            previous = current;
        }

        return best;
    }

    internal static bool IsConsecutiveStage(StageWinPosition previous, StageWinPosition current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (current.SeasonNumber == previous.SeasonNumber)
        {
            return current.StageNumber == previous.StageNumber + 1;
        }

        return current.SeasonNumber == previous.SeasonNumber + 1 && previous.StageNumber == 32 && current.StageNumber == 1;
    }
}
