namespace MtgSoloSports.Features.Records;

/// <summary>
/// Pure fixed-point scoring-record computation from persisted results only.
/// Reads never touch RNG, clock or database ordering for sporting values.
/// All arithmetic is integer-only; holder ordering is deterministic
/// (athlete name ascending then athlete id ascending; teams by team name).
/// Tie behavior: all athletes/teams sharing the maximum value are joint
/// holders ordered deterministically; a tie never replaces the holder list,
/// only an outright higher value does. Vacant records (no occurrences)
/// report no holders. Corrupt negative values or identities abort.
/// Historical ownership is by persisted athlete id, never by current
/// league/team membership, so later membership changes cannot rewrite it.
/// </summary>
public static class ScoreRecordCalculator
{
    public sealed record LeagueRoundOccurrence(
        string LeagueScope,
        int SeasonNumber,
        int StageNumber,
        int RoundNumber,
        int SaveAthleteId,
        int FinalThousandths,
        string LeagueName);

    public sealed record LeagueStageOccurrence(
        string LeagueScope,
        int SeasonNumber,
        int StageNumber,
        int SaveAthleteId,
        int StageScoreThousandths,
        string LeagueName);

    public sealed record LeaguePointsOccurrence(
        string LeagueScope,
        int SeasonNumber,
        int SaveAthleteId,
        int ChampionshipPointsThousandths,
        string LeagueName);

    public sealed record CupIndividualRoundOccurrence(
        int SeasonNumber,
        int RoundNumber,
        int SaveAthleteId,
        int FinalThousandths);

    public sealed record CupIndividualStageOccurrence(
        int SeasonNumber,
        int SaveAthleteId,
        int CupScoreThousandths);

    public sealed record QualifierRoundOccurrence(
        int FromSeasonNumber,
        int ToSeasonNumber,
        int RoundNumber,
        int SaveAthleteId,
        int FinalThousandths);

    public sealed record QualifierStageOccurrence(
        int FromSeasonNumber,
        int ToSeasonNumber,
        int SaveAthleteId,
        int QualifierScoreThousandths);

    public sealed record TeamLegRoundOccurrence(
        int SeasonNumber,
        int GroupNumber,
        int RoundNumber,
        int SaveAthleteId,
        string TeamKey,
        int FinalThousandths);

    public sealed record TeamLegStageOccurrence(
        int SeasonNumber,
        int GroupNumber,
        int SaveAthleteId,
        string TeamKey,
        int GroupScoreThousandths);

    public sealed record TeamTotalOccurrence(
        int SeasonNumber,
        string TeamKey,
        int TeamScoreThousandths);

    public sealed record TeamRoundOccurrence(
        int SeasonNumber,
        int RoundNumber,
        string TeamKey,
        int TeamRoundTotalThousandths);

    public sealed record ScoringHolder(
        int? SaveAthleteId,
        string TeamKey,
        int Value,
        int SeasonNumber,
        string Competition,
        string? LeagueName,
        int? StageNumber,
        int? RoundNumber,
        int? GroupNumber);

    public sealed record ScoringRecordResult(
        string RecordKey,
        int Value,
        IReadOnlyList<ScoringHolder> Holders);

