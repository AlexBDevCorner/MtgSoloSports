using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Cups.RunColorCupIndividual;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Runs the 32 selected Color
/// Cup athletes through one standard 16-round individual stage for a completed
/// odd source season. Active career bonus at the next-season Stage 1 boundary
/// applies with normal fixed-point scoring/ranking; no new round or stage
/// career bonus is generated, no league championship points are awarded, and
/// no league <c>StageStanding</c>, <c>SeasonStanding</c> or <c>Round</c> rows
/// are created, so normal league season totals and career bonus are untouched.
/// Persists 16 immutable Cup round payloads plus 32 Cup standings (Gold for
/// rank 1, Silver for rank 2, Bronze for rank 3) plus one official individual
/// championship honour for rank 1, plus the RNG-after state, in one
/// transaction. Holds one per-save lock; read-only Cup queries never lock.
/// </summary>
public sealed class RunColorCupIndividualHandler
{
    public const int CupLeagueId = 0;
    public const int CupLeagueKind = -1;
    public const string CupLeagueName = "Color Cup";

    private readonly SaveStore _store;

    public RunColorCupIndividualHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<RunColorCupIndividualResponse> HandleAsync(
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

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunUnderLockAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<RunColorCupIndividualResponse> RunUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        Pcg32State rngBefore = rngRow.ToState();

        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        EnsureOddSeason(source);
        List<ColorCupSelectionEntity> selection = await LoadSelectionAsync(context, source, cancellationToken).ConfigureAwait(false);
        ColorCupSelectionInvariants.ValidatePersisted(source, selection, rules);
        ColorCupIndividualInvariants.ValidateField(selection, rules);
        await EnsureCupAbsentAsync(context, source, cancellationToken).ConfigureAwait(false);

        (int stageCountBefore, int seasonCountBefore, int roundCountBefore, long lifetimeBefore, long effectiveBefore, long championshipBefore) =
            await CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);

        List<AdvanceRoundHandler.MemberRow> roster = BuildRoster(context, selection);
        Dictionary<int, Bonus> activeBonuses = await LoadCupActiveBonusesAsync(
            context, roster, source, rules, cancellationToken).ConfigureAwait(false);

        CupSimulation simulation = SimulateCup(selection, roster, activeBonuses, rngBefore, source, rules);

        await PersistCupAsync(context, source, simulation, cancellationToken).ConfigureAwait(false);
        context.ApplyRngState(simulation.RngAfter);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await ValidatePersistedAsync(
            context, source, simulation, rules,
            stageCountBefore, seasonCountBefore, roundCountBefore,
            lifetimeBefore, effectiveBefore, championshipBefore,
            cancellationToken).ConfigureAwait(false);
        await EmitCupStoriesAsync(context, source, simulation, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(_store, saveId, source, simulation, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record CupSimulation(
        List<ColorCupIndividualRoundPayloadDocument> Payloads,
        IReadOnlyList<StageAthleteTotals> Totals,
        IReadOnlyList<StageRankedAthlete> Ranked,
        Pcg32State RngAfter,
        string Checksum);

    internal static async Task<SeasonEntity> LoadSourceSeasonAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (explicitSeason is null || !explicitSeason.IsComplete)
            {
                throw new RunColorCupIndividualConflictException(
                    $"Season {sourceSeasonNumber.Value} is not a completed season ready for the Color Cup individual event.");
            }

            return explicitSeason;
        }

        List<ColorCupSelectionEntity> any = await context.ColorCupSelections
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new RunColorCupIndividualConflictException(
                "Color Cup team selection must be resolved before the individual event can run.");
        }

        int latestSourceId = any.Max(e => e.SourceSeasonId);
        SeasonEntity? latest = await context.Seasons
            .SingleOrDefaultAsync(e => e.Id == latestSourceId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null || !latest.IsComplete)
        {
            throw new InvalidOperationException("Color Cup selection references an unknown or incomplete season.");
        }

