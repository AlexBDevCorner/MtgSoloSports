using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Cups.GetChaosCupSurvival;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads Chaos Cup survival
/// analytics from persisted draws and official group standings without
/// simulating, drawing or touching sporting RNG: per-stage initial, advancing,
/// eliminated, pending and Finalist counts plus matching breakdowns by the
/// eight permanent sporting colors and by frozen source tier (Pool,
/// Superleague, Feeder 1/2/3). Breakdown sums equal stage totals exactly.
/// Incomplete stages stay provisional and undrawn stages report zeros, never
/// complete. Throws <see cref="GetChaosCupSurvivalNotFoundException"/> (404)
/// when the season has no edition; aborts on corrupt counts. Reads never lock
/// and use a fixed set of batched queries (never N+1, never payloads).
/// </summary>
public sealed class GetChaosCupSurvivalHandler
{
    private readonly SaveStore _store;

    public GetChaosCupSurvivalHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetChaosCupSurvivalResponse> HandleAsync(
        Guid saveId,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        var rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSourceAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        ChaosCupEditionEntity edition = await context.ChaosCupEditions
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new GetChaosCupSurvivalNotFoundException(
                $"Chaos Cup survival for Season {source.SeasonNumber} has not been resolved yet.");
        ChaosCupInvariants.ValidateEdition(edition, rules);

        List<ChaosCupEntrantEntity> entrants = await context.ChaosCupEntrants
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ChaosCupInvariants.ValidateEntrants(source, edition, entrants);

        List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> standings = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return BuildResponse(saveId, source, edition, entrants, draws, standings);
    }

    internal static async Task<SeasonEntity> LoadSourceAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (explicitSeason is null)
            {
                throw new GetChaosCupSurvivalNotFoundException(
                    $"Chaos Cup survival for Season {sourceSeasonNumber.Value} has not been resolved yet.");
            }