    public static IReadOnlyList<ScoringRecordResult> ComputeAll(
        IReadOnlyDictionary<int, string> athleteNames,
        IReadOnlyList<LeagueRoundOccurrence> leagueRounds,
        IReadOnlyList<LeagueStageOccurrence> leagueStages,
        IReadOnlyList<LeaguePointsOccurrence> leaguePoints,
        IReadOnlyList<CupIndividualRoundOccurrence> colourRounds,
        IReadOnlyList<CupIndividualStageOccurrence> colourStages,
        IReadOnlyList<QualifierRoundOccurrence> qualifierRounds,
        IReadOnlyList<QualifierStageOccurrence> qualifierStages,
        IReadOnlyList<TeamLegRoundOccurrence> colourLegRounds,
        IReadOnlyList<TeamLegStageOccurrence> colourLegStages,
        IReadOnlyList<TeamTotalOccurrence> colourTotals,
        IReadOnlyList<TeamRoundOccurrence> colourTeamRounds,
        IReadOnlyList<TeamLegRoundOccurrence> typeLegRounds,
        IReadOnlyList<TeamLegStageOccurrence> typeLegStages,
        IReadOnlyList<TeamTotalOccurrence> typeTotals,
        IReadOnlyList<TeamRoundOccurrence> typeTeamRounds)
    {
        ValidateInputs(
            athleteNames, leagueRounds, leagueStages, leaguePoints,
            colourRounds, colourStages, qualifierRounds, qualifierStages,
            colourLegRounds, colourLegStages, colourTotals, colourTeamRounds,
            typeLegRounds, typeLegStages, typeTotals, typeTeamRounds);
        List<ScoringRecordResult> results = [];
        AddLeagueRecords(results, athleteNames, leagueRounds, leagueStages, leaguePoints);
        AddCupRecords(results, athleteNames, colourRounds, colourStages, qualifierRounds, qualifierStages);
        AddTeamRecords(
            results, athleteNames,
            colourLegRounds, colourLegStages, colourTotals, colourTeamRounds,
            typeLegRounds, typeLegStages, typeTotals, typeTeamRounds);
        return results;
    }

    internal static void ValidateInputs(
        IReadOnlyDictionary<int, string> athleteNames,
        IReadOnlyList<LeagueRoundOccurrence> leagueRounds,
        IReadOnlyList<LeagueStageOccurrence> leagueStages,
        IReadOnlyList<LeaguePointsOccurrence> leaguePoints,
        IReadOnlyList<CupIndividualRoundOccurrence> colourRounds,
        IReadOnlyList<CupIndividualStageOccurrence> colourStages,
        IReadOnlyList<QualifierRoundOccurrence> qualifierRounds,
        IReadOnlyList<QualifierStageOccurrence> qualifierStages,
        IReadOnlyList<TeamLegRoundOccurrence> colourLegRounds,
        IReadOnlyList<TeamLegStageOccurrence> colourLegStages,
        IReadOnlyList<TeamTotalOccurrence> colourTotals,
        IReadOnlyList<TeamRoundOccurrence> colourTeamRounds,
        IReadOnlyList<TeamLegRoundOccurrence> typeLegRounds,
        IReadOnlyList<TeamLegStageOccurrence> typeLegStages,
        IReadOnlyList<TeamTotalOccurrence> typeTotals,
        IReadOnlyList<TeamRoundOccurrence> typeTeamRounds)
    {
        ArgumentNullException.ThrowIfNull(athleteNames);
        ArgumentNullException.ThrowIfNull(leagueRounds);
        ArgumentNullException.ThrowIfNull(leagueStages);
        ArgumentNullException.ThrowIfNull(leaguePoints);
        ArgumentNullException.ThrowIfNull(colourRounds);
        ArgumentNullException.ThrowIfNull(colourStages);
        ArgumentNullException.ThrowIfNull(qualifierRounds);
        ArgumentNullException.ThrowIfNull(qualifierStages);
        ArgumentNullException.ThrowIfNull(colourLegRounds);
        ArgumentNullException.ThrowIfNull(colourLegStages);
        ArgumentNullException.ThrowIfNull(colourTotals);
        ArgumentNullException.ThrowIfNull(colourTeamRounds);
        ArgumentNullException.ThrowIfNull(typeLegRounds);
        ArgumentNullException.ThrowIfNull(typeLegStages);
        ArgumentNullException.ThrowIfNull(typeTotals);
        ArgumentNullException.ThrowIfNull(typeTeamRounds);
    }

    internal static void AddLeagueRecords(
        List<ScoringRecordResult> results,
        IReadOnlyDictionary<int, string> athleteNames,
        IReadOnlyList<LeagueRoundOccurrence> leagueRounds,
        IReadOnlyList<LeagueStageOccurrence> leagueStages,
        IReadOnlyList<LeaguePointsOccurrence> leaguePoints)
    {
        foreach (string scope in ScoreRecordKey.LeagueScopes)
        {
            results.Add(BuildLeagueRound(scope, athleteNames, leagueRounds));
        }

        foreach (string scope in ScoreRecordKey.LeagueScopes)
        {
            results.Add(BuildLeagueStage(scope, athleteNames, leagueStages));
        }

        foreach (string scope in ScoreRecordKey.LeagueScopes)
        {
            results.Add(BuildLeaguePoints(scope, athleteNames, leaguePoints));
        }
    }

