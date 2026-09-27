using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.RunColorCupTeam;

public sealed partial class RunColorCupTeamHandler
{
    internal static async Task PersistTeamAsync(
        SaveDbContext context,
        SeasonEntity source,
        TeamSimulation simulation,
        CancellationToken cancellationToken)
    {
        PersistRoundRows(context, source, simulation);
        await PersistLegRowsAsync(context, source, simulation, cancellationToken).ConfigureAwait(false);
        PersistTeamRows(context, source, simulation);
        PersistChampionHonours(context, source, simulation);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void PersistRoundRows(
        SaveDbContext context,
        SeasonEntity source,
        TeamSimulation simulation)
    {
        foreach (ColorCupTeamRoundPayloadDocument payload in simulation.Payloads)
        {
            context.ColorCupTeamRounds.Add(new ColorCupTeamRoundEntity
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
        TeamSimulation simulation,
        CancellationToken cancellationToken)
    {
        Dictionary<int, ColorCupSelectionEntity> selectionByAthlete = (await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToDictionary(e => e.SaveAthleteId);
        foreach (TeamEvent.TeamLegRanked leg in simulation.Legs)
        {
            PersistSingleLeg(context, source, leg, selectionByAthlete);
        }
    }

    internal static void PersistSingleLeg(
        SaveDbContext context,
        SeasonEntity source,
        TeamEvent.TeamLegRanked leg,
        Dictionary<int, ColorCupSelectionEntity> selectionByAthlete)
    {
        if (!selectionByAthlete.TryGetValue(leg.AthleteId, out ColorCupSelectionEntity? selection))
        {
            throw new InvalidOperationException($"Color Cup team leg for '{leg.Name}' has no selection provenance.");
        }

        int groupNumber = GroupNumberForLeg(selection);
        context.ColorCupTeamGroupStandings.Add(new ColorCupTeamGroupStandingEntity
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
            SportingColor = selection.SportingColor,
            SelectionRank = selection.SelectionRank,
        });
    }

    internal static int GroupNumberForLeg(ColorCupSelectionEntity selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.SelectionRank < 1 || selection.SelectionRank > 4)
        {
            throw new InvalidOperationException($"Color Cup team selection rank {selection.SelectionRank} is out of range.");
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
            context.ColorCupTeamStandings.Add(new ColorCupTeamStandingEntity
            {
                SourceSeasonId = source.Id,
                SourceSeasonNumber = source.SeasonNumber,
                SportingColor = team.TeamId,
                TeamRank = team.TeamRank,
                TeamScoreThousandths = team.TeamScoreThousandths,
                TeamBaseThousandths = team.TeamBaseThousandths,
                GroupWins = team.GroupWins,
                RoundWins = team.RoundWins,
                GroupPlaceCountsJson = JsonSerializer.Serialize(team.GroupPlaceCounts),
                RoundPlaceCountsJson = JsonSerializer.Serialize(team.RoundPlaceCounts),
                Medal = team.TeamRank switch
                {
                    1 => (int)ColorCupMedal.Gold,
                    2 => (int)ColorCupMedal.Silver,
                    3 => (int)ColorCupMedal.Bronze,
                    _ => (int)ColorCupMedal.None,
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
            throw new InvalidOperationException($"Color Cup champion team '{champion.TeamName}' must field exactly four legs.");
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
                Kind = (int)Features.Records.HonourKind.ColorCupTeamChampion,
            });
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
            throw new InvalidOperationException("Color Cup team must not create league stage standings; championship totals are preserved.");
        }

        if (seasons != seasonCountBefore)
        {
            throw new InvalidOperationException("Color Cup team must not create league season standings; championship totals are preserved.");
        }

        if (rounds != roundCountBefore)
        {
            throw new InvalidOperationException("Color Cup team must not create league rounds; Cup history lives in Cup tables only.");
        }

        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths, cancellationToken).ConfigureAwait(false);
        if (lifetime != lifetimeBefore)
        {
            throw new InvalidOperationException("Color Cup team participation must not change career bonus.");
        }

        long effective = await context.AthleteCareers.SumAsync(e => (long)e.CurrentEffectiveBonusThousandths, cancellationToken).ConfigureAwait(false);
        if (effective != effectiveBefore)
        {
            throw new InvalidOperationException("Color Cup team participation must not change effective bonus projections.");
        }

        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths, cancellationToken).ConfigureAwait(false);
        if (championship != championshipBefore)
        {
            throw new InvalidOperationException("Color Cup team must not award league championship points.");
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
        List<ColorCupTeamRoundEntity> rounds = await context.ColorCupTeamRounds
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupTeamStandingEntity> teams = await context.ColorCupTeamStandings
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<HonourEntity> honours = await context.Honours
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ColorCupTeamInvariants.ValidatePersisted(source, rounds, legs, teams, honours, rules);
        if (!string.Equals(ComputeChecksum(simulation.Teams), simulation.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Color Cup team checksum does not match simulated standings.");
        }

        await VerifyPreservationAsync(
            context, stageCountBefore, seasonCountBefore, roundCountBefore,
            lifetimeBefore, effectiveBefore, championshipBefore, cancellationToken).ConfigureAwait(false);
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
            1 => nameof(ColorCupMedal.Gold),
            2 => nameof(ColorCupMedal.Silver),
            3 => nameof(ColorCupMedal.Bronze),
            _ => nameof(ColorCupMedal.None),
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
                Features.Stories.StoryEventType.ColorCupTeamMedal,
                $"color-cup-team-s{source.SeasonNumber}-{medal.ToLowerInvariant()}",
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
                Features.Stories.StoryEventType.ColorCupTeamTitle,
                $"color-cup-team-s{source.SeasonNumber}-title",
                source.SeasonNumber,
                null,
                new Features.Stories.StoryEventPayload(
                    leg.Name,
                    source.SeasonNumber,
                    LeagueName: TeamLeagueName,
                    CupRank: 1,
                    Medal: nameof(ColorCupMedal.Gold),
                    SportingColor: champion.TeamName),
                cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    internal static async Task<RunColorCupTeamResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity source,
        TeamSimulation simulation,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<ColorCupTeamStandingEntity> teams = await context.ColorCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.TeamRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber)
            .ThenBy(e => e.GroupRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupTeamRoundEntity> rounds = await context.ColorCupTeamRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (teams.Count != simulation.Teams.Count || legs.Count != simulation.Legs.Count || rounds.Count != simulation.Payloads.Count)
        {
            throw new InvalidOperationException("Color Cup team persisted result does not match the simulation.");
        }

        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupTeamMember> teamMembers = MapTeamMembers(teams);
        List<ColorCupTeamLegMember> legMembers = MapLegMembers(legs, names);
        ColorCupTeamStandingEntity champion = teams.Single(s => s.TeamRank == 1);
        string championName = ((SportingColor)champion.SportingColor).ToString();
        ColorCupTeamRoundPayloadDocument first = ColorCupTeamRoundPayloadDocument.FromStored(
            rounds.OrderBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber).First().PayloadJson);
        ColorCupTeamRoundPayloadDocument last = ColorCupTeamRoundPayloadDocument.FromStored(
            rounds.OrderBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber).Last().PayloadJson);
        return new RunColorCupTeamResponse(
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
            champion.SportingColor,
            championName,
            teamMembers,
            legMembers);
    }

    internal static List<ColorCupTeamMember> MapTeamMembers(List<ColorCupTeamStandingEntity> teams)
    {
        ArgumentNullException.ThrowIfNull(teams);
        List<ColorCupTeamMember> members = new(teams.Count);
        foreach (ColorCupTeamStandingEntity team in teams.OrderBy(t => t.TeamRank))
        {
            string name = ((SportingColor)team.SportingColor).ToString();
            members.Add(new ColorCupTeamMember(
                team.SportingColor,
                name,
                team.TeamRank,
                team.TeamScoreThousandths,
                team.TeamBaseThousandths,
                team.GroupWins,
                team.RoundWins,
                ((ColorCupMedal)team.Medal).ToString()));
        }

        return members;
    }

    internal static List<ColorCupTeamLegMember> MapLegMembers(
        List<ColorCupTeamGroupStandingEntity> legs,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(names);
        List<ColorCupTeamLegMember> members = new(legs.Count);
        foreach (ColorCupTeamGroupStandingEntity leg in legs.OrderBy(l => l.GroupNumber).ThenBy(l => l.GroupRank))
        {
            names.TryGetValue(leg.SaveAthleteId, out string? name);
            string color = ((SportingColor)leg.SportingColor).ToString();
            members.Add(new ColorCupTeamLegMember(
                leg.SaveAthleteId,
                name ?? $"Athlete {leg.SaveAthleteId}",
                color,
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