            return explicitSeason;
        }

        ChaosCupEditionEntity? latest = await context.ChaosCupEditions
            .AsNoTracking()
            .OrderByDescending(e => e.SourceSeasonNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new GetChaosCupSurvivalNotFoundException("Chaos Cup survival has not been resolved yet.");
        }

        SeasonEntity? source = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latest.SourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
        return source ?? throw new InvalidOperationException("Chaos Cup edition references an unknown season.");
    }

    internal static GetChaosCupSurvivalResponse BuildResponse(
        Guid saveId,
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        List<ChaosCupEntrantEntity> entrants,
        List<ChaosCupDrawEntity> draws,
        List<ChaosCupGroupStandingEntity> standings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(standings);

        Dictionary<int, ChaosCupEntrantEntity> entrantByAthlete = entrants.ToDictionary(e => e.SaveAthleteId);
        Dictionary<int, List<ChaosCupDrawEntity>> drawsByStage = draws
            .GroupBy(d => d.StageIndex)
            .ToDictionary(g => g.Key, g => g.ToList());
        Dictionary<int, List<ChaosCupGroupStandingEntity>> standingsByStage = standings
            .GroupBy(s => s.StageIndex)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (int stage in drawsByStage.Keys.Concat(standingsByStage.Keys).Distinct())
        {
            if (stage < 0 || stage >= ChaosCupFormat.StageCount)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stage} is out of range.");
            }
        }

        List<GetChaosCupSurvivalResponse.StageSurvival> stages = new(ChaosCupFormat.StageCount);
        for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
        {
            drawsByStage.TryGetValue(stage, out List<ChaosCupDrawEntity>? stageDraws);
            stageDraws ??= [];
            standingsByStage.TryGetValue(stage, out List<ChaosCupGroupStandingEntity>? stageStandings);
            stageStandings ??= [];
            if (stageDraws.Count != 0)
            {
                ChaosCupInvariants.ValidateStageDraw(source, edition, entrants, stageDraws, stage);
            }

            ValidateStageStandings(source, stage, stageDraws, stageStandings, entrantByAthlete);
            stages.Add(BuildStage(stage, stageDraws, stageStandings, entrantByAthlete));
        }

        return new GetChaosCupSurvivalResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            edition.RulesVersion,
            edition.ChaosFormatVersion,
            edition.FieldSize,
            stages.All(s => s.IsComplete),
            stages);
    }

    internal static void ValidateStageStandings(
        SeasonEntity source,
        int stageIndex,
        List<ChaosCupDrawEntity> stageDraws,
        List<ChaosCupGroupStandingEntity> stageStandings,
        Dictionary<int, ChaosCupEntrantEntity> entrantByAthlete)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stageDraws);
        ArgumentNullException.ThrowIfNull(stageStandings);
        ArgumentNullException.ThrowIfNull(entrantByAthlete);
        if (stageStandings.Count == 0)
        {
            return;
        }

        if (stageDraws.Count == 0)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} has standings without a persisted draw.");
        }

        HashSet<int> drawn = stageDraws.Select(d => d.SaveAthleteId).ToHashSet();
        HashSet<int> seen = new();
        bool isFinal = ChaosCupFormat.IsFinalStage(stageIndex);
        foreach (ChaosCupGroupStandingEntity standing in stageStandings)
        {
            if (!drawn.Contains(standing.SaveAthleteId))
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} ranks athlete {standing.SaveAthleteId} outside its draw.");
            }

            if (!seen.Add(standing.SaveAthleteId))
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} ranks athlete {standing.SaveAthleteId} more than once.");
            }

            if (!entrantByAthlete.ContainsKey(standing.SaveAthleteId))
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} ranks athlete {standing.SaveAthleteId} outside the frozen field.");
            }

            if (standing.GroupRank < 1 || standing.GroupRank > ChaosCupFormat.GroupSize)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} rank {standing.GroupRank} is out of range.");
            }

            bool shouldAdvance = !isFinal && standing.GroupRank <= ChaosCupFormat.AdvancingPerGroup;
            if (standing.IsAdvanced != shouldAdvance)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} rank {standing.GroupRank} has corrupt advancement flag.");
            }
        }

        if (stageStandings.Count > stageDraws.Count)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} holds {stageStandings.Count} standings for {stageDraws.Count} drawn athletes.");
        }

        if (stageStandings.Count % ChaosCupFormat.GroupSize != 0)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} holds {stageStandings.Count} standings, expected multiples of {ChaosCupFormat.GroupSize}.");
        }
    }

    internal static GetChaosCupSurvivalResponse.StageSurvival BuildStage(
        int stageIndex,
        List<ChaosCupDrawEntity> stageDraws,
        List<ChaosCupGroupStandingEntity> stageStandings,
        Dictionary<int, ChaosCupEntrantEntity> entrantByAthlete)
    {
        bool isFinal = ChaosCupFormat.IsFinalStage(stageIndex);
        int expected = ChaosCupFormat.EntrantCountForStage(stageIndex);
        bool isDrawn = stageDraws.Count != 0;
        bool isComplete = isDrawn && stageDraws.Count == expected && stageStandings.Count == expected;

        int initial = stageDraws.Count;
        int advanced;
        int eliminated;
        int finalists;
        int pending;
        if (isFinal)
        {
            finalists = stageStandings.Count;
            advanced = 0;
            eliminated = 0;
            pending = checked(initial - finalists);
        }
        else
        {
            advanced = stageStandings.Count(s => s.IsAdvanced);
            eliminated = stageStandings.Count(s => !s.IsAdvanced);
            finalists = 0;
            pending = checked(initial - advanced - eliminated);
        }

        if (pending < 0)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} counts do not sum: initial {initial}, advanced {advanced}, eliminated {eliminated}, finalists {finalists}.");
        }

        IReadOnlyList<GetChaosCupSurvivalResponse.BucketCount> byColor = BuildByColor(
            stageIndex, stageDraws, stageStandings, entrantByAthlete);
        IReadOnlyList<GetChaosCupSurvivalResponse.BucketCount> byDivision = BuildByDivision(
            stageIndex, stageDraws, stageStandings, entrantByAthlete);
        EnsureSums(stageIndex, initial, advanced, eliminated, pending, finalists, byColor, byDivision);

        return new GetChaosCupSurvivalResponse.StageSurvival(
            stageIndex,
            ChaosCupFormat.GroupCountForStage(stageIndex),
            expected,
            isFinal,
            isDrawn,
            isComplete,
            isDrawn && !isComplete,
            initial,
            advanced,
            eliminated,
            pending,
            finalists,
            byColor,
            byDivision);
    }

    internal static IReadOnlyList<GetChaosCupSurvivalResponse.BucketCount> BuildByColor(
        int stageIndex,
        List<ChaosCupDrawEntity> stageDraws,
        List<ChaosCupGroupStandingEntity> stageStandings,
        Dictionary<int, ChaosCupEntrantEntity> entrantByAthlete)
    {
        bool isFinal = ChaosCupFormat.IsFinalStage(stageIndex);
        Dictionary<SportingColor, List<int>> drawn = GroupDrawsByColor(stageDraws, entrantByAthlete);
        Dictionary<SportingColor, List<ChaosCupGroupStandingEntity>> resolved = GroupStandingsByColor(stageStandings, entrantByAthlete);
        List<GetChaosCupSurvivalResponse.BucketCount> buckets = new(8);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            drawn.TryGetValue(color, out List<int>? initialIds);
            resolved.TryGetValue(color, out List<ChaosCupGroupStandingEntity>? decided);
            buckets.Add(ToBucket(color.ToString(), color.ToString(), initialIds?.Count ?? 0, decided, isFinal));
        }

        return buckets;
    }

    internal static Dictionary<SportingColor, List<int>> GroupDrawsByColor(
        List<ChaosCupDrawEntity> stageDraws,
        Dictionary<int, ChaosCupEntrantEntity> entrantByAthlete)
    {
        ArgumentNullException.ThrowIfNull(stageDraws);
        ArgumentNullException.ThrowIfNull(entrantByAthlete);
        Dictionary<SportingColor, List<int>> grouped = new();
        foreach (ChaosCupDrawEntity draw in stageDraws)
        {
            SportingColor color = (SportingColor)entrantByAthlete[draw.SaveAthleteId].SportingColor;
            if (!grouped.TryGetValue(color, out List<int>? members))
            {
                members = [];
                grouped[color] = members;
            }

            members.Add(draw.SaveAthleteId);
        }

        return grouped;
    }

    internal static Dictionary<SportingColor, List<ChaosCupGroupStandingEntity>> GroupStandingsByColor(
        List<ChaosCupGroupStandingEntity> stageStandings,
        Dictionary<int, ChaosCupEntrantEntity> entrantByAthlete)
    {
        ArgumentNullException.ThrowIfNull(stageStandings);
        ArgumentNullException.ThrowIfNull(entrantByAthlete);
        Dictionary<SportingColor, List<ChaosCupGroupStandingEntity>> grouped = new();
        foreach (ChaosCupGroupStandingEntity standing in stageStandings)
        {
            SportingColor color = (SportingColor)entrantByAthlete[standing.SaveAthleteId].SportingColor;
            if (!grouped.TryGetValue(color, out List<ChaosCupGroupStandingEntity>? members))
            {
                members = [];
                grouped[color] = members;
            }

            members.Add(standing);
        }

        return grouped;
    }

    internal static GetChaosCupSurvivalResponse.BucketCount ToBucket(
        string key,
        string label,
        int initial,
        List<ChaosCupGroupStandingEntity>? resolved,
        bool isFinal)
    {
        int advanced;
        int eliminated;
        int finalists;
        if (isFinal)
        {
            finalists = resolved?.Count ?? 0;
            advanced = 0;
            eliminated = 0;
        }
        else
        {
            advanced = resolved?.Count(s => s.IsAdvanced) ?? 0;
            eliminated = resolved?.Count(s => !s.IsAdvanced) ?? 0;
            finalists = 0;
        }

        return new GetChaosCupSurvivalResponse.BucketCount(
            key, label, initial, advanced, eliminated,
            checked(initial - advanced - eliminated - finalists), finalists);
    }

    internal static IReadOnlyList<GetChaosCupSurvivalResponse.BucketCount> BuildByDivision(
        int stageIndex,
        List<ChaosCupDrawEntity> stageDraws,
        List<ChaosCupGroupStandingEntity> stageStandings,
        Dictionary<int, ChaosCupEntrantEntity> entrantByAthlete)
    {
        bool isFinal = ChaosCupFormat.IsFinalStage(stageIndex);
        string[] order = ["Pool", "Superleague", "Feeder1", "Feeder2", "Feeder3"];
        Dictionary<string, List<int>> drawn = GroupDrawsByDivision(stageDraws, entrantByAthlete);
        Dictionary<string, List<ChaosCupGroupStandingEntity>> resolved = GroupStandingsByDivision(stageStandings, entrantByAthlete);
        List<GetChaosCupSurvivalResponse.BucketCount> buckets = new(order.Length);
        foreach (string key in order)
        {
            drawn.TryGetValue(key, out List<int>? initialIds);
            resolved.TryGetValue(key, out List<ChaosCupGroupStandingEntity>? decided);
            buckets.Add(ToBucket(key, DivisionLabelFor(key), initialIds?.Count ?? 0, decided, isFinal));
        }

        return buckets;
    }

    internal static Dictionary<string, List<int>> GroupDrawsByDivision(
        List<ChaosCupDrawEntity> stageDraws,
        Dictionary<int, ChaosCupEntrantEntity> entrantByAthlete)
    {
        ArgumentNullException.ThrowIfNull(stageDraws);
        ArgumentNullException.ThrowIfNull(entrantByAthlete);
        Dictionary<string, List<int>> grouped = new(StringComparer.Ordinal);
        foreach (ChaosCupDrawEntity draw in stageDraws)
        {
            string key = DivisionKeyFor(entrantByAthlete[draw.SaveAthleteId]);
            if (!grouped.TryGetValue(key, out List<int>? members))
            {
                members = [];
                grouped[key] = members;
            }

            members.Add(draw.SaveAthleteId);
        }

        return grouped;
    }

    internal static Dictionary<string, List<ChaosCupGroupStandingEntity>> GroupStandingsByDivision(
        List<ChaosCupGroupStandingEntity> stageStandings,
        Dictionary<int, ChaosCupEntrantEntity> entrantByAthlete)
    {
        ArgumentNullException.ThrowIfNull(stageStandings);
        ArgumentNullException.ThrowIfNull(entrantByAthlete);
        Dictionary<string, List<ChaosCupGroupStandingEntity>> grouped = new(StringComparer.Ordinal);
        foreach (ChaosCupGroupStandingEntity standing in stageStandings)
        {
            string key = DivisionKeyFor(entrantByAthlete[standing.SaveAthleteId]);
            if (!grouped.TryGetValue(key, out List<ChaosCupGroupStandingEntity>? members))
            {
                members = [];
                grouped[key] = members;
            }

            members.Add(standing);
        }

        return grouped;
    }

    internal static string DivisionKeyFor(ChaosCupEntrantEntity entrant)
    {
        ArgumentNullException.ThrowIfNull(entrant);
        if (entrant.IsPoolInvite)
        {
            return "Pool";
        }

        if (entrant.SourceLeagueKind == (int)LeagueKind.Superleague)
        {
            return "Superleague";
        }

        return LeagueHierarchy.LevelForDivision((FeederDivision)entrant.SourceFeederDivision, isSuperleague: false) switch
        {
            LeagueLevel.Feeder1 => "Feeder1",
            LeagueLevel.Feeder2 => "Feeder2",
            LeagueLevel.Feeder3 => "Feeder3",
            _ => throw new InvalidOperationException($"Chaos Cup entrant {entrant.SaveAthleteId} has unknown division {entrant.SourceFeederDivision}."),
        };
    }

    internal static string DivisionLabelFor(string key) => key switch
    {
        "Pool" => "Pool",
        "Superleague" => "Superleague",
        "Feeder1" => "Feeder 1",
        "Feeder2" => "Feeder 2",
        "Feeder3" => "Feeder 3",
        _ => throw new ArgumentOutOfRangeException(nameof(key), $"Unknown Chaos division key '{key}'."),
    };

    internal static void EnsureSums(
        int stageIndex,
        int initial,
        int advanced,
        int eliminated,
        int pending,
        int finalists,
        IReadOnlyList<GetChaosCupSurvivalResponse.BucketCount> byColor,
        IReadOnlyList<GetChaosCupSurvivalResponse.BucketCount> byDivision)
    {
        ArgumentNullException.ThrowIfNull(byColor);
        ArgumentNullException.ThrowIfNull(byDivision);
        CheckBucketSums(stageIndex, "color", initial, advanced, eliminated, pending, finalists, byColor);
        CheckBucketSums(stageIndex, "division", initial, advanced, eliminated, pending, finalists, byDivision);
    }

    private static void CheckBucketSums(
        int stageIndex,
        string kind,
        int initial,
        int advanced,
        int eliminated,
        int pending,
        int finalists,
        IReadOnlyList<GetChaosCupSurvivalResponse.BucketCount> buckets)
    {
        if (buckets.Sum(b => b.Initial) != initial
            || buckets.Sum(b => b.Advanced) != advanced
            || buckets.Sum(b => b.Eliminated) != eliminated
            || buckets.Sum(b => b.Pending) != pending
            || buckets.Sum(b => b.Finalists) != finalists)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} {kind} breakdown does not sum to stage totals.");
        }
    }
}