    internal static void AddCupRecords(
        List<ScoringRecordResult> results,
        IReadOnlyDictionary<int, string> athleteNames,
        IReadOnlyList<CupIndividualRoundOccurrence> colourRounds,
        IReadOnlyList<CupIndividualStageOccurrence> colourStages,
        IReadOnlyList<QualifierRoundOccurrence> qualifierRounds,
        IReadOnlyList<QualifierStageOccurrence> qualifierStages)
    {
        results.Add(BuildCupIndividualRound(athleteNames, colourRounds));
        results.Add(BuildCupIndividualStage(athleteNames, colourStages));
        results.Add(BuildQualifierRound(athleteNames, qualifierRounds));
        results.Add(BuildQualifierStage(athleteNames, qualifierStages));
    }

    internal static void AddTeamRecords(
        List<ScoringRecordResult> results,
        IReadOnlyDictionary<int, string> athleteNames,
        IReadOnlyList<TeamLegRoundOccurrence> colourLegRounds,
        IReadOnlyList<TeamLegStageOccurrence> colourLegStages,
        IReadOnlyList<TeamTotalOccurrence> colourTotals,
        IReadOnlyList<TeamRoundOccurrence> colourTeamRounds,
        IReadOnlyList<TeamLegRoundOccurrence> typeLegRounds,
        IReadOnlyList<TeamLegStageOccurrence> typeLegStages,
        IReadOnlyList<TeamTotalOccurrence> typeTotals,
        IReadOnlyList<TeamRoundOccurrence> typeTeamRounds)
    {
        results.Add(BuildTeamLegRound(ScoreRecordKey.ColourCupTeamLegRoundBest, "Colour Cup team", athleteNames, colourLegRounds));
        results.Add(BuildTeamLegStage(ScoreRecordKey.ColourCupTeamLegStageBest, "Colour Cup team", athleteNames, colourLegStages));
        results.Add(BuildTeamTotal(ScoreRecordKey.ColourCupTeamTotalBest, athleteNames, colourTotals));
        results.Add(BuildTeamRound(ScoreRecordKey.ColourCupTeamRoundBest, athleteNames, colourTeamRounds));
        results.Add(BuildTeamLegRound(ScoreRecordKey.TypeCupTeamLegRoundBest, "Type Cup team", athleteNames, typeLegRounds));
        results.Add(BuildTeamLegStage(ScoreRecordKey.TypeCupTeamLegStageBest, "Type Cup team", athleteNames, typeLegStages));
        results.Add(BuildTeamTotal(ScoreRecordKey.TypeCupTeamTotalBest, athleteNames, typeTotals));
        results.Add(BuildTeamRound(ScoreRecordKey.TypeCupTeamRoundBest, athleteNames, typeTeamRounds));
    }

    internal static ScoringRecordResult BuildLeagueRound(
        string scope,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyList<LeagueRoundOccurrence> occurrences)
    {
        string key = ScoreRecordKey.LeagueSingleRound(scope);
        List<LeagueRoundOccurrence> scoped = occurrences.Where(o => string.Equals(o.LeagueScope, scope, StringComparison.Ordinal)).ToList();
        foreach (LeagueRoundOccurrence o in scoped)
        {
            ValidateAthlete(o.SaveAthleteId, names);
            ValidateNonNegative(o.FinalThousandths, o.SaveAthleteId);
            ValidateSeasonStageRound(o.SeasonNumber, o.StageNumber, o.RoundNumber);
        }

        if (scoped.Count == 0)
        {
            return new ScoringRecordResult(key, 0, []);
        }

        int maximum = scoped.Max(o => o.FinalThousandths);
        Dictionary<int, LeagueRoundOccurrence> earliestByAthlete = EarliestPerAthlete(
            scoped.Where(o => o.FinalThousandths == maximum).ToList(),
            static o => o.SaveAthleteId,
            static occurrences =>
            {
                return occurrences
                    .OrderBy(o => o.SeasonNumber)
                    .ThenBy(o => o.StageNumber)
                    .ThenBy(o => o.RoundNumber)
                    .First();
            });
        List<ScoringHolder> holders = earliestByAthlete.Values
            .OrderBy(o => AthleteDisplayName(names, o.SaveAthleteId), StringComparer.Ordinal)
            .ThenBy(o => o.SaveAthleteId)
            .Select(o => new ScoringHolder(
                o.SaveAthleteId,
                string.Empty,
                maximum,
                o.SeasonNumber,
                o.LeagueName,
                o.LeagueName,
                o.StageNumber,
                o.RoundNumber,
                null))
            .ToList();
        return new ScoringRecordResult(key, maximum, holders);
    }

