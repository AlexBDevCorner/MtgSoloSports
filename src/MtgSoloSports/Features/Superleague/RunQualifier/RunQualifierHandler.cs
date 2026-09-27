using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Superleague.RunQualifier;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Runs the 32-athlete
/// Superleague qualifier for a completed Superleague season after automatic
/// movement: 8 incumbents (Superleague ranks 17-24) plus 24 challengers
/// (feeder ranks 2-4 across all eight leagues) compete in one standard
/// 16-round stage. Active career bonus at the next-season Stage 1 boundary
/// applies with normal fixed-point scoring/ranking; incumbents and challengers
/// are treated identically (role is provenance only). No new round or stage
/// career bonus is generated and no league <c>StageStanding</c>,
/// <c>SeasonStanding</c> or <c>Round</c> rows are created, so normal league
/// season championship totals are untouched. Persists 16 immutable qualifier
/// round payloads plus 32 qualifier standings (exactly 8 qualified), applies
/// the eight winners to the next-season roster (winners to the next
/// Superleague, the 24 losers to their returning-color feeders, so the final
/// next Superleague is 16 safe + 8 promoted + 8 qualifier winners) and the
/// RNG-after state in one transaction. Holds one per-save lock; read-only
/// qualifier queries never lock. No Superleague color quota.
/// </summary>
public sealed class RunQualifierHandler
{
    private readonly SaveStore _store;

