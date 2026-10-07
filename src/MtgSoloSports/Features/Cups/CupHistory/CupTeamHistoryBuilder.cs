using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.CupHistory;

/// <summary>
/// Pure aggregation of one team's persisted Cup rows into its history. The
/// Color and Type slices load their own tables and hand over neutral rows, so
/// both Cups share one set of rules: a squad must have exactly the Cup's team
/// size, every leg must belong to a squad member and every standing to a
/// selected season. Violations abort; nothing is repaired.
/// </summary>
public static class CupTeamHistoryBuilder
{
    private const string NoMedal = "None";

    public sealed record SelectionRow(
        int SeasonNumber,
        int AthleteId,
        int SelectionRank,
        int FinalRatingThousandths,
        int BonusNormThousandths,
        int PerformanceNormThousandths,
        int FormNormThousandths,
        int PrestigeNormThousandths,
        string? Reason);

    public sealed record StandingRow(
        int SeasonNumber,
        int TeamRank,
        int TeamScoreThousandths,
        int TeamBaseThousandths,
        int GroupWins,
        int RoundWins,
        string Medal,
        int TournamentPhase = 0,
        int QualificationGroup = 0,
        bool IsHonourEligible = true,
        bool QualifiedForFinal = false,
        bool EliminatedInQualification = false);

    public sealed record LegRow(
        int SeasonNumber,
        int AthleteId,
        int GroupNumber,
        int GroupRank,
        int GroupScoreThousandths,
        int BaseScoreThousandths,
        int RoundWins);

    public sealed record IndividualRow(int SeasonNumber, int AthleteId, int CupRank, int CupScoreThousandths, string Medal);

    /// <summary>
    /// Per-season facts about the whole edition. <see cref="IndividualComplete"/>
    /// is always true for a Cup without an individual event.
    /// </summary>
    public sealed record SeasonFacts(int TeamCount, bool AnyRoundPlayed, bool IndividualComplete);

    public sealed record Input(
        Guid SaveId,
        string Cup,
        string TeamKey,
        string TeamName,
        int TeamSize,
        IReadOnlyList<SelectionRow> Selections,
        IReadOnlyList<StandingRow> Standings,
        IReadOnlyList<LegRow> Legs,
        IReadOnlyList<IndividualRow> Individuals,
        IReadOnlyDictionary<int, SeasonFacts> Seasons,
        IReadOnlyDictionary<int, SaveAthleteEntity> Athletes);

    public static CupTeamHistoryResponse Build(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Selections.Count == 0)
        {
            throw new CupTeamHistoryNotFoundException($"{input.Cup} Cup team '{input.TeamKey}' has never been selected.");
        }

        List<int> seasonNumbers = input.Selections
            .Select(s => s.SeasonNumber)
            .Distinct()
            .OrderByDescending(n => n)
            .ToList();
        StandingRow? orphan = input.Standings.FirstOrDefault(s => !seasonNumbers.Contains(s.SeasonNumber));
        if (orphan is not null)
        {
            throw new InvalidOperationException(
                $"{input.Cup} Cup team '{input.TeamKey}' has a Season {orphan.SeasonNumber} standing without a selection.");
        }