    internal static ScoringRecordResult BuildLeagueStage(
        string scope,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyList<LeagueStageOccurrence> occurrences)
    {
        string key = ScoreRecordKey.LeagueStageBest(scope);
        List<LeagueStageOccurrence> scoped = occurrences.Where(o => string.Equals(o.LeagueScope, scope, StringComparison.Ordinal)).ToList();
        foreach (LeagueStageOccurrence o in scoped)
        {
            ValidateAthlete(o.SaveAthleteId, names);
            ValidateNonNegative(o.StageScoreThousandths, o.SaveAthleteId);
            if (o.SeasonNumber < 1 || o.StageNumber < 1)
            {
                throw new InvalidOperationException($"League stage occurrence has corrupt season/stage {o.SeasonNumber}/{o.StageNumber}.");
            }
        }

        if (scoped.Count == 0)
        {
            return new ScoringRecordResult(key, 0, []);
        }

        int maximum = scoped.Max(o => o.StageScoreThousandths);
        Dictionary<int, LeagueStageOccurrence> earliest = EarliestPerAthlete(
            scoped.Where(o => o.StageScoreThousandths == maximum).ToList(),
            static o => o.SaveAthleteId,
            static list => list.OrderBy(o => o.SeasonNumber).ThenBy(o => o.StageNumber).First());
        List<ScoringHolder> holders = earliest.Values
            .OrderBy(o => AthleteDisplayName(names, o.SaveAthleteId), StringComparer.Ordinal)
            .ThenBy(o => o.SaveAthleteId)
            .Select(o => new ScoringHolder(
                o.SaveAthleteId, string.Empty, maximum, o.SeasonNumber, o.LeagueName, o.LeagueName, o.StageNumber, null, null))
            .ToList();
        return new ScoringRecordResult(key, maximum, holders);
    }

    internal static ScoringRecordResult BuildLeaguePoints(
        string scope,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyList<LeaguePointsOccurrence> occurrences)
    {
        string key = ScoreRecordKey.LeaguePointsBest(scope);
        List<LeaguePointsOccurrence> scoped = occurrences.Where(o => string.Equals(o.LeagueScope, scope, StringComparison.Ordinal)).ToList();
        foreach (LeaguePointsOccurrence o in scoped)
        {
            ValidateAthlete(o.SaveAthleteId, names);
            ValidateNonNegative(o.ChampionshipPointsThousandths, o.SaveAthleteId);
            if (o.SeasonNumber < 1)
            {
                throw new InvalidOperationException($"League points occurrence has corrupt season {o.SeasonNumber}.");
            }
        }

        if (scoped.Count == 0)
        {
            return new ScoringRecordResult(key, 0, []);
        }

        int maximum = scoped.Max(o => o.ChampionshipPointsThousandths);
        Dictionary<int, LeaguePointsOccurrence> earliest = EarliestPerAthlete(
            scoped.Where(o => o.ChampionshipPointsThousandths == maximum).ToList(),
            static o => o.SaveAthleteId,
            static list => list.OrderBy(o => o.SeasonNumber).First());
        List<ScoringHolder> holders = earliest.Values
            .OrderBy(o => AthleteDisplayName(names, o.SaveAthleteId), StringComparer.Ordinal)
            .ThenBy(o => o.SaveAthleteId)
            .Select(o => new ScoringHolder(
                o.SaveAthleteId, string.Empty, maximum, o.SeasonNumber, o.LeagueName, o.LeagueName, null, null, null))
            .ToList();
        return new ScoringRecordResult(key, maximum, holders);
    }