    public RunQualifierHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<RunQualifierResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<RunQualifierResponse> RunUnderLockAsync(
        Guid saveId,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        Pcg32State rngBefore = rngRow.ToState();

        (SeasonEntity source, SeasonEntity next) = await LoadPendingTransitionAsync(context, cancellationToken).ConfigureAwait(false);
        await EnsureAutomaticMovementResolvedAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        await EnsureQualifierAbsentAsync(context, source, next, cancellationToken).ConfigureAwait(false);

        List<LeagueEntity> sourceFeeders = await LoadSourceFeedersAsync(context, source, rules, cancellationToken).ConfigureAwait(false);
        LeagueEntity sourceSuperleague = await LoadSourceSuperleagueAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<SeasonStandingEntity> superStandings = await LoadLeagueStandingsAsync(context, source, sourceSuperleague, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandings = await LoadFeederStandingsAsync(context, source, sourceFeeders, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> sourceMemberships = await LoadSourceMembershipsAsync(context, source, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete = sourceMemberships.ToDictionary(m => m.SaveAthleteId);
        Dictionary<int, string> namesByAthlete = await LoadAthleteNamesAsync(context, cancellationToken).ConfigureAwait(false);

        QualifierFieldSelection.QualifierField field = QualifierFieldSelection.Select(
            superStandings, feederStandings, sourceFeeders, sourceSuperleague, membershipByAthlete, namesByAthlete, rules);
        QualifierInvariants.ValidateField(field, superStandings, feederStandings, sourceFeeders, sourceSuperleague, rules);

        (int stageCountBefore, int seasonCountBefore, int roundCountBefore, int qualifierRoundsBefore, int qualifierStandingsBefore) =
            await CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);
        if (qualifierRoundsBefore != 0 || qualifierStandingsBefore != 0)
        {
            throw new InvalidOperationException("Qualifier preservation snapshot is corrupt.");
        }

        List<AdvanceRoundHandler.MemberRow> roster = BuildRoster(field);
        Dictionary<int, Bonus> activeBonuses = await LoadQualifierActiveBonusesAsync(
            context, roster, next, rules, cancellationToken).ConfigureAwait(false);

        QualifierSimulation simulation = SimulateQualifier(field, roster, activeBonuses, rngBefore, source, next, rules);

        await PersistQualifierAsync(context, source, next, simulation, rules, cancellationToken).ConfigureAwait(false);
        await ApplyQualifierToNextRosterAsync(context, next, simulation, rules, cancellationToken).ConfigureAwait(false);
        context.ApplyRngState(simulation.RngAfter);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await ValidatePersistedAsync(context, source, next, simulation, rules, stageCountBefore, seasonCountBefore, roundCountBefore, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(_store, saveId, source, next, simulation, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record QualifierSimulation(
        QualifierFieldSelection.QualifierField Field,
        List<QualifierRoundPayloadDocument> Payloads,
        IReadOnlyList<StageAthleteTotals> Totals,
        IReadOnlyList<StageRankedAthlete> Ranked,
        Pcg32State RngAfter,
        string Checksum);

    internal static List<AdvanceRoundHandler.MemberRow> BuildRoster(QualifierFieldSelection.QualifierField field)
    {
        List<AdvanceRoundHandler.MemberRow> roster = new(field.All.Count);
        foreach (QualifierFieldSelection.QualifierPick pick in field.All)
        {
            roster.Add(new AdvanceRoundHandler.MemberRow(pick.SaveAthleteId, pick.Name, pick.SportingColor));
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (AdvanceRoundHandler.MemberRow row in roster)
        {
            if (!names.Add(row.Name))
            {
                throw new InvalidOperationException($"Qualifier field contains duplicate athlete '{row.Name}'.");
            }
        }

        roster.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return roster;
    }

    internal static async Task<Dictionary<int, Bonus>> LoadQualifierActiveBonusesAsync(
        SaveDbContext context,
        List<AdvanceRoundHandler.MemberRow> roster,
        SeasonEntity next,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        Dictionary<int, int> seasonNumbers = await AdvanceRoundHandler.LoadSeasonNumbersAsync(context, next, cancellationToken).ConfigureAwait(false);
        if (!seasonNumbers.TryGetValue(next.Id, out int nextSeasonNumber))
        {
            throw new InvalidOperationException($"Save has no season row for season {next.SeasonNumber}.");
        }

        List<StageStandingEntity> standings = await AdvanceRoundHandler.LoadBonusStandingsAsync(context, roster, cancellationToken).ConfigureAwait(false);
        Dictionary<int, List<BonusContribution>> contributions = AdvanceRoundHandler.GroupBonusContributions(
            roster, standings, seasonNumbers, nextSeasonNumber);

        // Identical treatment: role never branches bonus math. Every qualifier
        // athlete uses the next-season Stage 1 boundary, so source-season
        // Stage 32 bonus enters at 80% decay like any next-season opener.
        return AdvanceRoundHandler.ComputeStageStartBonuses(roster, contributions, nextSeasonNumber, 1, rules);
    }

    internal static QualifierSimulation SimulateQualifier(
        QualifierFieldSelection.QualifierField field,
        List<AdvanceRoundHandler.MemberRow> roster,
        Dictionary<int, Bonus> activeBonuses,
        Pcg32State rngBefore,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules)
    {
        Dictionary<int, Points> cumulative = new(roster.Count);
        List<QualifierRoundPayloadDocument> payloads = new(rules.QualifierRounds);
        Pcg32State current = rngBefore;

        for (int roundNumber = 1; roundNumber <= rules.QualifierRounds; roundNumber++)
        {
            RoundSimulationResult simulation = AdvanceRoundHandler.SimulateRound(roster, cumulative, activeBonuses, current, rules);
            QualifierRoundPayloadDocument payload = new(
                QualifierRoundPayloadDocument.PayloadVersion,
                rules.Version,
                source.SeasonNumber,
                next.SeasonNumber,
                roundNumber,
                current.State,
                current.Stream,
                simulation.RngAfter.State,
                simulation.RngAfter.Stream,
                simulation.Checksum,
                ToPayloadEntries(simulation));
            QualifierInvariants.ValidateRound(payload, rules, current.State, current.Stream);
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
        QualifierInvariants.ValidateCompletedQualifier(ranked, totals, rules);
        Pcg32State rngAfter = tieBreakRng.Snapshot();
        string checksum = ComputeChecksum(ranked);
        return new QualifierSimulation(field, payloads, totals, ranked, rngAfter, checksum);
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

    internal static List<List<StageRoundEntry>> BuildRoundEntries(List<QualifierRoundPayloadDocument> payloads)
    {
        List<List<StageRoundEntry>> roundEntries = new(payloads.Count);
        foreach (QualifierRoundPayloadDocument payload in payloads)
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
    /// Fingerprints qualifier standings: lowercase hex SHA-256 over lines of
    /// <c>Rank:AthleteId:Name:Score:Base</c> in rank order.
    /// SHA-256 is a content fingerprint here, not sporting randomness.
    /// Championship/earned values are intentionally excluded: the qualifier
    /// persists no championship points and no new bonus.
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

    internal static async Task PersistQualifierAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        QualifierSimulation simulation,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        foreach (QualifierRoundPayloadDocument payload in simulation.Payloads)
        {
            context.QualifierRounds.Add(new QualifierRoundEntity
            {
                FromSeasonId = source.Id,
                ToSeasonId = next.Id,
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

        Dictionary<int, QualifierFieldSelection.QualifierPick> picksByAthlete =
            simulation.Field.All.ToDictionary(p => p.SaveAthleteId);
        foreach (StageRankedAthlete entry in simulation.Ranked)
        {
            if (!picksByAthlete.TryGetValue(entry.AthleteId, out QualifierFieldSelection.QualifierPick? pick))
            {
                throw new InvalidOperationException($"Qualifier standing for '{entry.Name}' has no field provenance.");
            }

            context.QualifierStandings.Add(new QualifierStandingEntity
            {
                FromSeasonId = source.Id,
                ToSeasonId = next.Id,
                SaveAthleteId = entry.AthleteId,
                QualifierRank = entry.StageRank,
                QualifierScoreThousandths = entry.StageScoreThousandths,
                BaseScoreThousandths = entry.BaseScoreThousandths,
                RoundWins = entry.RoundWins,
                RoundPlaceCountsJson = JsonSerializer.Serialize(entry.RoundPlaceCounts),
                IsQualified = entry.StageRank <= rules.QualifierWinners,
                Role = (int)pick.Role,
                FromLeagueId = pick.FromLeagueId,
                FromSeasonRank = pick.FromSeasonRank,
                SportingColor = pick.SportingColor,
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the eight qualifier winners to the next-season roster inside the
    /// caller's transaction (no save here). Winners move to (or remain in) the
    /// next Superleague; the 24 losers move to (or remain in) their
    /// returning-color feeder. Automatic-movement <c>Movement</c> rows are
    /// provisional markers and are never rewritten; the final occupancy is
    /// determined by these membership rows plus the qualifier standings.
    /// Sporting color and pool membership are preserved; only the 32 qualifier
    /// participants can change leagues.
    /// </summary>
    internal static async Task ApplyQualifierToNextRosterAsync(
        SaveDbContext context,
        SeasonEntity next,
        QualifierSimulation simulation,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(rules);

        (LeagueEntity superleague, Dictionary<int, int> feederByColor) =
            await LoadNextLeagueTargetsAsync(context, next, rules, cancellationToken).ConfigureAwait(false);
        HashSet<int> fieldIds = ResolveFieldIds(simulation, rules);
        HashSet<int> qualifiedIds = ResolveQualifiedIds(simulation, fieldIds, rules);
        List<SeasonMembershipEntity> nextMemberships = await LoadNextQualifierMembershipsAsync(
            context, next, fieldIds, rules, cancellationToken).ConfigureAwait(false);
        AssignQualifierLeagues(nextMemberships, qualifiedIds, superleague.Id, feederByColor);
    }

    internal static async Task<(LeagueEntity Superleague, Dictionary<int, int> FeederByColor)> LoadNextLeagueTargetsAsync(
        SaveDbContext context,
        SeasonEntity next,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        LeagueEntity superleague = await context.Leagues.AsNoTracking().SingleOrDefaultAsync(
            e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Season {next.SeasonNumber} has no next Superleague.");
        List<LeagueEntity> feeders = await context.Leagues.AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (feeders.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season {next.SeasonNumber} must have exactly {rules.RegularLeagueCount} feeder leagues, was {feeders.Count}.");
        }

        return (superleague, feeders.ToDictionary(l => l.SportingColor, l => l.Id));
    }

    internal static HashSet<int> ResolveFieldIds(QualifierSimulation simulation, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(rules);
        HashSet<int> fieldIds = simulation.Field.All.Select(p => p.SaveAthleteId).ToHashSet();
        if (fieldIds.Count != rules.QualifierSize)
        {
            throw new InvalidOperationException(
                $"Qualifier field must hold exactly {rules.QualifierSize} athletes, was {fieldIds.Count}.");
        }

        return fieldIds;
    }

    internal static HashSet<int> ResolveQualifiedIds(
        QualifierSimulation simulation,
        HashSet<int> fieldIds,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(fieldIds);
        ArgumentNullException.ThrowIfNull(rules);
        HashSet<int> qualifiedIds = simulation.Ranked
            .Where(r => r.StageRank <= rules.QualifierWinners)
            .Select(r => r.AthleteId)
            .ToHashSet();
        if (qualifiedIds.Count != rules.QualifierWinners)
        {
            throw new InvalidOperationException(
                $"Qualifier must hold exactly {rules.QualifierWinners} successful qualifiers, was {qualifiedIds.Count}.");
        }

        foreach (int athleteId in qualifiedIds)
        {
            if (!fieldIds.Contains(athleteId))
            {
                throw new InvalidOperationException($"Qualifier winner {athleteId} is outside the qualifier field.");
            }
        }

        return qualifiedIds;
    }

    internal static async Task<List<SeasonMembershipEntity>> LoadNextQualifierMembershipsAsync(
        SaveDbContext context,
        SeasonEntity next,
        HashSet<int> fieldIds,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => e.SeasonId == next.Id && fieldIds.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count != rules.QualifierSize)
        {
            throw new InvalidOperationException(
                $"Next season must hold exactly {rules.QualifierSize} qualifier memberships, was {memberships.Count}.");
        }

        return memberships;
    }

    internal static void AssignQualifierLeagues(
        List<SeasonMembershipEntity> memberships,
        HashSet<int> qualifiedIds,
        int superleagueId,
        Dictionary<int, int> feederByColor)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(qualifiedIds);
        ArgumentNullException.ThrowIfNull(feederByColor);
        foreach (SeasonMembershipEntity membership in memberships)
        {
            AssignSingleQualifierLeague(membership, qualifiedIds, superleagueId, feederByColor);
        }
    }

    internal static void AssignSingleQualifierLeague(
        SeasonMembershipEntity membership,
        HashSet<int> qualifiedIds,
        int superleagueId,
        Dictionary<int, int> feederByColor)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(qualifiedIds);
        ArgumentNullException.ThrowIfNull(feederByColor);
        if (membership.LeagueId is null)
        {
            throw new InvalidOperationException(
                $"Qualifier athlete {membership.SaveAthleteId} has no provisional next-season league.");
        }

        if (qualifiedIds.Contains(membership.SaveAthleteId))
        {
            membership.LeagueId = superleagueId;
            return;
        }

        if (!feederByColor.TryGetValue(membership.SportingColor, out int feederId))
        {
            throw new InvalidOperationException($"Unknown sporting color {membership.SportingColor}.");
        }

        membership.LeagueId = feederId;
    }

    internal static async Task ValidatePersistedAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        QualifierSimulation simulation,
        RulesV1 rules,
        int stageCountBefore,
        int seasonCountBefore,
        int roundCountBefore,
        CancellationToken cancellationToken)
    {
        List<QualifierRoundEntity> rounds = await context.QualifierRounds
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<QualifierStandingEntity> standings = await context.QualifierStandings
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        QualifierInvariants.ValidatePersisted(source, next, rounds, standings, rules);
        await ValidateNextRosterAsync(context, next, simulation, standings, rules, cancellationToken).ConfigureAwait(false);
        await VerifyPreservationAsync(context, stageCountBefore, seasonCountBefore, roundCountBefore, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates that the persisted next-season roster reflects the qualifier
    /// result: the next Superleague holds exactly 32 athletes, every qualified
    /// athlete occupies the next Superleague and every failed qualifier
    /// occupies its returning-color feeder. Pool athletes and non-participants
    /// are untouched here; feeder rebalancing (MSS-017) restores 32 per feeder.
    /// </summary>
    internal static async Task ValidateNextRosterAsync(
        SaveDbContext context,
        SeasonEntity next,
        QualifierSimulation simulation,
        List<QualifierStandingEntity> standings,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(rules);

        (LeagueEntity superleague, Dictionary<int, int> feederByColor) =
            await LoadNextLeagueTargetsAsync(context, next, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, QualifierStandingEntity> standingByAthlete = ResolveStandingMap(standings, rules);
        HashSet<int> fieldIds = ResolveFieldIds(simulation, rules);
        if (!fieldIds.SetEquals(standingByAthlete.Keys))
        {
            throw new InvalidOperationException("Qualifier standings do not match the qualifier field.");
        }

        List<SeasonMembershipEntity> qualifierMemberships = await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == next.Id && fieldIds.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (qualifierMemberships.Count != rules.QualifierSize)
        {
            throw new InvalidOperationException(
                $"Next season must hold exactly {rules.QualifierSize} qualifier memberships, was {qualifierMemberships.Count}.");
        }

        CheckQualifierPlacements(qualifierMemberships, standingByAthlete, superleague.Id, feederByColor);
        await CheckFinalSuperleagueCountAsync(context, next, superleague, rules, cancellationToken).ConfigureAwait(false);
    }

    internal static Dictionary<int, QualifierStandingEntity> ResolveStandingMap(
        List<QualifierStandingEntity> standings,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<int, QualifierStandingEntity> map = standings.ToDictionary(s => s.SaveAthleteId);
        if (map.Count != rules.QualifierSize)
        {
            throw new InvalidOperationException(
                $"Qualifier must hold exactly {rules.QualifierSize} standings, was {map.Count}.");
        }

        return map;
    }

    internal static void CheckQualifierPlacements(
        List<SeasonMembershipEntity> memberships,
        Dictionary<int, QualifierStandingEntity> standingByAthlete,
        int superleagueId,
        Dictionary<int, int> feederByColor)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(standingByAthlete);
        ArgumentNullException.ThrowIfNull(feederByColor);
        foreach (SeasonMembershipEntity membership in memberships)
        {
            CheckSingleQualifierPlacement(membership, standingByAthlete, superleagueId, feederByColor);
        }
    }

    internal static void CheckSingleQualifierPlacement(
        SeasonMembershipEntity membership,
        Dictionary<int, QualifierStandingEntity> standingByAthlete,
        int superleagueId,
        Dictionary<int, int> feederByColor)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(standingByAthlete);
        ArgumentNullException.ThrowIfNull(feederByColor);
        if (!standingByAthlete.TryGetValue(membership.SaveAthleteId, out QualifierStandingEntity? standing))
        {
            throw new InvalidOperationException($"Qualifier athlete {membership.SaveAthleteId} has no standing.");
        }

        if (membership.LeagueId is null)
        {
            throw new InvalidOperationException($"Qualifier athlete {membership.SaveAthleteId} is in the common pool.");
        }

        if (standing.IsQualified)
        {
            if (membership.LeagueId != superleagueId)
            {
                throw new InvalidOperationException(
                    $"Qualified athlete {membership.SaveAthleteId} must occupy the next Superleague.");
            }

            return;
        }

        if (!feederByColor.TryGetValue(membership.SportingColor, out int expectedFeeder))
        {
            throw new InvalidOperationException($"Unknown sporting color {membership.SportingColor}.");
        }

        if (membership.LeagueId != expectedFeeder)
        {
            throw new InvalidOperationException(
                $"Failed qualifier {membership.SaveAthleteId} must occupy its returning-color feeder.");
        }
    }

    internal static async Task CheckFinalSuperleagueCountAsync(
        SaveDbContext context,
        SeasonEntity next,
        LeagueEntity superleague,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        int superCount = await context.SeasonMemberships.CountAsync(
            e => e.SeasonId == next.Id && e.LeagueId == superleague.Id, cancellationToken).ConfigureAwait(false);
        if (superCount != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Next Superleague must contain exactly {rules.SuperleagueSize} athletes (16 safe + 8 promoted + 8 qualifier winners), was {superCount}.");
        }
    }

    internal static async Task<(int StageCount, int SeasonCount, int RoundCount, int QualifierRounds, int QualifierStandings)> CapturePreservationAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierRounds = await context.QualifierRounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierStandings = await context.QualifierStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        return (stages, seasons, rounds, qualifierRounds, qualifierStandings);
    }

    internal static async Task VerifyPreservationAsync(
        SaveDbContext context,
        int stageCountBefore,
        int seasonCountBefore,
        int roundCountBefore,
        CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        if (stages != stageCountBefore)
        {
            throw new InvalidOperationException("Qualifier must not create league stage standings; championship totals are preserved.");
        }

        if (seasons != seasonCountBefore)
        {
            throw new InvalidOperationException("Qualifier must not create league season standings; championship totals are preserved.");
        }

        if (rounds != roundCountBefore)
        {
            throw new InvalidOperationException("Qualifier must not create league rounds; qualifier history lives in qualifier tables only.");
        }
    }

    internal static async Task<(SeasonEntity Source, SeasonEntity Next)> LoadPendingTransitionAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        List<SeasonEntity> candidates = await context.Seasons
            .Where(e => e.IsComplete && e.HasSuperleague)
            .OrderByDescending(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new RunQualifierConflictException(
                "No completed Superleague season is ready for the qualifier.");
        }

        foreach (SeasonEntity candidate in candidates)
        {
            SeasonEntity? successor = await context.Seasons.SingleOrDefaultAsync(
                e => e.SeasonNumber == candidate.SeasonNumber + 1, cancellationToken).ConfigureAwait(false);
            if (successor is null || !successor.HasSuperleague || successor.IsComplete)
            {
                continue;
            }

            bool hasQualifier = await context.QualifierStandings.AnyAsync(
                e => e.FromSeasonId == candidate.Id && e.ToSeasonId == successor.Id, cancellationToken).ConfigureAwait(false);
            if (hasQualifier)
            {
                continue;
            }

            return (candidate, successor);
        }

        bool anyQualifier = await context.QualifierStandings.AnyAsync(cancellationToken).ConfigureAwait(false);
        if (anyQualifier)
        {
            throw new RunQualifierConflictException(
                "The Superleague qualifier has already been resolved for the pending transition.");
        }

        throw new RunQualifierConflictException(
            "Automatic movement must be resolved before the Superleague qualifier can run.");
    }

    internal static async Task EnsureAutomaticMovementResolvedAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
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
        if (promotions != 8 || relegations != 8 || incumbents != 8 || challengers != 24)
        {
            throw new RunQualifierConflictException(
                "Automatic movement must be resolved before the Superleague qualifier can run.");
        }
    }

    internal static async Task EnsureQualifierAbsentAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        bool hasStandings = await context.QualifierStandings.AnyAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        if (hasStandings)
        {
            throw new RunQualifierConflictException(
                $"Qualifier for Season {source.SeasonNumber} has already been resolved.");
        }

        bool hasRounds = await context.QualifierRounds.AnyAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        if (hasRounds)
        {
            throw new InvalidOperationException($"Qualifier for Season {source.SeasonNumber} has corrupt partial rounds.");
        }
    }

    internal static async Task<List<LeagueEntity>> LoadSourceFeedersAsync(
        SaveDbContext context, SeasonEntity source, RulesV1 rules, CancellationToken cancellationToken)
    {
        List<LeagueEntity> leagues = await context.Leagues
            .Where(e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Feeder)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (leagues.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season {source.SeasonNumber} must have exactly {rules.RegularLeagueCount} feeder leagues, was {leagues.Count}.");
        }

        return leagues;
    }

    internal static async Task<LeagueEntity> LoadSourceSuperleagueAsync(
        SaveDbContext context, SeasonEntity source, CancellationToken cancellationToken)
    {
        LeagueEntity? league = await context.Leagues.SingleOrDefaultAsync(
            e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken).ConfigureAwait(false);
        if (league is null)
        {
            throw new InvalidOperationException($"Season {source.SeasonNumber} has no Superleague.");
        }

        return league;
    }

    internal static async Task<List<SeasonStandingEntity>> LoadLeagueStandingsAsync(
        SaveDbContext context,
        SeasonEntity source,
        LeagueEntity league,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingEntity> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId == league.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' must have exactly {rules.LeagueSize} final standings, was {rows.Count}.");
        }

        HashSet<int> ranks = rows.Select(r => r.SeasonRank).ToHashSet();
        if (!ranks.SetEquals(Enumerable.Range(1, rules.LeagueSize)))
        {
            throw new InvalidOperationException($"League '{league.Name}' must cover ranks 1..{rules.LeagueSize} exactly once.");
        }

        return rows;
    }

    internal static async Task<Dictionary<int, IReadOnlyList<SeasonStandingEntity>>> LoadFeederStandingsAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<LeagueEntity> feeders,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> map = new(feeders.Count);
        foreach (LeagueEntity feeder in feeders)
        {
            List<SeasonStandingEntity> rows = await LoadLeagueStandingsAsync(context, source, feeder, rules, cancellationToken).ConfigureAwait(false);
            map[feeder.Id] = rows;
        }

        return map;
    }

    internal static async Task<List<SeasonMembershipEntity>> LoadSourceMembershipsAsync(
        SaveDbContext context, SeasonEntity source, RulesV1 rules, CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Season {source.SeasonNumber} must have exactly {rules.TotalAthletesInSave} memberships, was {memberships.Count}.");
        }

        return memberships;
    }

    internal static async Task<Dictionary<int, string>> LoadAthleteNamesAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<RunQualifierResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        QualifierSimulation simulation,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<QualifierStandingEntity> standings = await LoadPersistedStandingsAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> names = await LoadAthleteNamesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await LoadLeaguesByIdAsync(context, cancellationToken).ConfigureAwait(false);
        List<QualifierStandingMember> members = MapResponseMembers(standings, names, leaguesById, simulation);
        return await MapResponseAsync(context, saveId, source, next, simulation, standings, members, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<List<QualifierStandingEntity>> LoadPersistedStandingsAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        return await context.QualifierStandings
            .AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .OrderBy(e => e.QualifierRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, LeagueEntity>> LoadLeaguesByIdAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static List<QualifierStandingMember> MapResponseMembers(
        List<QualifierStandingEntity> standings,
        Dictionary<int, string> names,
        Dictionary<int, LeagueEntity> leaguesById,
        QualifierSimulation simulation)
    {
        Dictionary<int, QualifierFieldSelection.QualifierPick> picksByAthlete =
            simulation.Field.All.ToDictionary(p => p.SaveAthleteId);
        List<QualifierStandingMember> members = new(standings.Count);
        foreach (QualifierStandingEntity standing in standings)
        {
            members.Add(MapSingleResponseMember(standing, names, leaguesById, picksByAthlete));
        }

        return members;
    }

    internal static QualifierStandingMember MapSingleResponseMember(
        QualifierStandingEntity standing,
        Dictionary<int, string> names,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, QualifierFieldSelection.QualifierPick> picksByAthlete)
    {
        names.TryGetValue(standing.SaveAthleteId, out string? name);
        leaguesById.TryGetValue(standing.FromLeagueId, out LeagueEntity? from);
        picksByAthlete.TryGetValue(standing.SaveAthleteId, out QualifierFieldSelection.QualifierPick? pick);
        string role = pick?.Role.ToString() ?? ((QualifierRole)standing.Role).ToString();
        string color = ((SportingColor)standing.SportingColor).ToString();
        return new QualifierStandingMember(
            standing.SaveAthleteId,
            name ?? $"Athlete {standing.SaveAthleteId}",
            color,
            role,
            standing.FromLeagueId,
            from?.Name ?? $"League {standing.FromLeagueId}",
            standing.FromSeasonRank,
            standing.QualifierRank,
            standing.QualifierScoreThousandths,
            standing.BaseScoreThousandths,
            standing.RoundWins,
            standing.IsQualified);
    }

    internal static async Task<RunQualifierResponse> MapResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        QualifierSimulation simulation,
        List<QualifierStandingEntity> standings,
        List<QualifierStandingMember> members,
        CancellationToken cancellationToken)
    {
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        Pcg32State rngAfter = rng.ToState();
        QualifierRoundPayloadDocument first = simulation.Payloads[0];
        int incumbents = standings.Count(s => s.Role == (int)QualifierRole.Incumbent && s.IsQualified);
        int challengers = standings.Count(s => s.Role == (int)QualifierRole.Challenger && s.IsQualified);
        return new RunQualifierResponse(
            saveId,
            source.SeasonNumber,
            next.SeasonNumber,
            standings.Count,
            simulation.Payloads.Count,
            standings.Count(s => s.IsQualified),
            simulation.Checksum,
            first.RngBeforeState,
            first.RngBeforeStream,
            rngAfter.State,
            rngAfter.Stream,
            members,
            incumbents,
            challengers);
    }
}