        return latest;
    }

    internal static void EnsureOddSeason(SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SeasonNumber % 2 == 0)
        {
            throw new RunColorCupIndividualConflictException(
                $"Season {source.SeasonNumber} is even; the Color Cup individual event runs only for odd seasons (even seasons use the Type Cup).");
        }
    }

    internal static async Task<List<ColorCupSelectionEntity>> LoadSelectionAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        List<ColorCupSelectionEntity> rows = await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new RunColorCupIndividualConflictException(
                $"Color Cup team selection for Season {source.SeasonNumber} must be resolved before the individual event can run.");
        }

        return rows;
    }

    internal static async Task EnsureCupAbsentAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool hasStandings = await context.ColorCupIndividualStandings.AnyAsync(
            e => e.SourceSeasonId == source.Id, cancellationToken).ConfigureAwait(false);
        if (hasStandings)
        {
            throw new RunColorCupIndividualConflictException(
                $"Color Cup individual event for Season {source.SeasonNumber} has already been resolved.");
        }

        bool hasRounds = await context.ColorCupIndividualRounds.AnyAsync(
            e => e.SourceSeasonId == source.Id, cancellationToken).ConfigureAwait(false);
        if (hasRounds)
        {
            throw new InvalidOperationException($"Color Cup individual event for Season {source.SeasonNumber} has corrupt partial rounds.");
        }

        bool hasHonour = await context.Honours.AnyAsync(
            e => e.SeasonId == source.Id && e.Kind == (int)Features.Records.HonourKind.ColorCupIndividualChampion,
            cancellationToken).ConfigureAwait(false);
        if (hasHonour)
        {
            throw new InvalidOperationException($"Color Cup individual event for Season {source.SeasonNumber} has corrupt partial honours.");
        }
    }

    internal static List<AdvanceRoundHandler.MemberRow> BuildRoster(
        SaveDbContext context,
        List<ColorCupSelectionEntity> selection)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selection);
        Dictionary<int, string> names = context.SaveAthletes
            .AsNoTracking()
            .Where(e => selection.Select(s => s.SaveAthleteId).Contains(e.Id))
            .ToDictionary(e => e.Id, e => e.Name);
        if (names.Count != selection.Count)
        {
            throw new InvalidOperationException("Color Cup selection references unknown athletes.");
        }

        Dictionary<int, int> colors = context.SaveAthletes
            .AsNoTracking()
            .Where(e => selection.Select(s => s.SaveAthleteId).Contains(e.Id))
            .ToDictionary(e => e.Id, e => e.SportingColor);
        List<AdvanceRoundHandler.MemberRow> roster = new(selection.Count);
        foreach (ColorCupSelectionEntity row in selection)
        {
            roster.Add(new AdvanceRoundHandler.MemberRow(row.SaveAthleteId, names[row.SaveAthleteId], colors[row.SaveAthleteId]));
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (AdvanceRoundHandler.MemberRow row in roster)
        {
            if (!seen.Add(row.Name))
            {
                throw new InvalidOperationException($"Color Cup field contains duplicate athlete '{row.Name}'.");
            }
        }

        roster.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return roster;
    }

    internal static async Task<Dictionary<int, Bonus>> LoadCupActiveBonusesAsync(
        SaveDbContext context,
        List<AdvanceRoundHandler.MemberRow> roster,
        SeasonEntity source,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        Dictionary<int, int> seasonNumbers = await AdvanceRoundHandler.LoadSeasonNumbersAsync(context, source, cancellationToken).ConfigureAwait(false);
        if (!seasonNumbers.TryGetValue(source.Id, out int sourceSeasonNumber))
        {
            throw new InvalidOperationException($"Save has no season row for season {source.SeasonNumber}.");
        }

        int cupSeason = checked(sourceSeasonNumber + 1);
        List<StageStandingEntity> standings = await AdvanceRoundHandler.LoadBonusStandingsAsync(context, roster, cancellationToken).ConfigureAwait(false);
        Dictionary<int, List<BonusContribution>> contributions = AdvanceRoundHandler.GroupBonusContributions(
            roster, standings, seasonNumbers, cupSeason);

        // Cup uses the next-season Stage 1 boundary, identical to the selection
        // bonus component: source-season Stage 32 bonus enters at 80% decay.
        return AdvanceRoundHandler.ComputeStageStartBonuses(roster, contributions, cupSeason, 1, rules);
    }

    internal static CupSimulation SimulateCup(
        List<ColorCupSelectionEntity> selection,
        List<AdvanceRoundHandler.MemberRow> roster,
        Dictionary<int, Bonus> activeBonuses,
        Pcg32State rngBefore,
        SeasonEntity source,
        RulesV1 rules)
    {
        Dictionary<int, Points> cumulative = new(roster.Count);
        List<ColorCupIndividualRoundPayloadDocument> payloads = new(rules.ColorCupIndividualRounds);
        Pcg32State current = rngBefore;

        for (int roundNumber = 1; roundNumber <= rules.ColorCupIndividualRounds; roundNumber++)
        {
            RoundSimulationResult simulation = AdvanceRoundHandler.SimulateRound(roster, cumulative, activeBonuses, current, rules);
            ColorCupIndividualRoundPayloadDocument payload = new(
                ColorCupIndividualRoundPayloadDocument.PayloadVersion,
                rules.Version,
                source.SeasonNumber,
                roundNumber,
                current.State,
                current.Stream,
                simulation.RngAfter.State,
                simulation.RngAfter.Stream,
                simulation.Checksum,
                ToPayloadEntries(simulation));
            ColorCupIndividualInvariants.ValidateRound(payload, rules, current.State, current.Stream);
            payloads.Add(payload);
            foreach (RoundPayloadEntry entry in payload.Placements)
            {
                cumulative[entry.AthleteId] = Points.FromThousandths(entry.CumulativeAfterThousandths);
            }

            current = simulation.RngAfter;
        }

        List<List<StageRoundEntry>> roundEntries = BuildRoundEntries(payloads);
        IReadOnlyList<StageAthleteTotals> totals = StageCalculator.Accumulate(roundEntries, rules);
        Pcg32V1 tieBreakRng = Pcg32V1.Restore(current);
        IReadOnlyList<StageRankedAthlete> ranked = StageCalculator.Rank(totals, tieBreakRng, rules, isSuperleague: false);
        ColorCupIndividualInvariants.ValidateCompletedCup(ranked, totals, rules);
        Pcg32State rngAfter = tieBreakRng.Snapshot();
        string checksum = ComputeChecksum(ranked);
        return new CupSimulation(payloads, totals, ranked, rngAfter, checksum);
    }

    internal static IReadOnlyList<RoundPayloadEntry> ToPayloadEntries(RoundSimulationResult simulation)
    {
        List<RoundPayloadEntry> entries = new(simulation.Placements.Count);
        foreach (RoundPlacement placement in simulation.Placements)
        {
            entries.Add(new RoundPayloadEntry(
                placement.AthleteId,
                placement.Name,
                placement.Position,
                placement.BasePoints.Thousandths,
                placement.ActiveBonus.Thousandths,
                placement.FinalPoints.Thousandths,
                placement.CumulativeBefore.Thousandths,
                placement.CumulativeAfter.Thousandths,
                placement.RankBefore,
                placement.RankAfter,
                placement.RankMovement));
        }

        return entries;
    }

    internal static List<List<StageRoundEntry>> BuildRoundEntries(List<ColorCupIndividualRoundPayloadDocument> payloads)
    {
        List<List<StageRoundEntry>> roundEntries = new(payloads.Count);
        foreach (ColorCupIndividualRoundPayloadDocument payload in payloads)
        {
            List<StageRoundEntry> entries = new(payload.Placements.Count);
            foreach (RoundPayloadEntry placement in payload.Placements)
            {
                entries.Add(new StageRoundEntry(
                    placement.AthleteId,
                    placement.Name,
                    placement.Position,
                    placement.BaseThousandths,
                    placement.FinalThousandths));
            }

            roundEntries.Add(entries);
        }

        return roundEntries;
    }

    /// <summary>
    /// Fingerprints Cup standings: lowercase hex SHA-256 over lines of
    /// <c>Rank:AthleteId:Name:Score:Base</c> in rank order.
    /// SHA-256 is a content fingerprint here, not sporting randomness.
    /// Championship/earned values are intentionally excluded: the Cup persists
    /// no championship points and no new bonus.
    /// </summary>
    public static string ComputeChecksum(IReadOnlyList<StageRankedAthlete> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (StageRankedAthlete entry in ranked)
        {
            builder.Append(entry.StageRank.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.AthleteId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.Name);
            builder.Append(':');
            builder.Append(entry.StageScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.BaseScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('\n');
        }

        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    internal static async Task PersistCupAsync(
        SaveDbContext context,
        SeasonEntity source,
        CupSimulation simulation,
        CancellationToken cancellationToken)
    {
        PersistRoundRows(context, source, simulation);
        await PersistStandingRowsAsync(context, source, simulation, cancellationToken).ConfigureAwait(false);
        PersistChampionHonour(context, source, simulation);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void PersistRoundRows(
        SaveDbContext context,
        SeasonEntity source,
        CupSimulation simulation)
    {
        foreach (ColorCupIndividualRoundPayloadDocument payload in simulation.Payloads)
        {
            context.ColorCupIndividualRounds.Add(new ColorCupIndividualRoundEntity
            {
                SourceSeasonId = source.Id,
                SourceSeasonNumber = source.SeasonNumber,
                RoundNumber = payload.RoundNumber,
                RulesVersion = payload.RulesVersion,
                RngBeforeState = unchecked((long)payload.RngBeforeState),
                RngBeforeStream = unchecked((long)payload.RngBeforeStream),
                RngAfterState = unchecked((long)payload.RngAfterState),
                RngAfterStream = unchecked((long)payload.RngAfterStream),
                PayloadJson = payload.ToJson(),
                PayloadChecksum = payload.Checksum,
            });
        }
    }

    internal static async Task PersistStandingRowsAsync(
        SaveDbContext context,
        SeasonEntity source,
        CupSimulation simulation,
        CancellationToken cancellationToken)
    {
        Dictionary<int, ColorCupSelectionEntity> selectionByAthlete = (await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToDictionary(e => e.SaveAthleteId);
        foreach (StageRankedAthlete entry in simulation.Ranked)
        {
            PersistSingleStanding(context, source, entry, selectionByAthlete);
        }
    }

    internal static void PersistSingleStanding(
        SaveDbContext context,
        SeasonEntity source,
        StageRankedAthlete entry,
        Dictionary<int, ColorCupSelectionEntity> selectionByAthlete)
    {
        if (!selectionByAthlete.TryGetValue(entry.AthleteId, out ColorCupSelectionEntity? selection))
        {
            throw new InvalidOperationException($"Color Cup standing for '{entry.Name}' has no selection provenance.");
        }

        context.ColorCupIndividualStandings.Add(new ColorCupIndividualStandingEntity
        {
            SourceSeasonId = source.Id,
            SourceSeasonNumber = source.SeasonNumber,
            SaveAthleteId = entry.AthleteId,
            CupRank = entry.StageRank,
            CupScoreThousandths = entry.StageScoreThousandths,
            BaseScoreThousandths = entry.BaseScoreThousandths,
            RoundWins = entry.RoundWins,
            RoundPlaceCountsJson = JsonSerializer.Serialize(entry.RoundPlaceCounts),
            Medal = entry.StageRank switch
            {
                1 => (int)ColorCupMedal.Gold,
                2 => (int)ColorCupMedal.Silver,
                3 => (int)ColorCupMedal.Bronze,
                _ => (int)ColorCupMedal.None,
            },
            SportingColor = selection.SportingColor,
            SelectionRank = selection.SelectionRank,
        });
    }

    internal static void PersistChampionHonour(
        SaveDbContext context,
        SeasonEntity source,
        CupSimulation simulation)
    {
        StageRankedAthlete champion = simulation.Ranked.Single(r => r.StageRank == 1);
        context.Honours.Add(new HonourEntity
        {
            SeasonId = source.Id,
            SeasonNumber = source.SeasonNumber,
            LeagueId = CupLeagueId,
            LeagueName = CupLeagueName,
            LeagueKind = CupLeagueKind,
            SaveAthleteId = champion.AthleteId,
            Kind = (int)Features.Records.HonourKind.ColorCupIndividualChampion,
        });
    }

    internal static async Task<(int StageCount, int SeasonCount, int RoundCount, long Lifetime, long Effective, long Championship)> CapturePreservationAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths, cancellationToken).ConfigureAwait(false);
        long effective = await context.AthleteCareers.SumAsync(e => (long)e.CurrentEffectiveBonusThousandths, cancellationToken).ConfigureAwait(false);
        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths, cancellationToken).ConfigureAwait(false);
        return (stages, seasons, rounds, lifetime, effective, championship);
    }

    internal static async Task VerifyPreservationAsync(
        SaveDbContext context,
        int stageCountBefore,
        int seasonCountBefore,
        int roundCountBefore,
        long lifetimeBefore,
        long effectiveBefore,
        long championshipBefore,
        CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        if (stages != stageCountBefore)
        {
            throw new InvalidOperationException("Color Cup must not create league stage standings; championship totals are preserved.");
        }

        if (seasons != seasonCountBefore)
        {
            throw new InvalidOperationException("Color Cup must not create league season standings; championship totals are preserved.");
        }

        if (rounds != roundCountBefore)
        {
            throw new InvalidOperationException("Color Cup must not create league rounds; Cup history lives in Cup tables only.");
        }

        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths, cancellationToken).ConfigureAwait(false);
        if (lifetime != lifetimeBefore)
        {
            throw new InvalidOperationException("Color Cup participation must not change career bonus.");
        }

        long effective = await context.AthleteCareers.SumAsync(e => (long)e.CurrentEffectiveBonusThousandths, cancellationToken).ConfigureAwait(false);
        if (effective != effectiveBefore)
        {
            throw new InvalidOperationException("Color Cup participation must not change effective bonus projections.");
        }

        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths, cancellationToken).ConfigureAwait(false);
        if (championship != championshipBefore)
        {
            throw new InvalidOperationException("Color Cup must not award league championship points.");
        }
    }

    internal static async Task ValidatePersistedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CupSimulation simulation,
        RulesV1 rules,
        int stageCountBefore,
        int seasonCountBefore,
        int roundCountBefore,
        long lifetimeBefore,
        long effectiveBefore,
        long championshipBefore,
        CancellationToken cancellationToken)
    {
        List<ColorCupIndividualRoundEntity> rounds = await context.ColorCupIndividualRounds
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupIndividualStandingEntity> standings = await context.ColorCupIndividualStandings
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<HonourEntity> honours = await context.Honours
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ColorCupIndividualInvariants.ValidatePersisted(source, rounds, standings, honours, rules);
        if (!string.Equals(ComputeChecksum(simulation.Ranked), simulation.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Color Cup checksum does not match simulated standings.");
        }

        await VerifyPreservationAsync(
            context, stageCountBefore, seasonCountBefore, roundCountBefore,
            lifetimeBefore, effectiveBefore, championshipBefore, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task EmitCupStoriesAsync(
        SaveDbContext context,
        SeasonEntity source,
        CupSimulation simulation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(simulation);
        bool emitted = false;
        foreach (StageRankedAthlete entry in simulation.Ranked.Where(r => r.StageRank <= 3).OrderBy(r => r.StageRank))
        {
            string medal = entry.StageRank switch
            {
                1 => nameof(ColorCupMedal.Gold),
                2 => nameof(ColorCupMedal.Silver),
                3 => nameof(ColorCupMedal.Bronze),
                _ => nameof(ColorCupMedal.None),
            };
            emitted |= await Features.Stories.StoryEventEmitter.TryEmitAsync(
                context,
                entry.AthleteId,
                Features.Stories.StoryEventType.ColorCupMedal,
                $"color-cup-s{source.SeasonNumber}-{medal.ToLowerInvariant()}",
                source.SeasonNumber,
                null,
                new Features.Stories.StoryEventPayload(
                    entry.Name,
                    source.SeasonNumber,
                    LeagueName: CupLeagueName,
                    CupRank: entry.StageRank,
                    Medal: medal),
                cancellationToken).ConfigureAwait(false);
        }

        StageRankedAthlete champion = simulation.Ranked.Single(r => r.StageRank == 1);
        emitted |= await Features.Stories.StoryEventEmitter.TryEmitAsync(
            context,
            champion.AthleteId,
            Features.Stories.StoryEventType.ColorCupIndividualTitle,
            $"color-cup-s{source.SeasonNumber}-title",
            source.SeasonNumber,
            null,
            new Features.Stories.StoryEventPayload(
                champion.Name,
                source.SeasonNumber,
                LeagueName: CupLeagueName,
                CupRank: 1,
                Medal: nameof(ColorCupMedal.Gold)),
            cancellationToken).ConfigureAwait(false);
        if (emitted)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<RunColorCupIndividualResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity source,
        CupSimulation simulation,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<ColorCupIndividualStandingEntity> standings = await context.ColorCupIndividualStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.CupRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupIndividualRoundEntity> rounds = await context.ColorCupIndividualRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (standings.Count != simulation.Ranked.Count || rounds.Count != simulation.Payloads.Count)
        {
            throw new InvalidOperationException("Color Cup persisted result does not match the simulation.");
        }

        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupIndividualMember> members = new(standings.Count);
        foreach (ColorCupIndividualStandingEntity standing in standings)
        {
            names.TryGetValue(standing.SaveAthleteId, out string? name);
            string color = ((SimulationKernel.Catalog.SportingColor)standing.SportingColor).ToString();
            members.Add(new ColorCupIndividualMember(
                standing.SaveAthleteId,
                name ?? $"Athlete {standing.SaveAthleteId}",
                color,
                standing.SelectionRank,
                standing.CupRank,
                standing.CupScoreThousandths,
                standing.BaseScoreThousandths,
                standing.RoundWins,
                ((ColorCupMedal)standing.Medal).ToString()));
        }

        ColorCupIndividualRoundPayloadDocument first = ColorCupIndividualRoundPayloadDocument.FromJson(rounds[0].PayloadJson);
        ColorCupIndividualRoundPayloadDocument last = ColorCupIndividualRoundPayloadDocument.FromJson(rounds[^1].PayloadJson);
        ColorCupIndividualStandingEntity champion = standings.Single(s => s.CupRank == 1);
        names.TryGetValue(champion.SaveAthleteId, out string? championName);
        return new RunColorCupIndividualResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            standings.Count,
            rounds.Count,
            simulation.Checksum,
            first.RngBeforeState,
            first.RngBeforeStream,
            last.RngAfterState,
            last.RngAfterStream,
            champion.SaveAthleteId,
            championName ?? $"Athlete {champion.SaveAthleteId}",
            members);
    }
}
