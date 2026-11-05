using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Cups.GetColorCupIndividualProgress;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Format-aware progress for
/// the individual Color Cup: rounds 1..32 for phased editions, 1..16 for
/// historical legacy editions. Provisional standings come only from persisted
/// round payloads (last payload RankAfter/CumulativeAfter); the round-16
/// cutoff uses the same orchestrator as the runner once qualifying is
/// complete, so the displayed top-16 boundary matches the persisted round-17
/// roster. Carryover shows round-16 cumulative-after versus round-17
/// cumulative-before per finalist. Read-only; never resimulates, never touches
/// league standings, never consumes RNG.
/// </summary>
public sealed class GetColorCupIndividualProgressHandler
{
    private readonly SaveStore _store;

    public GetColorCupIndividualProgressHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetColorCupIndividualProgressResponse> HandleAsync(
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

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SeasonEntity> LoadSourceAsync(
        SaveDbContext context,
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (explicitSeason is null)
            {
                throw new GetColorCupIndividualProgressNotFoundException(
                    $"Color Cup individual progress for Season {sourceSeasonNumber.Value} has not started yet.");
            }

            bool hasRounds = await context.ColorCupIndividualRounds
                .AsNoTracking()
                .AnyAsync(e => e.SourceSeasonId == explicitSeason.Id, cancellationToken)
                .ConfigureAwait(false);
            if (!hasRounds)
            {
                throw new GetColorCupIndividualProgressNotFoundException(
                    $"Color Cup individual progress for Season {sourceSeasonNumber.Value} has not started yet.");
            }

            return explicitSeason;
        }

        List<int> seasonIds = await context.ColorCupIndividualRounds
            .AsNoTracking()
            .Select(e => e.SourceSeasonId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (seasonIds.Count == 0)
        {
            throw new GetColorCupIndividualProgressNotFoundException(
                "Color Cup individual event has not started yet.");
        }

        int latestSeasonId = seasonIds.Max();
        SeasonEntity? latest = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latestSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new InvalidOperationException("Color Cup individual event references an unknown season.");
        }

        return latest;
    }

    internal static async Task<GetColorCupIndividualProgressResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        List<ColorCupIndividualRoundEntity> rows = await context.ColorCupIndividualRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new GetColorCupIndividualProgressNotFoundException(
                $"Color Cup individual progress for Season {source.SeasonNumber} has not started yet.");
        }

        int formatVersion = ColorCupIndividualInvariants.ResolvePersistedFormat(rows);
        List<ColorCupIndividualRoundPayloadDocument> payloads = rows
            .Select(r => ColorCupIndividualRoundPayloadDocument.FromStored(r.PayloadJson))
            .ToList();
        ValidateProgressShapes(rows, payloads, formatVersion);
        int totalRounds = ColorCupIndividualFormat.TotalRounds(formatVersion);
        bool isPhased = ColorCupIndividualFormat.IsPhased(formatVersion);
        int roundsPlayed = rows.Count;
        bool roundsComplete = roundsPlayed == totalRounds;
        bool hasStandings = await context.ColorCupIndividualStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (roundsComplete && !hasStandings)
        {
            throw new InvalidOperationException(
                $"Color Cup individual event for Season {source.SeasonNumber} has all rounds but no standings; sporting state is corrupt.");
        }

        if (!roundsComplete && hasStandings)
        {
            throw new InvalidOperationException(
                $"Color Cup individual event for Season {source.SeasonNumber} has standings before all rounds; sporting state is corrupt.");
        }

        if (roundsComplete)
        {
            return await BuildCompletedAsync(context, saveId, source, payloads, formatVersion, rules, cancellationToken).ConfigureAwait(false);
        }

        return await BuildInProgressAsync(context, saveId, source, rows, payloads, formatVersion, rules, cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateProgressShapes(
        IReadOnlyList<ColorCupIndividualRoundEntity> rows,
        IReadOnlyList<ColorCupIndividualRoundPayloadDocument> payloads,
        int formatVersion)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(payloads);
        if (rows.Count != payloads.Count)
        {
            throw new InvalidOperationException("Color Cup round rows and payloads are out of sync.");
        }

        for (int index = 0; index < rows.Count; index++)
        {
            if (rows[index].RoundNumber != index + 1 || payloads[index].RoundNumber != index + 1)
            {
                throw new InvalidOperationException(
                    $"Color Cup — individual round sequence is corrupt: expected round {index + 1}.");
            }

            int expectedField = ColorCupIndividualFormat.ExpectedFieldSize(index + 1, formatVersion);
            if (payloads[index].Placements.Count != expectedField)
            {
                throw new InvalidOperationException(
                    $"Color Cup round {index + 1} must contain exactly {expectedField} placements, was {payloads[index].Placements.Count}.");
            }
        }

        if (isPhasedFinalStarted(formatVersion, rows.Count))
        {
            HashSet<int> firstFinal = payloads[ColorCupIndividualFormat.QualifyingRounds].Placements.Select(p => p.AthleteId).ToHashSet();
            if (firstFinal.Count != ColorCupIndividualFormat.FinalFieldSize)
            {
                throw new InvalidOperationException(
                    $"Color Cup final must hold exactly {ColorCupIndividualFormat.FinalFieldSize} qualifiers, was {firstFinal.Count}.");
            }

            for (int i = ColorCupIndividualFormat.QualifyingRounds + 1; i < payloads.Count; i++)
            {
                HashSet<int> roundIds = payloads[i].Placements.Select(p => p.AthleteId).ToHashSet();
                if (!roundIds.SetEquals(firstFinal))
                {
                    throw new InvalidOperationException(
                        $"Color Cup — individual final round {payloads[i].RoundNumber} roster does not match the round-17 qualifiers.");
                }
            }
        }
    }

    private static bool isPhasedFinalStarted(int formatVersion, int roundsPlayed) =>
        ColorCupIndividualFormat.IsPhased(formatVersion) && roundsPlayed > ColorCupIndividualFormat.QualifyingRounds;

    internal static async Task<GetColorCupIndividualProgressResponse> BuildCompletedAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        List<ColorCupIndividualRoundPayloadDocument> payloads,
        int formatVersion,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        GetColorCupIndividualResultResponse result =
            await GetColorCupIndividualResultHandler.BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
        Dictionary<int, GetColorCupIndividualProgressMember> members = result.Standings
            .ToDictionary(m => m.AthleteId, m => new GetColorCupIndividualProgressMember(
                m.AthleteId,
                m.Name,
                m.SportingColor,
                m.SelectionRank,
                m.CupRank,
                m.CupScoreThousandths,
                m.QualifyingRank,
                m.QualifiedForFinal.HasValue ? (m.QualifiedForFinal.Value ? "Qualified" : "Eliminated") : null,
                m.QualifyingScoreThousandths,
                m.FinalScoreThousandths,
                m.ImageUrl));
        List<GetColorCupIndividualProgressMember> ordered = members.Values.OrderBy(m => m.ProvisionalRank).ToList();
        IReadOnlyList<GetColorCupIndividualCarryover>? carryover =
            BuildCarryover(payloads, formatVersion, members.ToDictionary(p => p.Key, p => p.Value.Name));
        return new GetColorCupIndividualProgressResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            result.FormatVersion,
            result.IsPhased,
            result.Rounds,
            result.Rounds,
            true,
            ColorCupIndividualFormat.QualifyingRounds,
            result.CutoffRank,
            result.FinalFieldSize,
            result.QualifyingOrderAthleteIds,
            result.QualifierAthleteIds,
            result.EliminatedAthleteIds,
            result.TieDrawConsumed,
            false,
            ordered,
            carryover);
    }

    internal static async Task<GetColorCupIndividualProgressResponse> BuildInProgressAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        List<ColorCupIndividualRoundEntity> rows,
        List<ColorCupIndividualRoundPayloadDocument> payloads,
        int formatVersion,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        bool isPhased = ColorCupIndividualFormat.IsPhased(formatVersion);
        int totalRounds = ColorCupIndividualFormat.TotalRounds(formatVersion);
        int roundsPlayed = rows.Count;
        Dictionary<int, string> names = await LoadNamesAsync(context, payloads, cancellationToken).ConfigureAwait(false);
        Dictionary<int, (string Color, int SelectionRank, string? ImageUrl)> meta =
            await LoadMetaAsync(context, source, payloads, cancellationToken).ConfigureAwait(false);
        if (!isPhased)
        {
            List<GetColorCupIndividualProgressMember> legacy = BuildQualifyingProvisional(
                payloads[^1], names, meta, provisionalCutoff: false);
            return new GetColorCupIndividualProgressResponse(
                saveId, source.SeasonNumber, source.Id, formatVersion, false,
                totalRounds, roundsPlayed, false,
                ColorCupIndividualFormat.QualifyingRounds,
                null, null, null, null, null, null, false, legacy, null);
        }

        if (roundsPlayed <= ColorCupIndividualFormat.QualifyingRounds)
        {
            return BuildQualifyingPhase(saveId, source, payloads, formatVersion, totalRounds, roundsPlayed, rules, names, meta);
        }

        return BuildFinalPhase(saveId, source, payloads, formatVersion, totalRounds, roundsPlayed, rules, names, meta);
    }

    internal static GetColorCupIndividualProgressResponse BuildQualifyingPhase(
        Guid saveId,
        SeasonEntity source,
        List<ColorCupIndividualRoundPayloadDocument> payloads,
        int formatVersion,
        int totalRounds,
        int roundsPlayed,
        RulesV1 rules,
        Dictionary<int, string> names,
        Dictionary<int, (string Color, int SelectionRank, string? ImageUrl)> meta)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(rules);
        if (roundsPlayed < ColorCupIndividualFormat.QualifyingRounds)
        {
            List<GetColorCupIndividualProgressMember> provisional = BuildQualifyingProvisional(
                payloads[^1], names, meta, provisionalCutoff: true);
            List<int> order = provisional.OrderBy(m => m.ProvisionalRank).Select(m => m.AthleteId).ToList();
            return new GetColorCupIndividualProgressResponse(
                saveId, source.SeasonNumber, source.Id, formatVersion, true,
                totalRounds, roundsPlayed, false,
                ColorCupIndividualFormat.QualifyingRounds,
                ColorCupIndividualFormat.CutoffRank,
                ColorCupIndividualFormat.FinalFieldSize,
                order,
                order.Take(ColorCupIndividualFormat.CutoffRank).ToList(),
                order.Skip(ColorCupIndividualFormat.CutoffRank).ToList(),
                null, true, provisional, null);
        }

        List<ColorCupIndividualRoundPayloadDocument> qualifying = payloads.Take(ColorCupIndividualFormat.QualifyingRounds).ToList();
        Pcg32State round16After = new(qualifying[^1].RngAfterState, qualifying[^1].RngAfterStream);
        ColorCupIndividualPhasedOrchestrator.CutoffResolution cutoff =
            ColorCupIndividualPhasedOrchestrator.ResolveCutoff(qualifying, rules, round16After);
        Dictionary<int, StageAthleteTotals> qualById = cutoff.QualifyingTotals.ToDictionary(t => t.AthleteId);
        Dictionary<int, int> orderIndex = cutoff.QualifyingOrderAthleteIds
            .Select((id, index) => (id, index))
            .ToDictionary(p => p.id, p => p.index);
        List<GetColorCupIndividualProgressMember> members = new(orderIndex.Count);
        foreach (int athleteId in cutoff.QualifyingOrderAthleteIds)
        {
            StageAthleteTotals totals = qualById[athleteId];
            int qualifyingRank = orderIndex[athleteId] + 1;
            bool qualified = qualifyingRank <= ColorCupIndividualFormat.CutoffRank;
            names.TryGetValue(athleteId, out string? name);
            meta.TryGetValue(athleteId, out (string Color, int SelectionRank, string? ImageUrl) info);
            members.Add(new GetColorCupIndividualProgressMember(
                athleteId,
                name ?? totals.Name,
                info.Color ?? string.Empty,
                info.SelectionRank,
                qualifyingRank,
                totals.StageScoreThousandths,
                qualifyingRank,
                qualified ? "Qualified" : "Eliminated",
                totals.StageScoreThousandths,
                null,
                info.ImageUrl));
        }

        return new GetColorCupIndividualProgressResponse(
            saveId, source.SeasonNumber, source.Id, formatVersion, true,
            totalRounds, roundsPlayed, false,
            ColorCupIndividualFormat.QualifyingRounds,
            ColorCupIndividualFormat.CutoffRank,
            ColorCupIndividualFormat.FinalFieldSize,
            cutoff.QualifyingOrderAthleteIds.ToList(),
            cutoff.QualifierAthleteIds.ToList(),
            cutoff.QualifyingOrderAthleteIds.Skip(ColorCupIndividualFormat.CutoffRank).ToList(),
            cutoff.TieDrawConsumed, false, members, null);
    }

    internal static GetColorCupIndividualProgressResponse BuildFinalPhase(
        Guid saveId,
        SeasonEntity source,
        List<ColorCupIndividualRoundPayloadDocument> payloads,
        int formatVersion,
        int totalRounds,
        int roundsPlayed,
        RulesV1 rules,
        Dictionary<int, string> names,
        Dictionary<int, (string Color, int SelectionRank, string? ImageUrl)> meta)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(rules);
        ColorCupIndividualPhasedOrchestrator.CutoffResolution cutoff = ResolveFinalCutoff(payloads, rules);
        List<GetColorCupIndividualProgressMember> members = BuildFinalMembers(payloads, cutoff, names, meta);
        IReadOnlyList<GetColorCupIndividualCarryover>? carryover =
            BuildCarryover(payloads, formatVersion, names);
        return new GetColorCupIndividualProgressResponse(
            saveId, source.SeasonNumber, source.Id, formatVersion, true,
            totalRounds, roundsPlayed, false,
            ColorCupIndividualFormat.QualifyingRounds,
            ColorCupIndividualFormat.CutoffRank,
            ColorCupIndividualFormat.FinalFieldSize,
            cutoff.QualifyingOrderAthleteIds.ToList(),
            cutoff.QualifierAthleteIds.ToList(),
            cutoff.QualifyingOrderAthleteIds.Skip(ColorCupIndividualFormat.CutoffRank).ToList(),
            cutoff.TieDrawConsumed, false, members, carryover);
    }

    private static ColorCupIndividualPhasedOrchestrator.CutoffResolution ResolveFinalCutoff(
        List<ColorCupIndividualRoundPayloadDocument> payloads,
        RulesV1 rules)
    {
        List<ColorCupIndividualRoundPayloadDocument> qualifying = payloads.Take(ColorCupIndividualFormat.QualifyingRounds).ToList();
        Pcg32State round16After = new(qualifying[^1].RngAfterState, qualifying[^1].RngAfterStream);
        return ColorCupIndividualPhasedOrchestrator.ResolveCutoff(qualifying, rules, round16After);
    }

    private static List<GetColorCupIndividualProgressMember> BuildFinalMembers(
        List<ColorCupIndividualRoundPayloadDocument> payloads,
        ColorCupIndividualPhasedOrchestrator.CutoffResolution cutoff,
        Dictionary<int, string> names,
        Dictionary<int, (string Color, int SelectionRank, string? ImageUrl)> meta)
    {
        Dictionary<int, StageAthleteTotals> qualById = cutoff.QualifyingTotals.ToDictionary(t => t.AthleteId);
        Dictionary<int, int> orderIndex = cutoff.QualifyingOrderAthleteIds
            .Select((id, index) => (id, index))
            .ToDictionary(p => p.id, p => p.index);
        List<(int AthleteId, int Combined)> combined = SortFinalistsByCombined(payloads, cutoff, names);
        List<GetColorCupIndividualProgressMember> members = BuildQualifiedMembers(combined, qualById, orderIndex, names, meta);
        AddEliminatedMembers(members, cutoff, qualById, orderIndex, names, meta);
        return members;
    }

    private static List<(int AthleteId, int Combined)> SortFinalistsByCombined(
        List<ColorCupIndividualRoundPayloadDocument> payloads,
        ColorCupIndividualPhasedOrchestrator.CutoffResolution cutoff,
        Dictionary<int, string> names)
    {
        Dictionary<int, RoundPayloadEntry> lastById = payloads[^1].Placements.ToDictionary(p => p.AthleteId);
        List<(int AthleteId, int Combined)> combined = new(cutoff.QualifierAthleteIds.Count);
        foreach (int id in cutoff.QualifierAthleteIds)
        {
            combined.Add((id, lastById[id].CumulativeAfterThousandths));
        }

        combined.Sort((left, right) =>
        {
            int score = right.Combined.CompareTo(left.Combined);
            if (score != 0)
            {
                return score;
            }

            names.TryGetValue(left.AthleteId, out string? leftName);
            names.TryGetValue(right.AthleteId, out string? rightName);
            return string.Compare(leftName, rightName, StringComparison.Ordinal);
        });
        return combined;
    }

    private static List<GetColorCupIndividualProgressMember> BuildQualifiedMembers(
        List<(int AthleteId, int Combined)> combined,
        Dictionary<int, StageAthleteTotals> qualById,
        Dictionary<int, int> orderIndex,
        Dictionary<int, string> names,
        Dictionary<int, (string Color, int SelectionRank, string? ImageUrl)> meta)
    {
        List<GetColorCupIndividualProgressMember> members = new(combined.Count + ColorCupIndividualFormat.CutoffRank);
        for (int i = 0; i < combined.Count; i++)
        {
            members.Add(BuildOneQualified(combined[i], i, qualById, orderIndex, names, meta));
        }

        return members;
    }

    private static GetColorCupIndividualProgressMember BuildOneQualified(
        (int AthleteId, int Combined) entry,
        int index,
        Dictionary<int, StageAthleteTotals> qualById,
        Dictionary<int, int> orderIndex,
        Dictionary<int, string> names,
        Dictionary<int, (string Color, int SelectionRank, string? ImageUrl)> meta)
    {
        int qualifyingScore = qualById[entry.AthleteId].StageScoreThousandths;
        int finalSoFar = checked(entry.Combined - qualifyingScore);
        int qualifyingRank = orderIndex[entry.AthleteId] + 1;
        names.TryGetValue(entry.AthleteId, out string? name);
        meta.TryGetValue(entry.AthleteId, out (string Color, int SelectionRank, string? ImageUrl) info);
        return new GetColorCupIndividualProgressMember(
            entry.AthleteId,
            name ?? qualById[entry.AthleteId].Name,
            info.Color ?? string.Empty,
            info.SelectionRank,
            index + 1,
            entry.Combined,
            qualifyingRank,
            "Qualified",
            qualifyingScore,
            finalSoFar,
            info.ImageUrl);
    }

    private static void AddEliminatedMembers(
        List<GetColorCupIndividualProgressMember> members,
        ColorCupIndividualPhasedOrchestrator.CutoffResolution cutoff,
        Dictionary<int, StageAthleteTotals> qualById,
        Dictionary<int, int> orderIndex,
        Dictionary<int, string> names,
        Dictionary<int, (string Color, int SelectionRank, string? ImageUrl)> meta)
    {
        foreach (int athleteId in cutoff.QualifyingOrderAthleteIds.Skip(ColorCupIndividualFormat.CutoffRank))
        {
            members.Add(BuildOneEliminated(athleteId, members.Count + 1, qualById, orderIndex, names, meta));
        }
    }

    private static GetColorCupIndividualProgressMember BuildOneEliminated(
        int athleteId,
        int rank,
        Dictionary<int, StageAthleteTotals> qualById,
        Dictionary<int, int> orderIndex,
        Dictionary<int, string> names,
        Dictionary<int, (string Color, int SelectionRank, string? ImageUrl)> meta)
    {
        int qualifyingScore = qualById[athleteId].StageScoreThousandths;
        int qualifyingRank = orderIndex[athleteId] + 1;
        names.TryGetValue(athleteId, out string? name);
        meta.TryGetValue(athleteId, out (string Color, int SelectionRank, string? ImageUrl) info);
        return new GetColorCupIndividualProgressMember(
            athleteId,
            name ?? qualById[athleteId].Name,
            info.Color ?? string.Empty,
            info.SelectionRank,
            rank,
            qualifyingScore,
            qualifyingRank,
            "Eliminated",
            qualifyingScore,
            0,
            info.ImageUrl);
    }

    internal static List<GetColorCupIndividualProgressMember> BuildQualifyingProvisional(
        ColorCupIndividualRoundPayloadDocument last,
        Dictionary<int, string> names,
        Dictionary<int, (string Color, int SelectionRank, string? ImageUrl)> meta,
        bool provisionalCutoff)
    {
        ArgumentNullException.ThrowIfNull(last);
        List<GetColorCupIndividualProgressMember> members = new(last.Placements.Count);
        foreach (RoundPayloadEntry placement in last.Placements.OrderBy(p => p.RankAfter))
        {
            names.TryGetValue(placement.AthleteId, out string? name);
            meta.TryGetValue(placement.AthleteId, out (string Color, int SelectionRank, string? ImageUrl) info);
            string? status = null;
            int? qualifyingRank = null;
            if (provisionalCutoff)
            {
                qualifyingRank = placement.RankAfter;
                status = placement.RankAfter <= ColorCupIndividualFormat.CutoffRank
                    ? "ProvisionalQualified"
                    : "ProvisionalEliminated";
            }

            members.Add(new GetColorCupIndividualProgressMember(
                placement.AthleteId,
                name ?? placement.Name,
                info.Color ?? string.Empty,
                info.SelectionRank,
                placement.RankAfter,
                placement.CumulativeAfterThousandths,
                qualifyingRank,
                status,
                provisionalCutoff ? placement.CumulativeAfterThousandths : null,
                null,
                info.ImageUrl));
        }

        return members;
    }

    internal static IReadOnlyList<GetColorCupIndividualCarryover>? BuildCarryover(
        IReadOnlyList<ColorCupIndividualRoundPayloadDocument> payloads,
        int formatVersion,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(names);
        if (!ColorCupIndividualFormat.IsPhased(formatVersion)
            || payloads.Count <= ColorCupIndividualFormat.QualifyingRounds)
        {
            return null;
        }

        ColorCupIndividualRoundPayloadDocument round16 = payloads[ColorCupIndividualFormat.QualifyingRounds - 1];
        ColorCupIndividualRoundPayloadDocument round17 = payloads[ColorCupIndividualFormat.QualifyingRounds];
        Dictionary<int, int> carried = round16.Placements.ToDictionary(p => p.AthleteId, p => p.CumulativeAfterThousandths);
        List<GetColorCupIndividualCarryover> entries = new(round17.Placements.Count);
        foreach (RoundPayloadEntry entry in round17.Placements.OrderBy(p => p.AthleteId))
        {
            if (!carried.TryGetValue(entry.AthleteId, out int qualifyingScore))
            {
                throw new InvalidOperationException(
                    $"Color Cup finalist '{entry.Name}' has no qualifying total to carry over.");
            }

            names.TryGetValue(entry.AthleteId, out string? name);
            entries.Add(new GetColorCupIndividualCarryover(
                entry.AthleteId,
                name ?? entry.Name,
                qualifyingScore,
                entry.CumulativeBeforeThousandths,
                entry.CumulativeBeforeThousandths == qualifyingScore));
        }

        return entries;
    }

    internal static async Task<Dictionary<int, string>> LoadNamesAsync(
        SaveDbContext context,
        List<ColorCupIndividualRoundPayloadDocument> payloads,
        CancellationToken cancellationToken)
    {
        HashSet<int> ids = payloads.SelectMany(p => p.Placements.Select(e => e.AthleteId)).ToHashSet();
        return await context.SaveAthletes
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, (string Color, int SelectionRank, string? ImageUrl)>> LoadMetaAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<ColorCupIndividualRoundPayloadDocument> payloads,
        CancellationToken cancellationToken)
    {
        HashSet<int> ids = payloads.SelectMany(p => p.Placements.Select(e => e.AthleteId)).ToHashSet();
        Dictionary<int, SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, ColorCupSelectionEntity> selections = await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToDictionaryAsync(e => e.SaveAthleteId, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, (string Color, int SelectionRank, string? ImageUrl)> meta = new(ids.Count);
        foreach (int id in ids)
        {
            athletes.TryGetValue(id, out SaveAthleteEntity? athlete);
            selections.TryGetValue(id, out ColorCupSelectionEntity? selection);
            string color = selection is null
                ? string.Empty
                : ((SportingColor)selection.SportingColor).ToString();
            meta[id] = (color, selection?.SelectionRank ?? 0, athlete?.ImageUrl);
        }

        return meta;
    }
}
