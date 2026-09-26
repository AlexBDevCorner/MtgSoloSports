using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Simulation.GlobalStage;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;

namespace MtgSoloSports.Features.Simulation.AdvanceRound;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Simulates and persists the
/// next legal round for one league in its current stage: deterministic shuffle
/// with only the save RNG, base points by shuffled position, final points with
/// only the stage-start active bonus, one compact immutable round payload, and
/// the RNG-after state committed in the same transaction. Presentation DTOs are
/// built from the persisted payload so replay never resimulates.
/// Season 1 Stage 1 has no prior bonus, so the stage-start active bonus is zero
/// for every athlete; pending bonus earned in the current stage never affects
/// the current stage and its activation arrives with the stage slice.
/// </summary>
public sealed class AdvanceRoundHandler
{
    private readonly SaveStore _store;

    public AdvanceRoundHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<AdvanceRoundResponse> HandleAsync(Guid saveId, int leagueId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (leagueId <= 0)
        {
            throw new ArgumentException("League id must be positive.", nameof(leagueId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AdvanceUnderLockAsync(saveId, leagueId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<AdvanceRoundResponse> AdvanceUnderLockAsync(Guid saveId, int leagueId, CancellationToken cancellationToken)
    {
        // Existing saves created before MSS-008 have no Stages/Rounds tables.
        // Apply pending save-schema migrations before touching sporting state;
        // the per-save lock held by HandleAsync serializes concurrent upgrades.
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);

        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        SaveMetadataEntity metadata = await LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RulesV1 rules = await LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        Pcg32State rngBefore = rngRow.ToState();

        SeasonEntity season = await LoadSeasonAsync(context, metadata, cancellationToken).ConfigureAwait(false);
        LeagueEntity league = await LoadLeagueAsync(context, season, leagueId, cancellationToken).ConfigureAwait(false);
        List<MemberRow> roster = await LoadRosterAsync(context, season, league, rules, cancellationToken).ConfigureAwait(false);

        StageEntity stage = await LoadOrCreateStageAsync(context, season, league, rules, cancellationToken).ConfigureAwait(false);
        if (stage.IsComplete)
        {
            throw new AdvanceRoundConflictException(
                $"Stage {stage.StageNumber} for league '{league.Name}' is already complete; stage completion is handled by the stage slice.");
        }

        await EnsureGlobalStageLegalAsync(context, season, league, stage, rules, cancellationToken).ConfigureAwait(false);

        int roundNumber = checked(stage.CompletedRounds + 1);
        if (roundNumber > rules.RoundsPerStage)
        {
            throw new AdvanceRoundConflictException(
                $"Stage {stage.StageNumber} for league '{league.Name}' is already complete with {rules.RoundsPerStage} rounds; stage completion is handled by the stage slice.");
        }

        await EnsureRoundAbsentAsync(context, season, league, stage.StageNumber, roundNumber, cancellationToken).ConfigureAwait(false);

        Dictionary<int, Points> cumulativeBefore = await LoadCumulativeBeforeAsync(context, season, league, stage, roundNumber, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, Bonus> activeBonuses = await LoadStageStartActiveBonusesAsync(context, roster, season, stage, rules, cancellationToken).ConfigureAwait(false);

        RoundSimulationResult simulation = SimulateRound(roster, cumulativeBefore, activeBonuses, rngBefore, rules);
        RoundPayloadDocument payload = BuildPayload(season, league, stage, roundNumber, rules, rngBefore, simulation);
        RoundInvariants.ValidateSimulation(payload, rules, rngBefore.State, rngBefore.Stream);

        RoundEntity round = PersistRound(context, season, league, stage, roundNumber, rules, rngBefore, simulation, payload);
        stage.CompletedRounds = roundNumber;
        context.ApplyRngState(simulation.RngAfter);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await VerifyCommittedAsync(context, round.Id, payload, simulation, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return BuildResponse(saveId, season, league, stage, roundNumber, rules, rngBefore, simulation, payload);
    }

    internal sealed record MemberRow(int AthleteId, string Name, int SportingColor);

    internal static async Task<SaveMetadataEntity> LoadMetadataAsync(SaveDbContext context, Guid saveId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        SaveMetadataEntity? metadata = await context.SaveMetadata.SingleOrDefaultAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            throw new InvalidOperationException($"Save '{saveId:D}' is missing metadata.");
        }

        if (metadata.SaveId != saveId)
        {
            throw new InvalidOperationException($"Save '{saveId:D}' has mismatched identity.");
        }

        return metadata;
    }

    internal static async Task<RulesV1> LoadRulesAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RulesSnapshotEntity? row = await context.RulesSnapshots.SingleOrDefaultAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        if (row is null || string.IsNullOrWhiteSpace(row.RulesJson))
        {
            throw new InvalidOperationException("Save is missing its rules snapshot.");
        }

        return RulesSnapshotDocument.FromJson(row.RulesJson).ToRules();
    }

    internal static async Task<RngStateEntity> LoadRngAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RngStateEntity? row = await context.RngStates.SingleOrDefaultAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            throw new InvalidOperationException("Save is missing its RNG state.");
        }

        _ = row.ToState();
        return row;
    }

    internal static async Task<SeasonEntity> LoadSeasonAsync(SaveDbContext context, SaveMetadataEntity metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metadata);
        SeasonEntity? season = await context.Seasons.SingleOrDefaultAsync(e => e.SeasonNumber == metadata.CurrentSeason, cancellationToken).ConfigureAwait(false);
        if (season is null)
        {
            throw new InvalidOperationException($"Save has no season {metadata.CurrentSeason}.");
        }

        return season;
    }

    internal static async Task<LeagueEntity> LoadLeagueAsync(SaveDbContext context, SeasonEntity season, int leagueId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        LeagueEntity? league = await context.Leagues.SingleOrDefaultAsync(e => e.Id == leagueId, cancellationToken).ConfigureAwait(false);
        if (league is null || league.SeasonId != season.Id)
        {
            throw new AdvanceRoundConflictException($"League {leagueId} is not part of season {season.SeasonNumber}.");
        }

        return league;
    }

    internal static async Task<List<MemberRow>> LoadRosterAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(rules);

        List<SeasonMembershipEntity> memberships = await LoadLeagueMembershipsAsync(context, season, league, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SaveAthleteEntity> athletes = await LoadRosterAthletesAsync(context, memberships, league, rules, cancellationToken).ConfigureAwait(false);
        List<MemberRow> roster = BuildRosterRows(memberships, athletes);
        ValidateRosterRows(roster, league);
        roster.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return roster;
    }

    internal static async Task<List<SeasonMembershipEntity>> LoadLeagueMembershipsAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (memberships.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException($"League '{league.Name}' must contain exactly {rules.LeagueSize} athletes, was {memberships.Count}.");
        }

        HashSet<int> athleteIds = new();
        foreach (SeasonMembershipEntity membership in memberships)
        {
            if (!athleteIds.Add(membership.SaveAthleteId))
            {
                throw new InvalidOperationException($"League '{league.Name}' contains duplicate athlete id {membership.SaveAthleteId}.");
            }
        }

        return memberships;
    }

