using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Athletes.GetProfile;
using MtgSoloSports.Features.Athletes.GetRecordHoldings;
using MtgSoloSports.Features.Reports.AthleteReport;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// MSS-093: bulk athlete-report projection for Drive publishing.
/// Reuses the exact same single-athlete report semantics as the individual
/// download (<see cref="GetAthleteReportHandler"/>): persisted profile plus
/// record holdings rendered by <see cref="AthleteReportMarkdown"/>. The global
/// record maxima are loaded once per sync page and reused for every athlete
/// instead of calling the single-athlete handler 2048 times (which would
/// repeat seven whole-save aggregates per athlete). Files are still generated
/// one at a time so large universes never buffer the whole corpus; only small
/// bounded dictionaries (maxima, careers for the index) are held.
/// </summary>
internal sealed class DriveAthleteReports
{
    internal sealed record SharedAthleteData(
        SaveMetadataEntity Metadata,
        SimulationKernel.Rules.RulesV1 Rules,
        int SeasonCount,
        GetAthleteRecordHoldingsHandler.GlobalMaxima Maxima);

    internal static async Task<SharedAthleteData> LoadSharedAsync(
        SaveStore store,
        Guid saveId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        await store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        int seasonCount = await context.Seasons.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
        GetAthleteRecordHoldingsHandler.GlobalMaxima maxima =
            await GetAthleteRecordHoldingsHandler.LoadGlobalMaximaAsync(context, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SharedAthleteData(metadata, rules, seasonCount, maxima);
    }

    internal static async Task<string> GenerateAthleteAsync(
        SaveStore store,
        Guid saveId,
        int athleteId,
        SharedAthleteData shared,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(shared);
        using SaveDbContext context = store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        GetAthleteProfileResponse profile = await GetAthleteReportHandler.LoadProfileAsync(context, saveId, athleteId, cancellationToken).ConfigureAwait(false);
        GetAthleteRecordHoldingsHandler.AthleteValues values =
            await GetAthleteRecordHoldingsHandler.LoadAthleteValuesAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        GetAthleteRecordHoldingsResponse holdings =
            GetAthleteRecordHoldingsHandler.Map(saveId, athleteId, values, shared.Maxima);
        return AthleteReportMarkdown.Build(saveId, shared.Metadata, shared.Rules, shared.SeasonCount, profile, holdings);
    }

    internal sealed record AthleteIndexRow(
        int AthleteId,
        string Name,
        string ColorName,
        string LeagueOrPool,
        string ReportPath);

    internal static async Task<string> GenerateIndexAsync(
        SaveStore store,
        Guid saveId,
        SharedAthleteData shared,
        IReadOnlyList<DriveLibraryInventory.AthleteInventoryRow> athletes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(athletes);
        Dictionary<int, (string LeagueOrPool, string Name, int Color)> leagueById =
            await LoadLeagueMapAsync(store, saveId, cancellationToken).ConfigureAwait(false);
        List<AthleteIndexRow> rows = new(athletes.Count);
        foreach (DriveLibraryInventory.AthleteInventoryRow row in athletes.OrderBy(e => e.AthleteId))
        {
            string colorName = DriveLibraryInventory.AthleteColorName(row.SportingColor);
            string leagueOrPool = leagueById.TryGetValue(row.AthleteId, out var entry) ? entry.LeagueOrPool : "Common pool";
            string fileName = ReportFilenames.AthleteFileName(row.AthleteId, row.Name, "md");
            rows.Add(new AthleteIndexRow(row.AthleteId, row.Name, colorName, leagueOrPool, $"{colorName}/{fileName}"));
        }

        return BuildIndexMarkdown(saveId, shared, rows);
    }

    private static async Task<Dictionary<int, (string LeagueOrPool, string Name, int Color)>> LoadLeagueMapAsync(
        SaveStore store,
        Guid saveId,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<AthleteCareerEntity> careers = await context.AthleteCareers
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, (string LeagueOrPool, string Name, int Color)> map = new(careers.Count);
        foreach (AthleteCareerEntity career in careers)
        {
            string leagueOrPool = career.IsActive && !string.IsNullOrWhiteSpace(career.CurrentLeagueName)
                ? career.CurrentLeagueName!
                : "Common pool";
            map[career.SaveAthleteId] = (leagueOrPool, string.Empty, 0);
        }

        return map;
    }

    internal static string BuildIndexMarkdown(
        Guid saveId,
        SharedAthleteData shared,
        IReadOnlyList<AthleteIndexRow> rows)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(rows);
        StringBuilder sb = new();
        sb.AppendLine("# Athletes index — one report per athlete");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Save: {shared.Metadata.Name} (`{saveId:D}`) · Season {shared.Metadata.CurrentSeason} · Phase {shared.Metadata.Phase}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Scope: {rows.Count} persisted athletes (active + common pool) · Snapshot: {shared.SeasonCount} season(s) · Report: {DriveLibraryKind.AthletesIndex} v{ReportContract.SchemaVersion} · Rules v{shared.Rules.Version} · Schema v{shared.Metadata.SchemaVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)} (UTC; metadata only)");
        sb.AppendLine();
        sb.AppendLine("- Each athlete keeps one stable file under `Athletes/<Color>/athlete-<id>-<slug>.md` keyed by stable save-scoped id; the name slug is decoration only and renames update in place.");
        sb.AppendLine("- This Drive folder is not a ChatGPT Project: a folder with thousands of reports does not auto-upload into ChatGPT. Use this index to retrieve explicit files in ChatGPT via its Drive picker; Drive updates never replace separately uploaded Project copies.");
        sb.AppendLine("- Coverage: active athletes show their current league; pool athletes show `Common pool`. Unchanged pool reports are skipped on incremental syncs; run an occasional full reconciliation to catch missed record-holder changes.");
        sb.AppendLine();
        int active = rows.Count(r => !string.Equals(r.LeagueOrPool, "Common pool", StringComparison.Ordinal));
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Coverage — {rows.Count} athletes ({active} active, {rows.Count - active} pool)");
        sb.AppendLine();
        sb.AppendLine("| Athlete id | Name | Color | League / pool | Report path |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (AthleteIndexRow row in rows)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.AthleteId} | {ReportMarkdown.Cell(row.Name)} | {ReportMarkdown.Cell(row.ColorName)} | {ReportMarkdown.Cell(row.LeagueOrPool)} | {ReportMarkdown.Cell(row.ReportPath)} |");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// MSS-093: changed athlete ids for opt-in auto publishing after a
    /// committed global stage or relevant Cup/selection/postseason/new-season
    /// event. Covers participants (career last season/stage in range),
    /// honours/selections/movements/cup histories in the changed season range,
    /// plus current and previous record holders so pool athletes losing a
    /// global record are not missed. Pure read-only queries; no markdown.
    /// </summary>
    internal static async Task<HashSet<int>> LoadChangedAthleteIdsAsync(
        SaveDbContext context,
        int cutoffSeason,
        GetAthleteRecordHoldingsHandler.GlobalMaxima newMaxima,
        IReadOnlyDictionary<string, int>? oldMaxima,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        HashSet<int> ids = new();
        foreach (int id in await LoadCareerParticipantsAsync(context, cutoffSeason, cancellationToken).ConfigureAwait(false))
        {
            ids.Add(id);
        }

        foreach (int id in await LoadHonourIdsAsync(context, cutoffSeason, cancellationToken).ConfigureAwait(false))
        {
            ids.Add(id);
        }

        foreach (int id in await LoadMovementIdsAsync(context, cutoffSeason, cancellationToken).ConfigureAwait(false))
        {
            ids.Add(id);
        }

        foreach (int id in await LoadSelectionIdsAsync(context, cutoffSeason, cancellationToken).ConfigureAwait(false))
        {
            ids.Add(id);
        }

        foreach (int id in await LoadCupHistoryIdsAsync(context, cutoffSeason, cancellationToken).ConfigureAwait(false))
        {
            ids.Add(id);
        }

        foreach (int id in await LoadChaosIdsAsync(context, cutoffSeason, cancellationToken).ConfigureAwait(false))
        {
            ids.Add(id);
        }

        foreach (int id in await LoadRecordHolderIdsAsync(context, newMaxima, cancellationToken).ConfigureAwait(false))
        {
            ids.Add(id);
        }

        if (oldMaxima is not null)
        {
            foreach (int id in await LoadPreviousHolderIdsAsync(context, newMaxima, oldMaxima, cancellationToken).ConfigureAwait(false))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static async Task<List<int>> LoadCareerParticipantsAsync(SaveDbContext context, int cutoff, CancellationToken ct) =>
        await context.AthleteCareers.AsNoTracking().Where(e => e.LastSeasonNumber >= cutoff).Select(e => e.SaveAthleteId).Distinct().ToListAsync(ct).ConfigureAwait(false);

    private static async Task<List<int>> LoadHonourIdsAsync(SaveDbContext context, int cutoff, CancellationToken ct) =>
        await context.Honours.AsNoTracking().Where(e => e.SeasonNumber >= cutoff).Select(e => e.SaveAthleteId).Distinct().ToListAsync(ct).ConfigureAwait(false);

    private static async Task<List<int>> LoadMovementIdsAsync(SaveDbContext context, int cutoff, CancellationToken ct) =>
        await context.Movements.AsNoTracking().Where(e => e.ToSeasonId >= 1).Join(
            context.Seasons.AsNoTracking(),
            m => m.ToSeasonId,
            s => s.Id,
            (m, s) => new { m.SaveAthleteId, s.SeasonNumber }).Where(x => x.SeasonNumber >= cutoff).Select(x => x.SaveAthleteId).Distinct().ToListAsync(ct).ConfigureAwait(false);

    private static async Task<List<int>> LoadSelectionIdsAsync(SaveDbContext context, int cutoff, CancellationToken ct)
    {
        List<int> color = await context.ColorCupSelections.AsNoTracking().Where(e => e.SourceSeasonNumber >= cutoff).Select(e => e.SaveAthleteId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        List<int> type = await context.TypeCupSelections.AsNoTracking().Where(e => e.SourceSeasonNumber >= cutoff).Select(e => e.SaveAthleteId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        color.AddRange(type);
        return color.Distinct().ToList();
    }

    private static async Task<List<int>> LoadCupHistoryIdsAsync(SaveDbContext context, int cutoff, CancellationToken ct)
    {
        List<int> ids = [];
        ids.AddRange(await context.ColorCupIndividualStandings.AsNoTracking().Where(e => e.SourceSeasonNumber >= cutoff).Select(e => e.SaveAthleteId).Distinct().ToListAsync(ct).ConfigureAwait(false));
        ids.AddRange(await context.ColorCupTeamGroupStandings.AsNoTracking().Where(e => e.SourceSeasonNumber >= cutoff).Select(e => e.SaveAthleteId).Distinct().ToListAsync(ct).ConfigureAwait(false));
        ids.AddRange(await context.TypeCupTeamGroupStandings.AsNoTracking().Where(e => e.SourceSeasonNumber >= cutoff).Select(e => e.SaveAthleteId).Distinct().ToListAsync(ct).ConfigureAwait(false));
        return ids.Distinct().ToList();
    }

    private static async Task<List<int>> LoadChaosIdsAsync(SaveDbContext context, int cutoff, CancellationToken ct)
    {
        List<int> ids = [];
        ids.AddRange(await context.ChaosCupEntrants.AsNoTracking().Where(e => e.SourceSeasonNumber >= cutoff).Select(e => e.SaveAthleteId).Distinct().ToListAsync(ct).ConfigureAwait(false));
        ids.AddRange(await context.ChaosCupDraws.AsNoTracking().Where(e => e.SourceSeasonNumber >= cutoff).Select(e => e.SaveAthleteId).Distinct().ToListAsync(ct).ConfigureAwait(false));
        ids.AddRange(await context.ChaosCupGroupStandings.AsNoTracking().Where(e => e.SourceSeasonNumber >= cutoff).Select(e => e.SaveAthleteId).Distinct().ToListAsync(ct).ConfigureAwait(false));
        return ids.Distinct().ToList();
    }

    internal static async Task<HashSet<int>> LoadRecordHolderIdsAsync(
        SaveDbContext context,
        GetAthleteRecordHoldingsHandler.GlobalMaxima maxima,
        CancellationToken ct)
    {
        HashSet<int> holders = [];
        await AddCareerHoldersAsync(context, maxima, holders, ct).ConfigureAwait(false);
        await AddAggregateHoldersAsync(context, maxima, holders, ct).ConfigureAwait(false);
        return holders;
    }

    private static async Task AddCareerHoldersAsync(
        SaveDbContext context,
        GetAthleteRecordHoldingsHandler.GlobalMaxima maxima,
        HashSet<int> holders,
        CancellationToken ct)
    {
        if (maxima.StageWins > 0)
        {
            foreach (int id in await context.AthleteCareers.AsNoTracking().Where(e => e.StageWins == maxima.StageWins).Select(e => e.SaveAthleteId).ToListAsync(ct).ConfigureAwait(false))
            {
                holders.Add(id);
            }
        }

        if (maxima.RoundWins > 0)
        {
            foreach (int id in await context.AthleteCareers.AsNoTracking().Where(e => e.RoundWins == maxima.RoundWins).Select(e => e.SaveAthleteId).ToListAsync(ct).ConfigureAwait(false))
            {
                holders.Add(id);
            }
        }

        if (maxima.EffectiveBonus > 0)
        {
            foreach (int id in await context.AthleteCareers.AsNoTracking().Where(e => e.CurrentEffectiveBonusThousandths == maxima.EffectiveBonus).Select(e => e.SaveAthleteId).ToListAsync(ct).ConfigureAwait(false))
            {
                holders.Add(id);
            }
        }
    }

    private static async Task AddAggregateHoldersAsync(
        SaveDbContext context,
        GetAthleteRecordHoldingsHandler.GlobalMaxima maxima,
        HashSet<int> holders,
        CancellationToken ct)
    {
        if (maxima.FeederTitles > 0 || maxima.SuperleagueTitles > 0 || maxima.TotalTitles > 0)
        {
            foreach (int id in await LoadTitleHolderIdsAsync(context, maxima, ct).ConfigureAwait(false))
            {
                holders.Add(id);
            }
        }

        if (maxima.SuperleagueAppearances > 0 || maxima.TotalAppearances > 0)
        {
            foreach (int id in await LoadAppearanceHolderIdsAsync(context, maxima, ct).ConfigureAwait(false))
            {
                holders.Add(id);
            }
        }

        if (maxima.LongestTenure > 0)
        {
            foreach (int id in await LoadTenureHolderIdsAsync(context, maxima.LongestTenure, ct).ConfigureAwait(false))
            {
                holders.Add(id);
            }
        }

        await AddStreakHoldersAsync(context, maxima, holders, ct).ConfigureAwait(false);
    }

    private static async Task AddStreakHoldersAsync(
        SaveDbContext context,
        GetAthleteRecordHoldingsHandler.GlobalMaxima maxima,
        HashSet<int> holders,
        CancellationToken ct)
    {
        if (maxima.Promotions > 0 || maxima.Relegations > 0)
        {
            foreach (int id in await LoadPromotionHolderIdsAsync(context, maxima, ct).ConfigureAwait(false))
            {
                holders.Add(id);
            }
        }

        if (maxima.TitleStreak > 0)
        {
            foreach (int id in await LoadTitleStreakHolderIdsAsync(context, maxima.TitleStreak, ct).ConfigureAwait(false))
            {
                holders.Add(id);
            }
        }

        if (maxima.StageWinStreak > 0)
        {
            foreach (int id in await LoadStageStreakHolderIdsAsync(context, maxima.StageWinStreak, ct).ConfigureAwait(false))
            {
                holders.Add(id);
            }
        }
    }

    private static async Task<List<int>> LoadTitleHolderIdsAsync(SaveDbContext context, GetAthleteRecordHoldingsHandler.GlobalMaxima maxima, CancellationToken ct)
    {
        // Same kind mapping as GetAthleteRecordHoldingsHandler: Superleague
        // kind is LeagueKind.Superleague (1); feeder is anything else.
        const int superleagueKind = (int)LeagueKind.Superleague;
        List<GetAthleteRecordHoldingsHandler.TitleGroup> groups = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.IsChampion)
            .Join(context.Leagues.AsNoTracking(),
                standing => standing.LeagueId,
                league => league.Id,
                (standing, league) => new { standing.SaveAthleteId, league.Kind })
            .GroupBy(x => x.SaveAthleteId)
            .Select(g => new GetAthleteRecordHoldingsHandler.TitleGroup(
                g.Key,
                g.Count(x => x.Kind != superleagueKind),
                g.Count(x => x.Kind == superleagueKind),
                g.Count()))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return groups.Where(g =>
            (maxima.FeederTitles > 0 && g.Feeder == maxima.FeederTitles) ||
            (maxima.SuperleagueTitles > 0 && g.Super == maxima.SuperleagueTitles) ||
            (maxima.TotalTitles > 0 && g.Total == maxima.TotalTitles)).Select(g => g.SaveAthleteId).ToList();
    }

    private static async Task<List<int>> LoadAppearanceHolderIdsAsync(SaveDbContext context, GetAthleteRecordHoldingsHandler.GlobalMaxima maxima, CancellationToken ct)
    {
        const int superleagueKind = (int)LeagueKind.Superleague;
        List<GetAthleteRecordHoldingsHandler.AppearanceGroup> groups = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.LeagueId != null)
            .Join(context.Leagues.AsNoTracking(),
                membership => membership.LeagueId!.Value,
                league => league.Id,
                (membership, league) => new { membership.SaveAthleteId, league.Kind })
            .GroupBy(x => x.SaveAthleteId)
            .Select(g => new GetAthleteRecordHoldingsHandler.AppearanceGroup(
                g.Key,
                g.Count(x => x.Kind == superleagueKind),
                g.Count()))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return groups.Where(g =>
            (maxima.SuperleagueAppearances > 0 && g.Super == maxima.SuperleagueAppearances) ||
            (maxima.TotalAppearances > 0 && g.Total == maxima.TotalAppearances)).Select(g => g.SaveAthleteId).ToList();
    }

    private static async Task<List<int>> LoadTenureHolderIdsAsync(SaveDbContext context, int tenureMax, CancellationToken ct)
    {
        const int superleagueKind = (int)LeagueKind.Superleague;
        List<GetAthleteRecordHoldingsHandler.TenureRow> rows = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.LeagueId != null)
            .Join(context.Leagues.AsNoTracking(),
                membership => membership.LeagueId!.Value,
                league => league.Id,
                (membership, league) => new { membership.SaveAthleteId, membership.SeasonId, league.Kind })
            .Where(x => x.Kind == superleagueKind)
            .Join(context.Seasons.AsNoTracking(),
                x => x.SeasonId,
                season => season.Id,
                (x, season) => new GetAthleteRecordHoldingsHandler.TenureRow(x.SaveAthleteId, season.SeasonNumber))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.GroupBy(r => r.SaveAthleteId)
            .Where(g => GetAthleteRecordHoldingsHandler.LongestRun(g.Select(r => r.SeasonNumber).ToList()) == tenureMax)
            .Select(g => g.Key).ToList();
    }

    private static async Task<List<int>> LoadPromotionHolderIdsAsync(SaveDbContext context, GetAthleteRecordHoldingsHandler.GlobalMaxima maxima, CancellationToken ct)
    {
        // Bounded: reuse movement + qualifier probes (same as maxima path).
        List<GetAthleteRecordHoldingsHandler.MovementProbe> movements = await context.Movements
            .AsNoTracking()
            .Select(e => new GetAthleteRecordHoldingsHandler.MovementProbe(e.SaveAthleteId, e.Kind))
            .ToListAsync(ct).ConfigureAwait(false);
        List<GetAthleteRecordHoldingsHandler.QualifierProbe> qualifiers = await context.QualifierStandings
            .AsNoTracking()
            .Select(e => new GetAthleteRecordHoldingsHandler.QualifierProbe(e.SaveAthleteId, e.Role, e.IsQualified))
            .ToListAsync(ct).ConfigureAwait(false);
        Dictionary<int, int> promos = [];
        Dictionary<int, int> releg = [];
        foreach (var m in movements)
        {
            promos.TryAdd(m.SaveAthleteId, 0);
            releg.TryAdd(m.SaveAthleteId, 0);
            if (m.Kind is (int)MovementKind.InauguralPromotion or (int)MovementKind.AutomaticPromotion)
            {
                promos[m.SaveAthleteId]++;
            }
            else if (m.Kind == (int)MovementKind.AutomaticRelegation)
            {
                releg[m.SaveAthleteId]++;
            }
        }

        foreach (var q in qualifiers)
        {
            promos.TryAdd(q.SaveAthleteId, 0);
            releg.TryAdd(q.SaveAthleteId, 0);
            if (q.Role == (int)QualifierRole.Challenger && q.IsQualified)
            {
                promos[q.SaveAthleteId]++;
            }
            else if (q.Role == (int)QualifierRole.Incumbent && !q.IsQualified)
            {
                releg[q.SaveAthleteId]++;
            }
        }

        List<int> holders = [];
        foreach (var kv in promos)
        {
            if (maxima.Promotions > 0 && kv.Value == maxima.Promotions)
            {
                holders.Add(kv.Key);
            }
        }

        foreach (var kv in releg)
        {
            if (maxima.Relegations > 0 && kv.Value == maxima.Relegations)
            {
                holders.Add(kv.Key);
            }
        }

        return holders.Distinct().ToList();
    }

    private static async Task<List<int>> LoadTitleStreakHolderIdsAsync(SaveDbContext context, int streakMax, CancellationToken ct)
    {
        List<GetAthleteRecordHoldingsHandler.TitleSeasonRow> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.IsChampion)
            .Join(context.Seasons.AsNoTracking(),
                standing => standing.SeasonId,
                season => season.Id,
                (standing, season) => new GetAthleteRecordHoldingsHandler.TitleSeasonRow(standing.SaveAthleteId, season.SeasonNumber))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.GroupBy(r => r.SaveAthleteId)
            .Where(g => GetAthleteRecordHoldingsHandler.LongestRun(g.Select(r => r.SeasonNumber).ToList()) == streakMax)
            .Select(g => g.Key).ToList();
    }

