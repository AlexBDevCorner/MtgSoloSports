using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

public sealed partial class RunTypeCupTeamHandler
{
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
        TeamEvent.TeamRanked champion = simulation.Teams.Single(r => r.TeamRank == 1);
        List<TeamEvent.TeamLegRanked> championLegs = simulation.Legs
            .Where(l => l.TeamId == champion.TeamId)
            .OrderBy(l => l.AthleteId)
            .ToList();
        if (championLegs.Count != 4)
        {
            throw new InvalidOperationException($"Type Cup champion team '{champion.TeamName}' must field exactly four legs.");
        }

        foreach (TeamEvent.TeamLegRanked leg in championLegs)
        {
            context.Honours.Add(new HonourEntity
            {
                SeasonId = source.Id,
                SeasonNumber = source.SeasonNumber,
                LeagueId = TeamLeagueId,
                LeagueName = TeamLeagueName,
                LeagueKind = TeamLeagueKind,
                SaveAthleteId = leg.AthleteId,
                Kind = (int)Features.Records.HonourKind.TypeCupTeamChampion,
            });
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
        Dictionary<int, string> typeByAthlete = legs.ToDictionary(l => l.SaveAthleteId, l => l.CreatureType);
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
        TeamSimulation simulation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(simulation);
        bool emitted = false;
        foreach (TeamEvent.TeamRanked team in simulation.Teams.Where(t => t.TeamRank <= 3).OrderBy(t => t.TeamRank))
        {
            emitted |= await EmitMedalForTeamAsync(context, source, simulation, team, cancellationToken).ConfigureAwait(false);
        }

        emitted |= await EmitTitleForChampionAsync(context, source, simulation, cancellationToken).ConfigureAwait(false);
        if (emitted)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<bool> EmitMedalForTeamAsync(
        SaveDbContext context,
        SeasonEntity source,
        TeamSimulation simulation,
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
        List<TeamEvent.TeamLegRanked> members = simulation.Legs
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
        TeamSimulation simulation,
        CancellationToken cancellationToken)
    {
        TeamEvent.TeamRanked champion = simulation.Teams.Single(r => r.TeamRank == 1);
        bool emitted = false;
        List<TeamEvent.TeamLegRanked> members = simulation.Legs
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