    internal static ScoringRecordResult BuildCupIndividualRound(
        IReadOnlyDictionary<int, string> names,
        IReadOnlyList<CupIndividualRoundOccurrence> occurrences)
    {
        const string key = ScoreRecordKey.ColourCupIndividualRoundBest;
        foreach (CupIndividualRoundOccurrence o in occurrences)
        {
            ValidateAthlete(o.SaveAthleteId, names);
            ValidateNonNegative(o.FinalThousandths, o.SaveAthleteId);
            ValidateSeasonStageRound(o.SeasonNumber, 1, o.RoundNumber);
        }

        if (occurrences.Count == 0)
        {
            return new ScoringRecordResult(key, 0, []);
        }

        int maximum = occurrences.Max(o => o.FinalThousandths);
        Dictionary<int, CupIndividualRoundOccurrence> earliest = EarliestPerAthlete(
            occurrences.Where(o => o.FinalThousandths == maximum).ToList(),
            static o => o.SaveAthleteId,
            static list => list.OrderBy(o => o.SeasonNumber).ThenBy(o => o.RoundNumber).First());
        List<ScoringHolder> holders = earliest.Values
            .OrderBy(o => AthleteDisplayName(names, o.SaveAthleteId), StringComparer.Ordinal)
            .ThenBy(o => o.SaveAthleteId)
            .Select(o => new ScoringHolder(
                o.SaveAthleteId, string.Empty, maximum, o.SeasonNumber, "Colour Cup individual", null, null, o.RoundNumber, null))
            .ToList();
        return new ScoringRecordResult(key, maximum, holders);
    }

    internal static ScoringRecordResult BuildCupIndividualStage(
        IReadOnlyDictionary<int, string> names,
        IReadOnlyList<CupIndividualStageOccurrence> occurrences)
    {
        const string key = ScoreRecordKey.ColourCupIndividualStageBest;
        foreach (CupIndividualStageOccurrence o in occurrences)
        {
            ValidateAthlete(o.SaveAthleteId, names);
            ValidateNonNegative(o.CupScoreThousandths, o.SaveAthleteId);
            if (o.SeasonNumber < 1)
            {
                throw new InvalidOperationException($"Cup stage occurrence has corrupt season {o.SeasonNumber}.");
            }
        }

        if (occurrences.Count == 0)
        {
            return new ScoringRecordResult(key, 0, []);
        }

        int maximum = occurrences.Max(o => o.CupScoreThousandths);
        Dictionary<int, CupIndividualStageOccurrence> earliest = EarliestPerAthlete(
            occurrences.Where(o => o.CupScoreThousandths == maximum).ToList(),
            static o => o.SaveAthleteId,
            static list => list.OrderBy(o => o.SeasonNumber).First());
        List<ScoringHolder> holders = earliest.Values
            .OrderBy(o => AthleteDisplayName(names, o.SaveAthleteId), StringComparer.Ordinal)
            .ThenBy(o => o.SaveAthleteId)
            .Select(o => new ScoringHolder(
                o.SaveAthleteId, string.Empty, maximum, o.SeasonNumber, "Colour Cup individual", null, null, null, null))
            .ToList();
        return new ScoringRecordResult(key, maximum, holders);
    }