    private static async Task<List<int>> LoadStageStreakHolderIdsAsync(SaveDbContext context, int streakMax, CancellationToken ct)
    {
        List<GetAthleteRecordHoldingsHandler.StageWinRow> rows = await context.StageStandings
            .AsNoTracking()
            .Where(e => e.StageRank == 1)
            .Join(context.Seasons.AsNoTracking(),
                standing => standing.SeasonId,
                season => season.Id,
                (standing, season) => new GetAthleteRecordHoldingsHandler.StageWinRow(standing.SaveAthleteId, season.SeasonNumber, standing.StageNumber))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.GroupBy(r => r.SaveAthleteId)
            .Where(g => GetAthleteRecordHoldingsHandler.MeasureWinsOnlyStreak(g.Select(r => new GetAthleteRecordHoldingsHandler.StageWinPosition(r.SeasonNumber, r.StageNumber)).ToList()) == streakMax)
            .Select(g => g.Key).ToList();
    }

    private static async Task<List<int>> LoadPreviousHolderIdsAsync(
        SaveDbContext context,
        GetAthleteRecordHoldingsHandler.GlobalMaxima @new,
        IReadOnlyDictionary<string, int> old,
        CancellationToken ct)
    {
        // When a global maxima increased, previous holders (value == old max)
        // lose their holding and need a re-upload. Query only changed keys.
        static int OldValue(IReadOnlyDictionary<string, int> map, string key) => map.TryGetValue(key, out int v) ? v : 0;
        List<int> previous = [];
        int oldStage = OldValue(old, "stage_wins");
        int oldRound = OldValue(old, "round_wins");
        int oldBonus = OldValue(old, "highest_effective_bonus");
        int oldFeeder = OldValue(old, "feeder_titles");
        int oldSuper = OldValue(old, "superleague_titles");
        int oldTotal = OldValue(old, "total_titles");
        if (@new.StageWins != oldStage && oldStage > 0)
        {
            previous.AddRange(await context.AthleteCareers.AsNoTracking().Where(e => e.StageWins == oldStage).Select(e => e.SaveAthleteId).ToListAsync(ct).ConfigureAwait(false));
        }

        if (@new.RoundWins != oldRound && oldRound > 0)
        {
            previous.AddRange(await context.AthleteCareers.AsNoTracking().Where(e => e.RoundWins == oldRound).Select(e => e.SaveAthleteId).ToListAsync(ct).ConfigureAwait(false));
        }

        if (@new.EffectiveBonus != oldBonus && oldBonus > 0)
        {
            previous.AddRange(await context.AthleteCareers.AsNoTracking().Where(e => e.CurrentEffectiveBonusThousandths == oldBonus).Select(e => e.SaveAthleteId).ToListAsync(ct).ConfigureAwait(false));
        }

        // Other keys use the same holder loaders with old maxima values.
        if ((@new.FeederTitles != oldFeeder || @new.SuperleagueTitles != oldSuper || @new.TotalTitles != oldTotal)
            && (oldFeeder > 0 || oldSuper > 0 || oldTotal > 0))
        {
            var oldMax = new GetAthleteRecordHoldingsHandler.GlobalMaxima(
                oldFeeder, oldSuper, oldTotal,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            previous.AddRange(await LoadTitleHolderIdsAsync(context, oldMax, ct).ConfigureAwait(false));
        }

        return previous.Distinct().ToList();
    }

    internal static IReadOnlyDictionary<string, int> ToMaximaMap(GetAthleteRecordHoldingsHandler.GlobalMaxima maxima) =>
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["feeder_titles"] = maxima.FeederTitles,
            ["superleague_titles"] = maxima.SuperleagueTitles,
            ["total_titles"] = maxima.TotalTitles,
            ["stage_wins"] = maxima.StageWins,
            ["round_wins"] = maxima.RoundWins,
            ["superleague_appearances"] = maxima.SuperleagueAppearances,
            ["total_appearances"] = maxima.TotalAppearances,
            ["longest_superleague_tenure"] = maxima.LongestTenure,
            ["promotions"] = maxima.Promotions,
            ["relegations"] = maxima.Relegations,
            ["highest_effective_bonus"] = maxima.EffectiveBonus,
            ["longest_title_streak"] = maxima.TitleStreak,
            ["longest_stage_win_streak"] = maxima.StageWinStreak,
        };
}