        return new CupTeamHistoryResponse(
            input.SaveId,
            input.Cup,
            input.TeamKey,
            input.TeamName,
            BuildHonours(input, seasonNumbers.Count),
            seasonNumbers.Select(number => BuildSeason(input, number)).ToList(),
            BuildRoster(input),
            input.Individuals
                .Where(i => !string.Equals(i.Medal, NoMedal, StringComparison.Ordinal))
                .OrderByDescending(i => i.SeasonNumber)
                .ThenBy(i => i.CupRank)
                .Select(i => new CupTeamHistoryResponse.IndividualMedal(i.SeasonNumber, i.AthleteId, Athlete(input, i.AthleteId).Name, i.Medal))
                .ToList());
    }

    private static SaveAthleteEntity Athlete(Input input, int athleteId)
    {
        if (!input.Athletes.TryGetValue(athleteId, out SaveAthleteEntity? athlete))
        {
            throw new InvalidOperationException($"{input.Cup} Cup team '{input.TeamKey}' references unknown athlete {athleteId}.");
        }

        return athlete;
    }

    private static CupTeamHistoryResponse.TeamHonours BuildHonours(Input input, int editions)
    {
        // Official honours only: qualification group tables never produce
        // medals or titles, even for group winners.
        List<StandingRow> official = input.Standings.Where(s => s.IsHonourEligible).ToList();
        StandingRow? best = official
            .OrderBy(s => s.TeamRank)
            .ThenByDescending(s => s.SeasonNumber)
            .FirstOrDefault();
        return new CupTeamHistoryResponse.TeamHonours(
            editions,
            official.Count(s => s.TeamRank == 1),
            official.Count(s => s.TeamRank == 2),
            official.Count(s => s.TeamRank == 3),
            best?.TeamRank,
            best?.SeasonNumber,
            official.Sum(s => s.GroupWins),
            official.Sum(s => s.RoundWins),
            official.Sum(s => (long)s.TeamScoreThousandths));
    }

    private static CupTeamHistoryResponse.Season BuildSeason(Input input, int seasonNumber)
    {
        List<SelectionRow> squad = input.Selections
            .Where(s => s.SeasonNumber == seasonNumber)
            .OrderBy(s => s.SelectionRank)
            .ToList();
        if (squad.Count != input.TeamSize)
        {
            throw new InvalidOperationException(
                $"{input.Cup} Cup team '{input.TeamKey}' has {squad.Count} selected athletes in Season {seasonNumber}; expected {input.TeamSize}.");
        }

        if (!input.Seasons.TryGetValue(seasonNumber, out SeasonFacts? facts))
        {
            throw new InvalidOperationException(
                $"{input.Cup} Cup team '{input.TeamKey}' has no edition facts for Season {seasonNumber}.");
        }

        HashSet<int> squadIds = squad.Select(s => s.AthleteId).ToHashSet();
        List<LegRow> legs = input.Legs.Where(l => l.SeasonNumber == seasonNumber).ToList();
        LegRow? stray = legs.FirstOrDefault(l => !squadIds.Contains(l.AthleteId));
        if (stray is not null)
        {
            throw new InvalidOperationException(
                $"{input.Cup} Cup team '{input.TeamKey}' has a Season {seasonNumber} leg for athlete {stray.AthleteId}, who is not in the squad.");
        }

        StandingRow? standing = input.Standings.SingleOrDefault(s => s.SeasonNumber == seasonNumber);
        bool complete = standing is not null && facts.IndividualComplete;
        bool anyPlayed = facts.AnyRoundPlayed || standing is not null || legs.Count > 0;
        List<CupTeamHistoryResponse.SquadMember> members = squad
            .Select(pick => BuildMember(input, pick, legs, facts.TeamCount))
            .ToList();
        return new CupTeamHistoryResponse.Season(
            seasonNumber,
            CupEditionState.For(anyPlayed, complete),
            facts.TeamCount,
            standing?.TeamRank,
            standing?.Medal,
            standing?.TeamScoreThousandths,
            standing?.TeamBaseThousandths,
            standing?.GroupWins,
            standing?.RoundWins,
            members,
            TournamentStageLabel(standing),
            standing?.TournamentPhase,
            standing?.TournamentPhase == 1 ? standing?.QualificationGroup : null,
            standing?.QualifiedForFinal ?? false,
            standing?.EliminatedInQualification ?? false);
    }

    private static CupTeamHistoryResponse.SquadMember BuildMember(
        Input input,
        SelectionRow pick,
        List<LegRow> seasonLegs,
        int teamCount)
    {
        SaveAthleteEntity athlete = Athlete(input, pick.AthleteId);
        LegRow? leg = seasonLegs.SingleOrDefault(l => l.AthleteId == pick.AthleteId);
        IndividualRow? individual = input.Individuals
            .SingleOrDefault(i => i.SeasonNumber == pick.SeasonNumber && i.AthleteId == pick.AthleteId);
        return new CupTeamHistoryResponse.SquadMember(
            athlete.Id,
            athlete.Name,
            athlete.ImageUrl,
            pick.SelectionRank,
            pick.FinalRatingThousandths,
            pick.BonusNormThousandths,
            pick.PerformanceNormThousandths,
            pick.FormNormThousandths,
            pick.PrestigeNormThousandths,
            pick.Reason,
            leg is null
                ? null
                : new CupTeamHistoryResponse.Leg(
                    leg.GroupNumber,
                    leg.GroupRank,
                    teamCount,
                    leg.GroupScoreThousandths,
                    leg.BaseScoreThousandths,
                    leg.RoundWins),
            individual is null
                ? null
                : new CupTeamHistoryResponse.Individual(individual.CupRank, individual.CupScoreThousandths, individual.Medal));
    }

    internal static string? TournamentStageLabel(StandingRow? standing)
    {
        if (standing is null)
        {
            return null;
        }

        if (standing.TournamentPhase == 2)
        {
            return "Final";
        }

        if (standing.TournamentPhase == 1)
        {
            return $"Qualification Group {GroupLetter(standing.QualificationGroup)}";
        }

        return null;
    }

    internal static string GroupLetter(int qualificationGroup)
    {
        if (qualificationGroup < 1)
        {
            return qualificationGroup.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        System.Text.StringBuilder builder = new();
        int value = qualificationGroup;
        while (value > 0)
        {
            value--;
            builder.Insert(0, (char)('A' + (value % 26)));
            value /= 26;
        }

        return builder.ToString();
    }

    private static List<CupTeamHistoryResponse.RosterEntry> BuildRoster(Input input)
    {
        return input.Selections
            .GroupBy(s => s.AthleteId)
            .Select(picks =>
            {
                SaveAthleteEntity athlete = Athlete(input, picks.Key);
                List<LegRow> legs = input.Legs.Where(l => l.AthleteId == picks.Key).ToList();
                return new CupTeamHistoryResponse.RosterEntry(
                    athlete.Id,
                    athlete.Name,
                    athlete.ImageUrl,
                    picks.Count(),
                    picks.Min(p => p.SeasonNumber),
                    picks.Max(p => p.SeasonNumber),
                    legs.Sum(l => (long)l.GroupScoreThousandths),
                    legs.Count == 0 ? null : legs.Min(l => l.GroupRank),
                    picks.Min(p => p.SelectionRank));
            })
            .OrderByDescending(r => r.Caps)
            .ThenByDescending(r => r.TotalLegScoreThousandths)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }
}