    internal static ScoringRecordResult BuildQualifierRound(
        IReadOnlyDictionary<int, string> names,
        IReadOnlyList<QualifierRoundOccurrence> occurrences)
    {
        const string key = ScoreRecordKey.QualifierSingleRoundBest;
        foreach (QualifierRoundOccurrence o in occurrences)
        {
            ValidateAthlete(o.SaveAthleteId, names);
            ValidateNonNegative(o.FinalThousandths, o.SaveAthleteId);
            if (o.FromSeasonNumber < 1 || o.ToSeasonNumber < 1 || o.RoundNumber < 1)
            {
                throw new InvalidOperationException("Qualifier round occurrence has corrupt identity.");
            }
        }

        if (occurrences.Count == 0)
        {
            return new ScoringRecordResult(key, 0, []);
        }

        int maximum = occurrences.Max(o => o.FinalThousandths);
        Dictionary<int, QualifierRoundOccurrence> earliest = EarliestPerAthlete(
            occurrences.Where(o => o.FinalThousandths == maximum).ToList(),
            static o => o.SaveAthleteId,
            static list => list.OrderBy(o => o.FromSeasonNumber).ThenBy(o => o.RoundNumber).First());
        List<ScoringHolder> holders = earliest.Values
            .OrderBy(o => AthleteDisplayName(names, o.SaveAthleteId), StringComparer.Ordinal)
            .ThenBy(o => o.SaveAthleteId)
            .Select(o => new ScoringHolder(
                o.SaveAthleteId, string.Empty, maximum, o.FromSeasonNumber, "Qualifier", null, null, o.RoundNumber, null))
            .ToList();
        return new ScoringRecordResult(key, maximum, holders);
    }

    internal static ScoringRecordResult BuildQualifierStage(
        IReadOnlyDictionary<int, string> names,
        IReadOnlyList<QualifierStageOccurrence> occurrences)
    {
        const string key = ScoreRecordKey.QualifierStageBest;
        foreach (QualifierStageOccurrence o in occurrences)
        {
            ValidateAthlete(o.SaveAthleteId, names);
            ValidateNonNegative(o.QualifierScoreThousandths, o.SaveAthleteId);
            if (o.FromSeasonNumber < 1 || o.ToSeasonNumber < 1)
            {
                throw new InvalidOperationException("Qualifier stage occurrence has corrupt identity.");
            }
        }

        if (occurrences.Count == 0)
        {
            return new ScoringRecordResult(key, 0, []);
        }

        int maximum = occurrences.Max(o => o.QualifierScoreThousandths);
        Dictionary<int, QualifierStageOccurrence> earliest = EarliestPerAthlete(
            occurrences.Where(o => o.QualifierScoreThousandths == maximum).ToList(),
            static o => o.SaveAthleteId,
            static list => list.OrderBy(o => o.FromSeasonNumber).First());
        List<ScoringHolder> holders = earliest.Values
            .OrderBy(o => AthleteDisplayName(names, o.SaveAthleteId), StringComparer.Ordinal)
            .ThenBy(o => o.SaveAthleteId)
            .Select(o => new ScoringHolder(
                o.SaveAthleteId, string.Empty, maximum, o.FromSeasonNumber, "Qualifier", null, null, null, null))
            .ToList();
        return new ScoringRecordResult(key, maximum, holders);
    }

    internal static ScoringRecordResult BuildTeamLegRound(
        string recordKey,
        string competition,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyList<TeamLegRoundOccurrence> occurrences)
    {
        foreach (TeamLegRoundOccurrence o in occurrences)
        {
            ValidateAthlete(o.SaveAthleteId, names);
            ValidateNonNegative(o.FinalThousandths, o.SaveAthleteId);
            ValidateTeamKey(o.TeamKey);
            if (o.SeasonNumber < 1 || o.GroupNumber < 1 || o.RoundNumber < 1)
            {
                throw new InvalidOperationException("Team leg round occurrence has corrupt identity.");
            }
        }

        if (occurrences.Count == 0)
        {
            return new ScoringRecordResult(recordKey, 0, []);
        }

        int maximum = occurrences.Max(o => o.FinalThousandths);
        Dictionary<int, TeamLegRoundOccurrence> earliest = EarliestPerAthlete(
            occurrences.Where(o => o.FinalThousandths == maximum).ToList(),
            static o => o.SaveAthleteId,
            static list => list.OrderBy(o => o.SeasonNumber).ThenBy(o => o.GroupNumber).ThenBy(o => o.RoundNumber).First());
        List<ScoringHolder> holders = earliest.Values
            .OrderBy(o => AthleteDisplayName(names, o.SaveAthleteId), StringComparer.Ordinal)
            .ThenBy(o => o.SaveAthleteId)
            .Select(o => new ScoringHolder(
                o.SaveAthleteId, o.TeamKey, maximum, o.SeasonNumber, competition, null, null, o.RoundNumber, o.GroupNumber))
            .ToList();
        return new ScoringRecordResult(recordKey, maximum, holders);
    }