    internal static async Task<Dictionary<int, SaveAthleteEntity>> LoadRosterAthletesAsync(
        SaveDbContext context,
        List<SeasonMembershipEntity> memberships,
        LeagueEntity league,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<int> ids = memberships.Select(m => m.SaveAthleteId).ToList();
        Dictionary<int, SaveAthleteEntity> athletes = await context.SaveAthletes
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);
        if (athletes.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException($"League '{league.Name}' references {rules.LeagueSize - athletes.Count} unknown athletes.");
        }

        return athletes;
    }

    internal static List<MemberRow> BuildRosterRows(
        List<SeasonMembershipEntity> memberships,
        Dictionary<int, SaveAthleteEntity> athletes)
    {
        List<MemberRow> roster = new(memberships.Count);
        foreach (SeasonMembershipEntity membership in memberships)
        {
            SaveAthleteEntity athlete = athletes[membership.SaveAthleteId];
            roster.Add(new MemberRow(athlete.Id, athlete.Name, athlete.SportingColor));
        }

        return roster;
    }

    internal static void ValidateRosterRows(List<MemberRow> roster, LeagueEntity league)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (MemberRow row in roster)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
            {
                throw new InvalidOperationException($"League '{league.Name}' contains an athlete with an empty name.");
            }

            if (!names.Add(row.Name))
            {
                throw new InvalidOperationException($"League '{league.Name}' contains duplicate athlete '{row.Name}'.");
            }
        }

        if (league.Kind == (int)LeagueKind.Feeder)
        {
            foreach (MemberRow row in roster)
            {
                if (row.SportingColor != league.SportingColor)
                {
                    throw new InvalidOperationException($"League '{league.Name}' contains athlete '{row.Name}' of a different sporting color.");
                }
            }
        }
    }

    internal static async Task<StageEntity> LoadOrCreateStageAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(rules);

        List<StageEntity> stages = await context.Stages
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.StageNumber)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (stages.Count == 0)
        {
            StageEntity created = new()
            {
                SeasonId = season.Id,
                LeagueId = league.Id,
                StageNumber = 1,
                CompletedRounds = 0,
            };
            context.Stages.Add(created);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return created;
        }

        foreach (StageEntity stage in stages)
        {
            if (stage.StageNumber < 1 || stage.StageNumber > rules.StagesPerSeason)
            {
                throw new InvalidOperationException($"League '{league.Name}' has corrupt stage {stage.StageNumber}.");
            }

            if (stage.CompletedRounds < 0 || stage.CompletedRounds > rules.RoundsPerStage)
            {
                throw new InvalidOperationException($"League '{league.Name}' stage {stage.StageNumber} has corrupt completed-round count {stage.CompletedRounds}.");
            }
        }

        StageEntity current = stages.OrderByDescending(s => s.StageNumber).First();
        if (current.CompletedRounds >= rules.RoundsPerStage && current.StageNumber >= rules.StagesPerSeason)
        {
            throw new AdvanceRoundConflictException($"League '{league.Name}' has completed all {rules.StagesPerSeason} stages; season completion is handled by a later slice.");
        }

        if (current.CompletedRounds >= rules.RoundsPerStage)
        {
            throw new AdvanceRoundConflictException(
                $"Stage {current.StageNumber} for league '{league.Name}' is already complete; stage completion is handled by the stage slice.");
        }

        int distinct = stages.Select(s => s.StageNumber).Distinct().Count();
        if (distinct != stages.Count)
        {
            throw new InvalidOperationException($"League '{league.Name}' has duplicate stage rows.");
        }

        return current;
    }

    /// <summary>
    /// Enforces synchronous global progression: a league may only advance
    /// rounds for the current global stage. Stage N+1 cannot begin until
    /// Stage N is complete for every active league.
    /// </summary>
    internal static async Task EnsureGlobalStageLegalAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        StageEntity stage,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(rules);

        GlobalStageGate.GlobalStageView global = await GlobalStageGate
            .LoadGlobalStageAsync(context, season, rules, cancellationToken)
            .ConfigureAwait(false);
        if (!GlobalStageGate.IsStageLegal(stage.StageNumber, global))
        {
            throw new AdvanceRoundConflictException(
                GlobalStageGate.BuildBlockedMessage(league.Name, stage.StageNumber, global));
        }
    }

    internal static async Task EnsureRoundAbsentAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        int stageNumber,
        int roundNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool exists = await context.Rounds.AnyAsync(
            e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.StageNumber == stageNumber && e.RoundNumber == roundNumber,
            cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            throw new AdvanceRoundConflictException(
                $"Round {roundNumber} of stage {stageNumber} for league '{league.Name}' has already been simulated.");
        }

        bool futureExists = await context.Rounds.AnyAsync(
            e => e.SeasonId == season.Id && e.LeagueId == league.Id && (e.StageNumber > stageNumber || (e.StageNumber == stageNumber && e.RoundNumber > roundNumber)),
            cancellationToken).ConfigureAwait(false);
        if (futureExists)
        {
            throw new AdvanceRoundConflictException(
                $"League '{league.Name}' already has a later round persisted; rounds must advance in order.");
        }
    }

    internal static async Task<Dictionary<int, Points>> LoadCumulativeBeforeAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        StageEntity stage,
        int roundNumber,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Dictionary<int, Points> before = new(rules.LeagueSize);
        if (roundNumber == 1)
        {
            return before;
        }

        RoundEntity? previous = await context.Rounds.SingleOrDefaultAsync(
            e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.StageNumber == stage.StageNumber && e.RoundNumber == roundNumber - 1,
            cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            throw new AdvanceRoundConflictException(
                $"Round {roundNumber - 1} of stage {stage.StageNumber} for league '{league.Name}' is missing; rounds must advance in order.");
        }

        RoundPayloadDocument document = RoundPayloadDocument.FromJson(previous.PayloadJson);
        if (document.StageNumber != stage.StageNumber || document.RoundNumber != roundNumber - 1)
        {
            throw new InvalidOperationException($"Persisted round {previous.Id} has corrupt stage/round identity.");
        }

        foreach (RoundPayloadEntry entry in document.Placements)
        {
            before[entry.AthleteId] = Points.FromThousandths(entry.CumulativeAfterThousandths);
        }

        return before;
    }

    internal static async Task<Dictionary<int, Bonus>> LoadStageStartActiveBonusesAsync(
        SaveDbContext context,
        List<MemberRow> roster,
        SeasonEntity season,
        StageEntity stage,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(rules);

        Dictionary<int, int> seasonNumbers = await LoadSeasonNumbersAsync(context, season, cancellationToken).ConfigureAwait(false);
        int currentSeasonNumber = seasonNumbers[season.Id];
        List<StageStandingEntity> standings = await LoadBonusStandingsAsync(context, roster, cancellationToken).ConfigureAwait(false);
        Dictionary<int, List<BonusContribution>> contributions = GroupBonusContributions(roster, standings, seasonNumbers, currentSeasonNumber);
        return ComputeStageStartBonuses(roster, contributions, currentSeasonNumber, stage.StageNumber, rules);
    }

    internal static async Task<Dictionary<int, int>> LoadSeasonNumbersAsync(
        SaveDbContext context,
        SeasonEntity season,
        CancellationToken cancellationToken)
    {
        Dictionary<int, int> seasonNumbers = await context.Seasons
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber, cancellationToken)
            .ConfigureAwait(false);
        if (!seasonNumbers.TryGetValue(season.Id, out int current))
        {
            throw new InvalidOperationException($"Save has no season row for season {season.SeasonNumber}.");
        }

        return seasonNumbers;
    }

    internal static async Task<List<StageStandingEntity>> LoadBonusStandingsAsync(
        SaveDbContext context,
        List<MemberRow> roster,
        CancellationToken cancellationToken)
    {
        HashSet<int> rosterIds = new(roster.Select(r => r.AthleteId));
        return await context.StageStandings
            .AsNoTracking()
            .Where(e => rosterIds.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static Dictionary<int, List<BonusContribution>> GroupBonusContributions(
        List<MemberRow> roster,
        List<StageStandingEntity> standings,
        Dictionary<int, int> seasonNumbers,
        int currentSeasonNumber)
    {
        HashSet<int> rosterIds = new(roster.Select(r => r.AthleteId));
        Dictionary<int, List<BonusContribution>> contributionsByAthlete = new(roster.Count);
        foreach (MemberRow row in roster)
        {
            contributionsByAthlete[row.AthleteId] = [];
        }

        foreach (StageStandingEntity standing in standings)
        {
            if (!rosterIds.Contains(standing.SaveAthleteId))
            {
                continue;
            }

            if (!seasonNumbers.TryGetValue(standing.SeasonId, out int earnedSeason))
            {
                throw new InvalidOperationException(
                    $"Stage standing {standing.Id} references unknown season {standing.SeasonId}.");
            }

            // Only the most recent five seasons contribute; older bonus decays to zero.
            // Current-stage pending bonus is excluded by EffectiveBonus (earned stage >= current stage).
            if (earnedSeason > currentSeasonNumber || currentSeasonNumber - earnedSeason > 5)
            {
                continue;
            }

            contributionsByAthlete[standing.SaveAthleteId].Add(new BonusContribution(
                earnedSeason,
                standing.StageNumber,
                Bonus.FromThousandths(standing.EarnedBonusThousandths)));
        }

        return contributionsByAthlete;
    }

    internal static Dictionary<int, Bonus> ComputeStageStartBonuses(
        List<MemberRow> roster,
        Dictionary<int, List<BonusContribution>> contributions,
        int currentSeasonNumber,
        int currentStageNumber,
        RulesV1 rules)
    {
        Dictionary<int, Bonus> bonuses = new(roster.Count);
        foreach (MemberRow row in roster)
        {
            bonuses[row.AthleteId] = BonusCalculator.EffectiveBonus(
                contributions[row.AthleteId],
                currentSeasonNumber,
                currentStageNumber,
                rules);
        }

        return bonuses;
    }

    internal static RoundSimulationResult SimulateRound(
        List<MemberRow> roster,
        Dictionary<int, Points> cumulativeBefore,
        Dictionary<int, Bonus> activeBonuses,
        Pcg32State rngBefore,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(cumulativeBefore);
        ArgumentNullException.ThrowIfNull(activeBonuses);
        ArgumentNullException.ThrowIfNull(rules);

        List<RoundAthleteInput> inputs = new(roster.Count);
        foreach (MemberRow row in roster)
        {
            Points before = cumulativeBefore.TryGetValue(row.AthleteId, out Points value) ? value : Points.Zero;
            if (!activeBonuses.TryGetValue(row.AthleteId, out Bonus bonus))
            {
                throw new InvalidOperationException($"Missing stage-start bonus for athlete '{row.Name}'.");
            }

            inputs.Add(new RoundAthleteInput(row.AthleteId, row.Name, bonus, before));
        }

        Pcg32V1 rng = Pcg32V1.Restore(rngBefore);
        return RoundSimulator.Simulate(inputs, rng, rules);
    }

    internal static RoundPayloadDocument BuildPayload(
        SeasonEntity season,
        LeagueEntity league,
        StageEntity stage,
        int roundNumber,
        RulesV1 rules,
        Pcg32State rngBefore,
        RoundSimulationResult simulation)
    {
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(simulation);

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

        return new RoundPayloadDocument(
            RoundPayloadDocument.PayloadVersion,
            rules.Version,
            season.SeasonNumber,
            league.Id,
            stage.StageNumber,
            roundNumber,
            rngBefore.State,
            rngBefore.Stream,
            simulation.RngAfter.State,
            simulation.RngAfter.Stream,
            simulation.Checksum,
            entries);
    }

    internal static RoundEntity PersistRound(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        StageEntity stage,
        int roundNumber,
        RulesV1 rules,
        Pcg32State rngBefore,
        RoundSimulationResult simulation,
        RoundPayloadDocument payload)
    {
        ArgumentNullException.ThrowIfNull(context);
        RoundEntity round = new()
        {
            SeasonId = season.Id,
            LeagueId = league.Id,
            StageId = stage.Id,
            StageNumber = stage.StageNumber,
            RoundNumber = roundNumber,
            RulesVersion = rules.Version,
            RngBeforeState = unchecked((long)rngBefore.State),
            RngBeforeStream = unchecked((long)rngBefore.Stream),
            RngAfterState = unchecked((long)simulation.RngAfter.State),
            RngAfterStream = unchecked((long)simulation.RngAfter.Stream),
            PayloadJson = payload.ToJson(),
            PayloadChecksum = payload.Checksum,
        };
        context.Rounds.Add(round);
        return round;
    }

    internal static async Task VerifyCommittedAsync(
        SaveDbContext context,
        int roundId,
        RoundPayloadDocument payload,
        RoundSimulationResult simulation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        RoundEntity? stored = await context.Rounds.SingleOrDefaultAsync(e => e.Id == roundId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            throw new InvalidOperationException("Round row was not staged for commit.");
        }

        if (!string.Equals(stored.PayloadChecksum, payload.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Staged round checksum does not match the simulated result.");
        }

        RoundPayloadDocument reparsed = RoundPayloadDocument.FromJson(stored.PayloadJson);
        if (!string.Equals(reparsed.Checksum, simulation.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Staged round payload does not match the simulated result.");
        }
    }

    internal static AdvanceRoundResponse BuildResponse(
        Guid saveId,
        SeasonEntity season,
        LeagueEntity league,
        StageEntity stage,
        int roundNumber,
        RulesV1 rules,
        Pcg32State rngBefore,
        RoundSimulationResult simulation,
        RoundPayloadDocument payload)
    {
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(payload);

        List<AdvanceRoundPlacement> placements = new(payload.Placements.Count);
        foreach (RoundPayloadEntry entry in payload.Placements)
        {
            placements.Add(new AdvanceRoundPlacement(
                entry.AthleteId,
                entry.Name,
                entry.Position,
                entry.BaseThousandths,
                entry.ActiveBonusThousandths,
                entry.FinalThousandths,
                entry.CumulativeBeforeThousandths,
                entry.CumulativeAfterThousandths,
                entry.RankBefore,
                entry.RankAfter,
                entry.RankMovement));
        }

        return new AdvanceRoundResponse(
            saveId,
            season.SeasonNumber,
            league.Id,
            league.Name,
            stage.StageNumber,
            roundNumber,
            rules.Version,
            payload.Checksum,
            rngBefore.State,
            rngBefore.Stream,
            simulation.RngAfter.State,
            simulation.RngAfter.Stream,
            placements);
    }

    /// <summary>
    /// Reads one persisted round without resimulation. Used by tests and future
    /// animated replay to prove presentation consumes immutable facts.
    /// </summary>
    internal static async Task<AdvanceRoundResponse> LoadPersistedAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity season,
        LeagueEntity league,
        int stageNumber,
        int roundNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RoundEntity? round = await context.Rounds.AsNoTracking().SingleOrDefaultAsync(
            e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.StageNumber == stageNumber && e.RoundNumber == roundNumber,
            cancellationToken).ConfigureAwait(false);
        if (round is null)
        {
            throw new AdvanceRoundConflictException($"Round {roundNumber} of stage {stageNumber} for league '{league.Name}' has not been simulated.");
        }

        RoundPayloadDocument payload = RoundPayloadDocument.FromJson(round.PayloadJson);
        if (!string.Equals(payload.Checksum, round.PayloadChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Persisted round {roundNumber} payload checksum mismatch.");
        }

        List<AdvanceRoundPlacement> placements = new(payload.Placements.Count);
        foreach (RoundPayloadEntry entry in payload.Placements)
        {
            placements.Add(new AdvanceRoundPlacement(
                entry.AthleteId,
                entry.Name,
                entry.Position,
                entry.BaseThousandths,
                entry.ActiveBonusThousandths,
                entry.FinalThousandths,
                entry.CumulativeBeforeThousandths,
                entry.CumulativeAfterThousandths,
                entry.RankBefore,
                entry.RankAfter,
                entry.RankMovement));
        }

        return new AdvanceRoundResponse(
            saveId,
            payload.SeasonNumber,
            payload.LeagueId,
            league.Name,
            payload.StageNumber,
            payload.RoundNumber,
            payload.RulesVersion,
            payload.Checksum,
            payload.RngBeforeState,
            payload.RngBeforeStream,
            payload.RngAfterState,
            payload.RngAfterStream,
            placements);
    }
}
