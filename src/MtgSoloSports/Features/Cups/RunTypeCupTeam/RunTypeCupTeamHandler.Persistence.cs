using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

public sealed partial class RunTypeCupTeamHandler
{
    /// <summary>
    /// Persists the completed tournament (MSS-062): new round rows with stage
    /// identity, per-stage leg/team standings (qualificationMedal None, Final
    /// medals Gold/Silver/Bronze), Final-only podium honours, permanent
    /// nationality for every actual participant, and the RNG-after state.
    /// Stages already persisted by step-by-step qual completions are validated
    /// but not re-persisted.
    /// </summary>
    internal static async Task PersistTournamentAsync(
        SaveDbContext context,
        TournamentState state,
        TournamentSimulation simulation,
        CancellationToken cancellationToken)
    {
        int alreadyPersisted = state.PlayedOrdered.Count;
        PersistTournamentRoundRows(context, state.Source, simulation.Ordered, alreadyPersisted);
        await PersistTournamentLegsAndTeamsAsync(context, state, simulation, cancellationToken).ConfigureAwait(false);
        PersistFinalHonours(context, state.Source, simulation);
        await ApplyTournamentNationalityAsync(context, state, simulation, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void PersistTournamentRoundRows(
        SaveDbContext context,
        SeasonEntity source,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        int alreadyPersisted)
    {
        foreach ((TypeCupTournamentPlan.StageKey key, TypeCupTeamRoundPayloadDocument payload) in ordered.Skip(alreadyPersisted))
        {
            context.TypeCupTeamRounds.Add(new TypeCupTeamRoundEntity
            {
                SourceSeasonId = source.Id,
                SourceSeasonNumber = source.SeasonNumber,
                TournamentPhase = key.Phase,
                QualificationGroup = key.QualificationGroup,
                GroupNumber = payload.GroupNumber,
                RoundNumber = payload.RoundNumber,
                RulesVersion = payload.RulesVersion,
                RngBeforeState = unchecked((long)payload.RngBeforeState),
                RngBeforeStream = unchecked((long)payload.RngBeforeStream),
                RngAfterState = unchecked((long)payload.RngAfterState),
                RngAfterStream = unchecked((long)payload.RngAfterStream),
                PayloadJson = payload.ToStored(),
                PayloadChecksum = payload.Checksum,
            });
        }
    }

    internal static async Task PersistTournamentLegsAndTeamsAsync(
        SaveDbContext context,
        TournamentState state,
        TournamentSimulation simulation,
        CancellationToken cancellationToken)
    {
        Dictionary<int, TypeCupSelectionEntity> selectionByAthlete = (await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == state.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToDictionary(e => e.SaveAthleteId);
        if (selectionByAthlete.Count != state.Selection.Count)
        {
            throw new InvalidOperationException("Type Cup team selection changed during the team event; aborting.");
        }

        foreach (StageSimulation stage in simulation.Stages)
        {
            if (await StageStandingsExistAsync(context, state, stage, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            PersistSingleStageStandings(context, state, stage, selectionByAthlete);
        }
    }

    private static async Task<bool> StageStandingsExistAsync(
        SaveDbContext context,
        TournamentState state,
        StageSimulation stage,
        CancellationToken cancellationToken)
    {
        bool legsExist = await context.TypeCupTeamGroupStandings.AnyAsync(
            e => e.SourceSeasonId == state.Source.Id
                && e.TournamentPhase == stage.Key.Phase
                && e.QualificationGroup == stage.Key.QualificationGroup,
            cancellationToken).ConfigureAwait(false);
        bool teamsExist = await context.TypeCupTeamStandings.AnyAsync(
            e => e.SourceSeasonId == state.Source.Id
                && e.TournamentPhase == stage.Key.Phase
                && e.QualificationGroup == stage.Key.QualificationGroup,
            cancellationToken).ConfigureAwait(false);
        if (legsExist || teamsExist)
        {
            if (legsExist != teamsExist)
            {
                throw new InvalidOperationException(
                    $"Type Cup tournament stage phase {stage.Key.Phase} qual {stage.Key.QualificationGroup} has corrupt partial standings.");
            }

            return true;
        }

        return false;
    }

    private static void PersistSingleStageStandings(
        SaveDbContext context,
        TournamentState state,
        StageSimulation stage,
        Dictionary<int, TypeCupSelectionEntity> selectionByAthlete)
    {
        bool isFinal = stage.Key.Phase == (int)TypeCupTournamentFormat.TournamentPhase.Final
            || stage.Key.Phase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField;
        foreach (TeamEvent.TeamLegRanked leg in stage.Legs)
        {
            PersistStageLeg(context, state.Source, stage.Key, leg, selectionByAthlete);
        }

        foreach (TeamEvent.TeamRanked team in stage.Teams)
        {
            context.TypeCupTeamStandings.Add(new TypeCupTeamStandingEntity
            {
                SourceSeasonId = state.Source.Id,
                SourceSeasonNumber = state.Source.SeasonNumber,
                TournamentPhase = stage.Key.Phase,
                QualificationGroup = stage.Key.QualificationGroup,
                CreatureType = team.TeamName,
                TeamRank = team.TeamRank,
                TeamScoreThousandths = team.TeamScoreThousandths,
                TeamBaseThousandths = team.TeamBaseThousandths,
                GroupWins = team.GroupWins,
                RoundWins = team.RoundWins,
                GroupPlaceCountsJson = JsonSerializer.Serialize(team.GroupPlaceCounts),
                RoundPlaceCountsJson = JsonSerializer.Serialize(team.RoundPlaceCounts),
                Medal = MedalForStageTeam(isFinal, team.TeamRank),
            });
        }
    }

    private static int MedalForStageTeam(bool isFinal, int rank) =>
        !isFinal
            ? (int)TypeCupMedal.None
            : rank switch
            {
                1 => (int)TypeCupMedal.Gold,
                2 => (int)TypeCupMedal.Silver,
                3 => (int)TypeCupMedal.Bronze,
                _ => (int)TypeCupMedal.None,
            };

    internal static void PersistStageLeg(
        SaveDbContext context,
        SeasonEntity source,
        TypeCupTournamentPlan.StageKey key,
        TeamEvent.TeamLegRanked leg,
        Dictionary<int, TypeCupSelectionEntity> selectionByAthlete)
    {
        if (!selectionByAthlete.TryGetValue(leg.AthleteId, out TypeCupSelectionEntity? row))
        {
            throw new InvalidOperationException($"Type Cup team leg for '{leg.Name}' has no selection provenance.");
        }

        if (!string.Equals(leg.TeamName, row.CreatureType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Type Cup team leg for '{leg.Name}' changes creature type within the event.");
        }

        int groupNumber = GroupNumberForLeg(row);
        context.TypeCupTeamGroupStandings.Add(new TypeCupTeamGroupStandingEntity
        {
            SourceSeasonId = source.Id,
            SourceSeasonNumber = source.SeasonNumber,
            TournamentPhase = key.Phase,
            QualificationGroup = key.QualificationGroup,
            GroupNumber = groupNumber,
            SaveAthleteId = leg.AthleteId,
            GroupRank = leg.LegRank,
            GroupScoreThousandths = leg.LegScoreThousandths,
            BaseScoreThousandths = leg.LegBaseThousandths,
            RoundWins = leg.RoundWins,
            RoundPlaceCountsJson = JsonSerializer.Serialize(leg.RoundPlaceCounts),
            CreatureType = row.CreatureType,
            SelectionRank = row.SelectionRank,
        });
    }

    internal static void PersistFinalHonours(
        SaveDbContext context,
        SeasonEntity source,
        TournamentSimulation simulation)
    {
        // Honours and medals are Final-only: qualification group winners
        // receive no title/medal honour. Existing MSS-047 podium semantics
        // (four members each for ranks 1..3) apply to the Final only.
        StageSimulation final = simulation.Final;
        int podiumRanks = Math.Min(3, final.Teams.Count);
        foreach (TeamEvent.TeamRanked podiumTeam in final.Teams.Where(t => t.TeamRank >= 1 && t.TeamRank <= podiumRanks).OrderBy(t => t.TeamRank))
        {
            Features.Records.HonourKind kind = Features.Records.HonourKindMapper.FromTypeCupTeamRank(podiumTeam.TeamRank);
            List<TeamEvent.TeamLegRanked> legs = final.Legs
                .Where(l => l.TeamId == podiumTeam.TeamId)
                .OrderBy(l => l.AthleteId)
                .ToList();
            if (legs.Count != 4)
            {
                throw new InvalidOperationException($"Type Cup team '{podiumTeam.TeamName}' rank {podiumTeam.TeamRank} must field exactly four legs.");
            }

            foreach (TeamEvent.TeamLegRanked leg in legs)
            {
                context.Honours.Add(new HonourEntity
                {
                    SeasonId = source.Id,
                    SeasonNumber = source.SeasonNumber,
                    LeagueId = TeamLeagueId,
                    LeagueName = TeamLeagueName,
                    LeagueKind = TeamLeagueKind,
                    SaveAthleteId = leg.AthleteId,
                    Kind = (int)kind,
                });
            }
        }
    }

    /// <summary>
    /// Persists permanent Type Cup nationality for every actual tournament
    /// participant (MSS-062): qualification-group athletes are capped even when
    /// eliminated before the Final; Finalists are already capped via
    /// qualification; direct-Final participants are capped at completion.
    /// Idempotent and never changes an existing cap. Athletes from selected
    /// teams that never entered a persisted stage are never capped here.
    /// </summary>
    internal static async Task ApplyTournamentNationalityAsync(
        SaveDbContext context,
        TournamentState state,
        TournamentSimulation simulation,
        CancellationToken cancellationToken)
    {
        HashSet<int> participantIds = new();
        foreach (StageSimulation stage in simulation.Stages)
        {
            foreach (TeamEvent.TeamLegRanked leg in stage.Legs)
            {
                participantIds.Add(leg.AthleteId);
            }
        }

        Dictionary<int, string> typeByAthlete = state.Selection
            .Where(e => participantIds.Contains(e.SaveAthleteId))
            .ToDictionary(e => e.SaveAthleteId, e => e.CreatureType);
        if (typeByAthlete.Count != participantIds.Count)
        {
            throw new InvalidOperationException("Type Cup tournament participants have no selection provenance.");
        }

        List<int> ids = typeByAthlete.Keys.ToList();
        List<SaveAthleteEntity> athletes = await context.SaveAthletes
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (athletes.Count != ids.Count)
        {
            throw new InvalidOperationException("Type Cup team selection references unknown athletes.");
        }

        foreach (SaveAthleteEntity athlete in athletes)
        {
            string allocated = typeByAthlete[athlete.Id];
            if (string.IsNullOrWhiteSpace(athlete.TypeCupNationality))
            {
                athlete.TypeCupNationality = allocated;
                continue;
            }

            if (!string.Equals(athlete.TypeCupNationality.Trim(), allocated, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Athlete '{athlete.Name}' is capped for '{athlete.TypeCupNationality}' but participated for '{allocated}'; nationality can never change.");
            }
        }
    }

    /// <summary>
    /// Caps one qualification stage's participants immediately when the stage
    /// completes step-by-step, so eliminated teams retain nationality even if
    /// the tournament never reaches the Final. Idempotent.
    /// </summary>
    internal static async Task ApplyStageNationalityAsync(
        SaveDbContext context,
        TournamentState state,
        TypeCupTournamentPlan.StageKey stage,
        IReadOnlyList<TeamEvent.TeamLegRanked> legs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        Dictionary<int, string> typeByAthlete = state.Selection
            .Where(e => legs.Any(l => l.AthleteId == e.SaveAthleteId))
            .ToDictionary(e => e.SaveAthleteId, e => e.CreatureType);
        List<int> ids = typeByAthlete.Keys.ToList();
        if (ids.Count == 0)
        {
            return;
        }

        List<SaveAthleteEntity> athletes = await context.SaveAthletes
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (athletes.Count != ids.Count)
        {
            throw new InvalidOperationException("Type Cup team selection references unknown athletes.");
        }

        foreach (SaveAthleteEntity athlete in athletes)
        {
            string allocated = typeByAthlete[athlete.Id];
            if (string.IsNullOrWhiteSpace(athlete.TypeCupNationality))
            {
                athlete.TypeCupNationality = allocated;
                continue;
            }

            if (!string.Equals(athlete.TypeCupNationality.Trim(), allocated, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Athlete '{athlete.Name}' is capped for '{athlete.TypeCupNationality}' but participated for '{allocated}'; nationality can never change.");
            }
        }
    }

    // Legacy single-event persistence kept for callers compiled against the old
    // shape. New code prefers PersistTournamentAsync.
    internal static async Task PersistTeamAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<TypeCupSelectionEntity> selection,
        TeamSimulation simulation,
        int teamCount,
        int alreadyPersisted,
        CancellationToken cancellationToken)
    {
        PersistRoundRows(context, source, simulation, alreadyPersisted);
        await PersistLegRowsAsync(context, source, selection, simulation, teamCount, cancellationToken).ConfigureAwait(false);
        PersistTeamRows(context, source, simulation);
        PersistChampionHonours(context, source, simulation);
        await ApplyNationalityAsync(context, selection, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void PersistRoundRows(
        SaveDbContext context,
        SeasonEntity source,
        TeamSimulation simulation,
        int alreadyPersisted)
    {
        foreach (TypeCupTeamRoundPayloadDocument payload in simulation.Payloads.Skip(alreadyPersisted))
        {
            context.TypeCupTeamRounds.Add(new TypeCupTeamRoundEntity
            {
                SourceSeasonId = source.Id,
                SourceSeasonNumber = source.SeasonNumber,
                GroupNumber = payload.GroupNumber,
                RoundNumber = payload.RoundNumber,
                RulesVersion = payload.RulesVersion,
                RngBeforeState = unchecked((long)payload.RngBeforeState),
                RngBeforeStream = unchecked((long)payload.RngBeforeStream),
                RngAfterState = unchecked((long)payload.RngAfterState),
                RngAfterStream = unchecked((long)payload.RngAfterStream),
                PayloadJson = payload.ToStored(),
                PayloadChecksum = payload.Checksum,
            });
        }
    }

    internal static async Task PersistLegRowsAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<TypeCupSelectionEntity> selection,
        TeamSimulation simulation,
        int teamCount,
        CancellationToken cancellationToken)
    {
        Dictionary<int, TypeCupSelectionEntity> selectionByAthlete = (await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToDictionary(e => e.SaveAthleteId);
        if (selectionByAthlete.Count != selection.Count)
        {
            throw new InvalidOperationException("Type Cup team selection changed during the team event; aborting.");
        }

        foreach (TeamEvent.TeamLegRanked leg in simulation.Legs)
        {
            PersistSingleLeg(context, source, leg, selectionByAthlete);
        }
    }

    internal static void PersistSingleLeg(
        SaveDbContext context,
        SeasonEntity source,
        TeamEvent.TeamLegRanked leg,
        Dictionary<int, TypeCupSelectionEntity> selectionByAthlete)
    {
        if (!selectionByAthlete.TryGetValue(leg.AthleteId, out TypeCupSelectionEntity? row))
        {
            throw new InvalidOperationException($"Type Cup team leg for '{leg.Name}' has no selection provenance.");
        }

        if (!string.Equals(leg.TeamName, row.CreatureType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Type Cup team leg for '{leg.Name}' changes creature type within the event.");
        }

        int groupNumber = GroupNumberForLeg(row);
        context.TypeCupTeamGroupStandings.Add(new TypeCupTeamGroupStandingEntity
        {
            SourceSeasonId = source.Id,
            SourceSeasonNumber = source.SeasonNumber,
            GroupNumber = groupNumber,
            SaveAthleteId = leg.AthleteId,
            GroupRank = leg.LegRank,
            GroupScoreThousandths = leg.LegScoreThousandths,
            BaseScoreThousandths = leg.LegBaseThousandths,
            RoundWins = leg.RoundWins,
            RoundPlaceCountsJson = JsonSerializer.Serialize(leg.RoundPlaceCounts),
            CreatureType = row.CreatureType,
            SelectionRank = row.SelectionRank,
        });
    }

    internal static int GroupNumberForLeg(TypeCupSelectionEntity selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.SelectionRank < 1 || selection.SelectionRank > 4)
        {
            throw new InvalidOperationException($"Type Cup team selection rank {selection.SelectionRank} is out of range.");
        }

        return selection.SelectionRank;
    }

    internal static void PersistTeamRows(
        SaveDbContext context,
        SeasonEntity source,
        TeamSimulation simulation)
    {
        foreach (TeamEvent.TeamRanked team in simulation.Teams)
        {
            context.TypeCupTeamStandings.Add(new TypeCupTeamStandingEntity
            {
                SourceSeasonId = source.Id,
                SourceSeasonNumber = source.SeasonNumber,
                CreatureType = team.TeamName,
                TeamRank = team.TeamRank,
                TeamScoreThousandths = team.TeamScoreThousandths,
                TeamBaseThousandths = team.TeamBaseThousandths,
                GroupWins = team.GroupWins,
                RoundWins = team.RoundWins,
                GroupPlaceCountsJson = JsonSerializer.Serialize(team.GroupPlaceCounts),
                RoundPlaceCountsJson = JsonSerializer.Serialize(team.RoundPlaceCounts),
                Medal = team.TeamRank switch
                {
                    1 => (int)TypeCupMedal.Gold,
                    2 => (int)TypeCupMedal.Silver,
                    3 => (int)TypeCupMedal.Bronze,
                    _ => (int)TypeCupMedal.None,
                },
            });
        }
    }

    internal static void PersistChampionHonours(
        SaveDbContext context,
        SeasonEntity source,
        TeamSimulation simulation)
    {
        PersistPodiumHonours(context, source, simulation);
    }

    internal static void PersistPodiumHonours(
        SaveDbContext context,
        SeasonEntity source,
        TeamSimulation simulation)
    {
        int podiumRanks = Math.Min(3, simulation.Teams.Count);
        foreach (TeamEvent.TeamRanked podiumTeam in simulation.Teams.Where(t => t.TeamRank >= 1 && t.TeamRank <= podiumRanks).OrderBy(t => t.TeamRank))
        {
            Features.Records.HonourKind kind = Features.Records.HonourKindMapper.FromTypeCupTeamRank(podiumTeam.TeamRank);
            List<TeamEvent.TeamLegRanked> legs = simulation.Legs
                .Where(l => l.TeamId == podiumTeam.TeamId)
                .OrderBy(l => l.AthleteId)
                .ToList();
            if (legs.Count != 4)
            {
                throw new InvalidOperationException($"Type Cup team '{podiumTeam.TeamName}' rank {podiumTeam.TeamRank} must field exactly four legs.");
            }

            foreach (TeamEvent.TeamLegRanked leg in legs)
            {
                context.Honours.Add(new HonourEntity
                {
                    SeasonId = source.Id,
                    SeasonNumber = source.SeasonNumber,
                    LeagueId = TeamLeagueId,
                    LeagueName = TeamLeagueName,
                    LeagueKind = TeamLeagueKind,
                    SaveAthleteId = leg.AthleteId,
                    Kind = (int)kind,
                });
            }
        }
    }

    /// <summary>
    /// Persists permanent Type Cup nationality atomically with the event. Every
    /// participant must already be allocated to its nationality: capped athletes
    /// must match, uncapped athletes are capped now to their allocated creature
    /// type. Any mismatch aborts; nationality is never changed once set.
    /// </summary>
    internal static async Task ApplyNationalityAsync(
        SaveDbContext context,
        List<TypeCupSelectionEntity> selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selection);
        Dictionary<int, string> typeByAthlete = selection.ToDictionary(e => e.SaveAthleteId, e => e.CreatureType);
        List<int> ids = typeByAthlete.Keys.ToList();
        List<SaveAthleteEntity> athletes = await context.SaveAthletes
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (athletes.Count != ids.Count)
        {
            throw new InvalidOperationException("Type Cup team selection references unknown athletes.");
        }

        foreach (SaveAthleteEntity athlete in athletes)
        {
            string allocated = typeByAthlete[athlete.Id];
            if (string.IsNullOrWhiteSpace(athlete.TypeCupNationality))
            {
                athlete.TypeCupNationality = allocated;
                continue;
            }

            if (!string.Equals(athlete.TypeCupNationality.Trim(), allocated, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Athlete '{athlete.Name}' is capped for '{athlete.TypeCupNationality}' but participated for '{allocated}'; nationality can never change.");
            }
        }
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
            throw new InvalidOperationException("Type Cup team must not create league stage standings; championship totals are preserved.");
        }

        if (seasons != seasonCountBefore)
        {
            throw new InvalidOperationException("Type Cup team must not create league season standings; championship totals are preserved.");
        }

        if (rounds != roundCountBefore)
        {
            throw new InvalidOperationException("Type Cup team must not create league rounds; Cup history lives in Cup tables only.");
        }

        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths, cancellationToken).ConfigureAwait(false);
        if (lifetime != lifetimeBefore)
        {
            throw new InvalidOperationException("Type Cup team participation must not change career bonus.");
        }

        long effective = await context.AthleteCareers.SumAsync(e => (long)e.CurrentEffectiveBonusThousandths, cancellationToken).ConfigureAwait(false);
        if (effective != effectiveBefore)
        {
            throw new InvalidOperationException("Type Cup team participation must not change effective bonus projections.");
        }

        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths, cancellationToken).ConfigureAwait(false);
        if (championship != championshipBefore)
        {
            throw new InvalidOperationException("Type Cup team must not award league championship points.");
        }
    }

    internal static async Task ValidatePersistedAsync(
        SaveDbContext context,
        TournamentState state,
        TournamentSimulation simulation,
        CancellationToken cancellationToken)
    {
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds
            .Where(e => e.SourceSeasonId == state.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings
            .Where(e => e.SourceSeasonId == state.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings
            .Where(e => e.SourceSeasonId == state.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<HonourEntity> honours = await context.Honours
            .Where(e => e.SeasonId == state.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        TypeCupTeamInvariants.ValidateTournamentPersisted(state.Source, rounds, legs, teams, honours, state.Rules, state.Plan);
        if (!string.Equals(ComputeChecksum(simulation.Final.Teams), simulation.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Type Cup team checksum does not match simulated standings.");
        }

        await VerifyPreservationAsync(
            context, state.StageCountBefore, state.SeasonCountBefore, state.RoundCountBefore,
            state.LifetimeBefore, state.EffectiveBefore, state.ChampionshipBefore, cancellationToken).ConfigureAwait(false);
        await ValidateNationalityPersistedAsync(context, state.Source, legs, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ValidatePersistedAsync(
        SaveDbContext context,
        SeasonEntity source,
        TeamSimulation simulation,
        RulesV1 rules,
        int stageCountBefore,
        int seasonCountBefore,
        int roundCountBefore,
        long lifetimeBefore,
        long effectiveBefore,
        long championshipBefore,
        CancellationToken cancellationToken)
    {
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<HonourEntity> honours = await context.Honours
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        TypeCupTeamInvariants.ValidatePersisted(source, rounds, legs, teams, honours, rules);
        if (!string.Equals(ComputeChecksum(simulation.Teams), simulation.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Type Cup team checksum does not match simulated standings.");
        }

        await VerifyPreservationAsync(
            context, stageCountBefore, seasonCountBefore, roundCountBefore,
            lifetimeBefore, effectiveBefore, championshipBefore, cancellationToken).ConfigureAwait(false);
        await ValidateNationalityPersistedAsync(context, source, legs, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ValidateNationalityPersistedAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<TypeCupTeamGroupStandingEntity> legs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(legs);
        if (legs.Count == 0)
        {
            return;
        }

        HashSet<int> participantIds = legs.Select(l => l.SaveAthleteId).ToHashSet();
        Dictionary<int, string> typeByAthlete = legs
            .GroupBy(l => l.SaveAthleteId)
            .ToDictionary(g => g.Key, g => g.First().CreatureType);
        List<SaveAthleteEntity> athletes = await context.SaveAthletes
            .Where(e => participantIds.Contains(e.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (athletes.Count != participantIds.Count)
        {
            throw new InvalidOperationException("Type Cup team participants reference unknown athletes.");
        }

        foreach (SaveAthleteEntity athlete in athletes)
        {
            string allocated = typeByAthlete[athlete.Id];
            if (string.IsNullOrWhiteSpace(athlete.TypeCupNationality))
            {
                throw new InvalidOperationException($"Athlete '{athlete.Name}' participated for '{allocated}' but has no permanent nationality.");
            }

            if (!string.Equals(athlete.TypeCupNationality.Trim(), allocated, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Athlete '{athlete.Name}' is capped for '{athlete.TypeCupNationality}' but participated for '{allocated}'; nationality can never change.");
            }
        }
    }

    internal static async Task EmitTeamStoriesAsync(
        SaveDbContext context,
        SeasonEntity source,
        TournamentSimulation simulation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(simulation);
        StageSimulation final = simulation.Final;
        bool emitted = false;
        foreach (TeamEvent.TeamRanked team in final.Teams.Where(t => t.TeamRank <= 3).OrderBy(t => t.TeamRank))
        {
            emitted |= await EmitMedalForTeamAsync(context, source, final, team, cancellationToken).ConfigureAwait(false);
        }

        emitted |= await EmitTitleForChampionAsync(context, source, final, cancellationToken).ConfigureAwait(false);
        if (emitted)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task EmitTeamStoriesAsync(
        SaveDbContext context,
        SeasonEntity source,
        TeamSimulation simulation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(simulation);
        bool emitted = false;
        foreach (TeamEvent.TeamRanked team in simulation.Teams.Where(t => t.TeamRank <= 3).OrderBy(t => t.TeamRank))
        {
            emitted |= await EmitMedalForLegacyTeamAsync(context, source, simulation, team, cancellationToken).ConfigureAwait(false);
        }

        emitted |= await EmitTitleForLegacyChampionAsync(context, source, simulation, cancellationToken).ConfigureAwait(false);
        if (emitted)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<bool> EmitMedalForTeamAsync(
        SaveDbContext context,
        SeasonEntity source,
        StageSimulation final,
        TeamEvent.TeamRanked team,
        CancellationToken cancellationToken)
    {
        string medal = team.TeamRank switch
        {
            1 => nameof(TypeCupMedal.Gold),
            2 => nameof(TypeCupMedal.Silver),
            3 => nameof(TypeCupMedal.Bronze),
            _ => nameof(TypeCupMedal.None),
        };
        bool emitted = false;
        List<TeamEvent.TeamLegRanked> members = final.Legs
            .Where(l => l.TeamId == team.TeamId)
            .OrderBy(l => l.AthleteId)
            .ToList();
        foreach (TeamEvent.TeamLegRanked leg in members)
        {
            emitted |= await Features.Stories.StoryEventEmitter.TryEmitAsync(
                context,
                leg.AthleteId,
                Features.Stories.StoryEventType.TypeCupTeamMedal,
                $"type-cup-team-s{source.SeasonNumber}-{medal.ToLowerInvariant()}",
                source.SeasonNumber,
                null,
                new Features.Stories.StoryEventPayload(
                    leg.Name,
                    source.SeasonNumber,
                    LeagueName: TeamLeagueName,
                    CupRank: team.TeamRank,
                    Medal: medal,
                    SportingColor: team.TeamName),
                cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    internal static async Task<bool> EmitTitleForChampionAsync(
        SaveDbContext context,
        SeasonEntity source,
        StageSimulation final,
        CancellationToken cancellationToken)
    {
        TeamEvent.TeamRanked champion = final.Teams.Single(r => r.TeamRank == 1);
        bool emitted = false;
        List<TeamEvent.TeamLegRanked> members = final.Legs
            .Where(l => l.TeamId == champion.TeamId)
            .OrderBy(l => l.AthleteId)
            .ToList();
        foreach (TeamEvent.TeamLegRanked leg in members)
        {
            emitted |= await Features.Stories.StoryEventEmitter.TryEmitAsync(
                context,
                leg.AthleteId,
                Features.Stories.StoryEventType.TypeCupTeamTitle,
                $"type-cup-team-s{source.SeasonNumber}-title",
                source.SeasonNumber,
                null,
                new Features.Stories.StoryEventPayload(
                    leg.Name,
                    source.SeasonNumber,
                    LeagueName: TeamLeagueName,
                    CupRank: 1,
                    Medal: nameof(TypeCupMedal.Gold),
                    SportingColor: champion.TeamName),
                cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    internal static async Task<bool> EmitMedalForLegacyTeamAsync(
        SaveDbContext context,
        SeasonEntity source,
        TeamSimulation simulation,
        TeamEvent.TeamRanked team,
        CancellationToken cancellationToken) =>
        await EmitMedalForTeamAsync(context, source, new StageSimulation(
            TypeCupTournamentPlan.LegacyKey(), simulation.Teams.Count, simulation.Legs, simulation.Teams, simulation.RngAfter, simulation.Checksum),
            team, cancellationToken).ConfigureAwait(false);

    internal static async Task<bool> EmitTitleForLegacyChampionAsync(
        SaveDbContext context,
        SeasonEntity source,
        TeamSimulation simulation,
        CancellationToken cancellationToken) =>
        await EmitTitleForChampionAsync(context, source, new StageSimulation(
            TypeCupTournamentPlan.LegacyKey(), simulation.Teams.Count, simulation.Legs, simulation.Teams, simulation.RngAfter, simulation.Checksum),
            cancellationToken).ConfigureAwait(false);

    internal static async Task<RunTypeCupTeamResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity source,
        TournamentSimulation simulation,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        StageSimulation final = simulation.Final;
        int finalPhase = final.Key.Phase;
        int finalQual = final.Key.QualificationGroup;
        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == finalPhase && e.QualificationGroup == finalQual)
            .OrderBy(e => e.TeamRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == finalPhase && e.QualificationGroup == finalQual)
            .OrderBy(e => e.GroupNumber)
            .ThenBy(e => e.GroupRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (teams.Count != final.Teams.Count || legs.Count != final.Legs.Count)
        {
            throw new InvalidOperationException("Type Cup team persisted result does not match the simulation.");
        }

        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamMember> teamMembers = MapTeamMembers(teams);
        List<TypeCupTeamLegMember> legMembers = MapLegMembers(legs, names);
        TypeCupTeamStandingEntity champion = teams.Single(s => s.TeamRank == 1);
        // RNG boundaries for the tournament: first round of the first stage to
        // the Final team ranking (save RNG). Checksum is the Final checksum.
        var orderedRounds = rounds
            .OrderBy(r => r.TournamentPhase).ThenBy(r => r.QualificationGroup).ThenBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber)
            .ToList();
        TypeCupTeamRoundPayloadDocument first = TypeCupTeamRoundPayloadDocument.FromStored(orderedRounds.First().PayloadJson);
        // Last persisted round's payload holds round-only RNG; the tournament
        // RNG-after (after Final team ranking) is the save RNG.
        Pcg32State rngAfter = simulation.RngAfter;
        return new RunTypeCupTeamResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            teams.Count,
            4,
            8,
            simulation.Checksum,
            first.RngBeforeState,
            first.RngBeforeStream,
            rngAfter.State,
            rngAfter.Stream,
            champion.CreatureType,
            champion.CreatureType,
            teamMembers,
            legMembers);
    }

    internal static async Task<RunTypeCupTeamResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity source,
        TeamSimulation simulation,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.TeamRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber)
            .ThenBy(e => e.GroupRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (teams.Count != simulation.Teams.Count || legs.Count != simulation.Legs.Count || rounds.Count != simulation.Payloads.Count)
        {
            throw new InvalidOperationException("Type Cup team persisted result does not match the simulation.");
        }

        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamMember> teamMembers = MapTeamMembers(teams);
        List<TypeCupTeamLegMember> legMembers = MapLegMembers(legs, names);
        TypeCupTeamStandingEntity champion = teams.Single(s => s.TeamRank == 1);
        TypeCupTeamRoundPayloadDocument first = TypeCupTeamRoundPayloadDocument.FromStored(
            rounds.OrderBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber).First().PayloadJson);
        TypeCupTeamRoundPayloadDocument last = TypeCupTeamRoundPayloadDocument.FromStored(
            rounds.OrderBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber).Last().PayloadJson);
        return new RunTypeCupTeamResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            teams.Count,
            4,
            8,
            simulation.Checksum,
            first.RngBeforeState,
            first.RngBeforeStream,
            last.RngAfterState,
            last.RngAfterStream,
            champion.CreatureType,
            champion.CreatureType,
            teamMembers,
            legMembers);
    }

    internal static List<TypeCupTeamMember> MapTeamMembers(List<TypeCupTeamStandingEntity> teams)
    {
        ArgumentNullException.ThrowIfNull(teams);
        List<TypeCupTeamMember> members = new(teams.Count);
        foreach (TypeCupTeamStandingEntity team in teams.OrderBy(t => t.TeamRank))
        {
            members.Add(new TypeCupTeamMember(
                team.CreatureType,
                team.CreatureType,
                team.TeamRank,
                team.TeamScoreThousandths,
                team.TeamBaseThousandths,
                team.GroupWins,
                team.RoundWins,
                ((TypeCupMedal)team.Medal).ToString()));
        }

        return members;
    }

    internal static List<TypeCupTeamLegMember> MapLegMembers(
        List<TypeCupTeamGroupStandingEntity> legs,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(names);
        List<TypeCupTeamLegMember> members = new(legs.Count);
        foreach (TypeCupTeamGroupStandingEntity leg in legs.OrderBy(l => l.GroupNumber).ThenBy(l => l.GroupRank))
        {
            names.TryGetValue(leg.SaveAthleteId, out string? name);
            members.Add(new TypeCupTeamLegMember(
                leg.SaveAthleteId,
                name ?? $"Athlete {leg.SaveAthleteId}",
                leg.CreatureType,
                leg.SelectionRank,
                leg.GroupNumber,
                leg.GroupRank,
                leg.GroupScoreThousandths,
                leg.BaseScoreThousandths,
                leg.RoundWins));
        }

        return members;
    }
}