    internal static ScoringRecordResult BuildTeamLegStage(
        string recordKey,
        string competition,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyList<TeamLegStageOccurrence> occurrences)
    {
        foreach (TeamLegStageOccurrence o in occurrences)
        {
            ValidateAthlete(o.SaveAthleteId, names);
            ValidateNonNegative(o.GroupScoreThousandths, o.SaveAthleteId);
            ValidateTeamKey(o.TeamKey);
            if (o.SeasonNumber < 1 || o.GroupNumber < 1)
            {
                throw new InvalidOperationException("Team leg stage occurrence has corrupt identity.");
            }
        }

        if (occurrences.Count == 0)
        {
            return new ScoringRecordResult(recordKey, 0, []);
        }

        int maximum = occurrences.Max(o => o.GroupScoreThousandths);
        Dictionary<int, TeamLegStageOccurrence> earliest = EarliestPerAthlete(
            occurrences.Where(o => o.GroupScoreThousandths == maximum).ToList(),
            static o => o.SaveAthleteId,
            static list => list.OrderBy(o => o.SeasonNumber).ThenBy(o => o.GroupNumber).First());
        List<ScoringHolder> holders = earliest.Values
            .OrderBy(o => AthleteDisplayName(names, o.SaveAthleteId), StringComparer.Ordinal)
            .ThenBy(o => o.SaveAthleteId)
            .Select(o => new ScoringHolder(
                o.SaveAthleteId, o.TeamKey, maximum, o.SeasonNumber, competition, null, null, null, o.GroupNumber))
            .ToList();
        return new ScoringRecordResult(recordKey, maximum, holders);
    }

    internal static ScoringRecordResult BuildTeamTotal(
        string recordKey,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyList<TeamTotalOccurrence> occurrences)
    {
        _ = names;
        foreach (TeamTotalOccurrence o in occurrences)
        {
            ValidateTeamKey(o.TeamKey);
            ValidateNonNegative(o.TeamScoreThousandths, 0);
            if (o.SeasonNumber < 1)
            {
                throw new InvalidOperationException("Team total occurrence has corrupt season.");
            }
        }

        if (occurrences.Count == 0)
        {
            return new ScoringRecordResult(recordKey, 0, []);
        }

        string competition = string.Equals(recordKey, ScoreRecordKey.ColourCupTeamTotalBest, StringComparison.Ordinal)
            ? "Colour Cup team"
            : "Type Cup team";
        int maximum = occurrences.Max(o => o.TeamScoreThousandths);
        Dictionary<string, TeamTotalOccurrence> earliestByTeam = [];
        foreach (TeamTotalOccurrence o in occurrences.Where(o => o.TeamScoreThousandths == maximum))
        {
            if (!earliestByTeam.TryGetValue(o.TeamKey, out TeamTotalOccurrence? existing) ||
                o.SeasonNumber < existing.SeasonNumber)
            {
                earliestByTeam[o.TeamKey] = o;
            }
        }

        List<ScoringHolder> holders = earliestByTeam.Values
            .OrderBy(o => o.TeamKey, StringComparer.Ordinal)
            .Select(o => new ScoringHolder(null, o.TeamKey, maximum, o.SeasonNumber, competition, null, null, null, null))
            .ToList();
        return new ScoringRecordResult(recordKey, maximum, holders);
    }

    internal static ScoringRecordResult BuildTeamRound(
        string recordKey,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyList<TeamRoundOccurrence> occurrences)
    {
        _ = names;
        foreach (TeamRoundOccurrence o in occurrences)
        {
            ValidateTeamKey(o.TeamKey);
            ValidateNonNegative(o.TeamRoundTotalThousandths, 0);
            if (o.SeasonNumber < 1 || o.RoundNumber < 1)
            {
                throw new InvalidOperationException("Team round occurrence has corrupt identity.");
            }
        }

        if (occurrences.Count == 0)
        {
            return new ScoringRecordResult(recordKey, 0, []);
        }

        string competition = string.Equals(recordKey, ScoreRecordKey.ColourCupTeamRoundBest, StringComparison.Ordinal)
            ? "Colour Cup team"
            : "Type Cup team";
        int maximum = occurrences.Max(o => o.TeamRoundTotalThousandths);
        Dictionary<string, TeamRoundOccurrence> earliestByTeam = [];
        foreach (TeamRoundOccurrence o in occurrences.Where(o => o.TeamRoundTotalThousandths == maximum))
        {
            if (!earliestByTeam.TryGetValue(o.TeamKey, out TeamRoundOccurrence? existing) ||
                IsEarlierTeamRound(o, existing))
            {
                earliestByTeam[o.TeamKey] = o;
            }
        }

        List<ScoringHolder> holders = earliestByTeam.Values
            .OrderBy(o => o.TeamKey, StringComparer.Ordinal)
            .Select(o => new ScoringHolder(null, o.TeamKey, maximum, o.SeasonNumber, competition, null, null, o.RoundNumber, null))
            .ToList();
        return new ScoringRecordResult(recordKey, maximum, holders);
    }

    internal static Dictionary<int, T> EarliestPerAthlete<T>(List<T> tied, Func<T, int> idSelector, Func<List<T>, T> pickEarliest)
    {
        // Group tied occurrences by athlete, then pick the earliest occurrence
        // per athlete using the caller-supplied deterministic ordering
        // (season/stage/round ascending). This preserves ties across athletes
        // while giving each holder one stable representative occurrence.
        ArgumentNullException.ThrowIfNull(tied);
        ArgumentNullException.ThrowIfNull(idSelector);
        ArgumentNullException.ThrowIfNull(pickEarliest);
        Dictionary<int, List<T>> byAthlete = [];
        foreach (T item in tied)
        {
            int id = idSelector(item);
            if (!byAthlete.TryGetValue(id, out List<T>? list))
            {
                list = [];
                byAthlete[id] = list;
            }

            list.Add(item);
        }

        Dictionary<int, T> earliest = [];
        foreach ((int id, List<T> list) in byAthlete)
        {
            earliest[id] = pickEarliest(list);
        }

        return earliest;
    }

    internal static string AthleteDisplayName(IReadOnlyDictionary<int, string> names, int athleteId)
    {
        return names.TryGetValue(athleteId, out string? name) ? name : string.Empty;
    }

    internal static bool IsEarlierTeamRound(TeamRoundOccurrence candidate, TeamRoundOccurrence existing)
    {
        if (candidate.SeasonNumber != existing.SeasonNumber)
        {
            return candidate.SeasonNumber < existing.SeasonNumber;
        }

        return candidate.RoundNumber < existing.RoundNumber;
    }

    internal static void ValidateAthlete(int athleteId, IReadOnlyDictionary<int, string> names)
    {
        if (athleteId <= 0)
        {
            throw new InvalidOperationException($"Scoring occurrence references corrupt athlete {athleteId}.");
        }

        if (!names.ContainsKey(athleteId))
        {
            throw new InvalidOperationException($"Scoring occurrence references unknown athlete {athleteId}.");
        }
    }

    internal static void ValidateNonNegative(int value, int athleteId)
    {
        if (value < 0)
        {
            throw new InvalidOperationException($"Athlete {athleteId} has corrupt negative scoring value {value}.");
        }
    }

    internal static void ValidateTeamKey(string teamKey)
    {
        if (string.IsNullOrWhiteSpace(teamKey))
        {
            throw new InvalidOperationException("Team scoring occurrence has corrupt empty team key.");
        }
    }

    internal static void ValidateSeasonStageRound(int season, int stage, int round)
    {
        if (season < 1 || stage < 1 || round < 1)
        {
            throw new InvalidOperationException($"Scoring occurrence has corrupt season/stage/round {season}/{stage}/{round}.");
        }
    }
}
