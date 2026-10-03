namespace MtgSoloSports.Features.Records;

/// <summary>
/// Stable scoring-record keys with explicit category/scope.
/// League records are scoped per league so feeder colors and the Superleague
/// never mix: each feeder color shares the same scoring table and bonus scale,
/// while the Superleague awards double bonus and therefore stays separate.
/// Cup records are scoped by event type (Colour Cup individual, Qualifier,
/// Colour Cup team, Type Cup team) and by round/stage/group-stage scope so
/// incompatible formats are never compared. All sporting values are
/// fixed-point thousandths integers; no floating point is used.
/// </summary>
public static class ScoreRecordKey
{
    public const string CategoryLeague = "League";
    public const string CategoryIndividualCups = "Individual Cups";
    public const string CategoryTeamCups = "Team Cups";

    public const string ScopeSuperleague = "superleague";
    public const string ScopeWhite = "white";
    public const string ScopeBlue = "blue";
    public const string ScopeBlack = "black";
    public const string ScopeRed = "red";
    public const string ScopeGreen = "green";
    public const string ScopeMulticolor = "multicolor";
    public const string ScopeHybrid = "hybrid";
    public const string ScopeColorless = "colorless";

    public static readonly IReadOnlyList<string> LeagueScopes =
    [
        ScopeWhite,
        ScopeBlue,
        ScopeBlack,
        ScopeRed,
        ScopeGreen,
        ScopeMulticolor,
        ScopeHybrid,
        ScopeColorless,
        ScopeSuperleague,
    ];

    // League single-round best, per league scope.
    public static string LeagueSingleRound(string scope) => $"league_single_round__{scope}";

    // League single-stage best, per league scope.
    public static string LeagueStageBest(string scope) => $"league_stage_best__{scope}";

    // League points (season championship points), per league scope.
    public static string LeaguePointsBest(string scope) => $"league_points_best__{scope}";

    public const string ColourCupIndividualRoundBest = "colour_cup_individual_round_best";
    public const string ColourCupIndividualStageBest = "colour_cup_individual_stage_best";

    public const string QualifierSingleRoundBest = "qualifier_single_round_best";
    public const string QualifierStageBest = "qualifier_stage_best";

    public const string ColourCupTeamLegRoundBest = "colour_cup_team_leg_round_best";
    public const string ColourCupTeamLegStageBest = "colour_cup_team_leg_stage_best";
    public const string ColourCupTeamTotalBest = "colour_cup_team_total_best";
    public const string ColourCupTeamRoundBest = "colour_cup_team_round_best";

    public const string TypeCupTeamLegRoundBest = "type_cup_team_leg_round_best";
    public const string TypeCupTeamLegStageBest = "type_cup_team_leg_stage_best";
    public const string TypeCupTeamTotalBest = "type_cup_team_total_best";
    public const string TypeCupTeamRoundBest = "type_cup_team_round_best";

    /// <summary>
    /// All scoring record keys in stable presentation order: league scopes
    /// (feeder colors alphabetical by scope, Superleague last), then
    /// individual Cups, then team Cups.
    /// </summary>
    public static readonly IReadOnlyList<string> All = BuildAll();

    private static IReadOnlyList<string> BuildAll()
    {
        List<string> keys = [];
        // League scopes in fixed order: white, blue, black, red, green,
        // multicolor, hybrid, colorless, superleague — actually use LeagueScopes order.
        foreach (string scope in LeagueScopes)
        {
            keys.Add(LeagueSingleRound(scope));
        }

        foreach (string scope in LeagueScopes)
        {
            keys.Add(LeagueStageBest(scope));
        }

        foreach (string scope in LeagueScopes)
        {
            keys.Add(LeaguePointsBest(scope));
        }

        keys.Add(ColourCupIndividualRoundBest);
        keys.Add(ColourCupIndividualStageBest);
        keys.Add(QualifierSingleRoundBest);
        keys.Add(QualifierStageBest);
        keys.Add(ColourCupTeamLegRoundBest);
        keys.Add(ColourCupTeamLegStageBest);
        keys.Add(ColourCupTeamTotalBest);
        keys.Add(ColourCupTeamRoundBest);
        keys.Add(TypeCupTeamLegRoundBest);
        keys.Add(TypeCupTeamLegStageBest);
        keys.Add(TypeCupTeamTotalBest);
        keys.Add(TypeCupTeamRoundBest);
        return keys;
    }

    public static bool IsKnown(string recordKey) => All.Contains(recordKey, StringComparer.Ordinal);

    /// <summary>
    /// All scoring values are fixed-point thousandths points.
    /// </summary>
    public static bool IsPoints(string recordKey) => IsKnown(recordKey);

    /// <summary>
    /// True for pure team records whose holders are teams, not athletes.
    /// </summary>
    public static bool IsTeamRecord(string recordKey) => recordKey is
        ColourCupTeamTotalBest or ColourCupTeamRoundBest or
        TypeCupTeamTotalBest or TypeCupTeamRoundBest;

    public static string CategoryOf(string recordKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        if (recordKey.StartsWith("league_", StringComparison.Ordinal))
        {
            return CategoryLeague;
        }

        if (string.Equals(recordKey, ColourCupIndividualRoundBest, StringComparison.Ordinal) ||
            string.Equals(recordKey, ColourCupIndividualStageBest, StringComparison.Ordinal) ||
            string.Equals(recordKey, QualifierSingleRoundBest, StringComparison.Ordinal) ||
            string.Equals(recordKey, QualifierStageBest, StringComparison.Ordinal))
        {
            return CategoryIndividualCups;
        }

        return CategoryTeamCups;
    }

    public static string ScopeOf(string recordKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        foreach (string scope in LeagueScopes)
        {
            if (string.Equals(recordKey, LeagueSingleRound(scope), StringComparison.Ordinal) ||
                string.Equals(recordKey, LeagueStageBest(scope), StringComparison.Ordinal) ||
                string.Equals(recordKey, LeaguePointsBest(scope), StringComparison.Ordinal))
            {
                return scope;
            }
        }

        if (string.Equals(recordKey, ColourCupIndividualRoundBest, StringComparison.Ordinal) ||
            string.Equals(recordKey, ColourCupIndividualStageBest, StringComparison.Ordinal))
        {
            return "colour_cup_individual";
        }

        if (string.Equals(recordKey, QualifierSingleRoundBest, StringComparison.Ordinal) ||
            string.Equals(recordKey, QualifierStageBest, StringComparison.Ordinal))
        {
            return "qualifier";
        }

        if (recordKey.StartsWith("colour_cup_team_", StringComparison.Ordinal))
        {
            return "colour_cup_team";
        }

        return "type_cup_team";
    }

    public static string ScopeLabelOf(string recordKey)
    {
        string scope = ScopeOf(recordKey);
        return scope switch
        {
            ScopeSuperleague => "Superleague",
            ScopeWhite => "White League",
            ScopeBlue => "Blue League",
            ScopeBlack => "Black League",
            ScopeRed => "Red League",
            ScopeGreen => "Green League",
            ScopeMulticolor => "Multicolor League",
            ScopeHybrid => "Hybrid League",
            ScopeColorless => "Colorless League",
            "colour_cup_individual" => "Colour Cup individual",
            "qualifier" => "Qualifier",
            "colour_cup_team" => "Colour Cup team",
            _ => "Type Cup team",
        };
    }

    public static string LabelOf(string recordKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        foreach (string scope in LeagueScopes)
        {
            string scopeLabel = ScopeLabelOf(LeagueSingleRound(scope));
            if (string.Equals(recordKey, LeagueSingleRound(scope), StringComparison.Ordinal))
            {
                return $"Most athlete points in single {scopeLabel} round";
            }

            if (string.Equals(recordKey, LeagueStageBest(scope), StringComparison.Ordinal))
            {
                return $"Most athlete points in single {scopeLabel} stage";
            }

            if (string.Equals(recordKey, LeaguePointsBest(scope), StringComparison.Ordinal))
            {
                return $"Most league points in single {scopeLabel} season";
            }
        }

        return recordKey switch
        {
            ColourCupIndividualRoundBest => "Most athlete points in single Colour Cup individual round",
            ColourCupIndividualStageBest => "Most athlete points in Colour Cup individual stage",
            QualifierSingleRoundBest => "Most athlete points in single Qualifier round",
            QualifierStageBest => "Most athlete points in Qualifier stage",
            ColourCupTeamLegRoundBest => "Most athlete points in single Colour Cup team round",
            ColourCupTeamLegStageBest => "Most athlete points in Colour Cup team group stage (leg)",
            ColourCupTeamTotalBest => "Most team points in Colour Cup team event",
            ColourCupTeamRoundBest => "Most team points in single Colour Cup team round",
            TypeCupTeamLegRoundBest => "Most athlete points in single Type Cup team round",
            TypeCupTeamLegStageBest => "Most athlete points in Type Cup team group stage (leg)",
            TypeCupTeamTotalBest => "Most team points in Type Cup team event",
            TypeCupTeamRoundBest => "Most team points in single Type Cup team round",
            _ => recordKey.Replace('_', ' '),
        };
    }

    /// <summary>
    /// Maps a league (kind + sporting color) to its stable record scope.
    /// Superleague ignores sporting color; feeders use their color.
    /// </summary>
    public static string LeagueScopeFor(int leagueKind, int sportingColor)
    {
        if (leagueKind == 1)
        {
            return ScopeSuperleague;
        }

        return sportingColor switch
        {
            0 => ScopeWhite,
            1 => ScopeBlue,
            2 => ScopeBlack,
            3 => ScopeRed,
            4 => ScopeGreen,
            5 => ScopeMulticolor,
            6 => ScopeHybrid,
            7 => ScopeColorless,
            _ => throw new InvalidOperationException($"League has corrupt sporting color {sportingColor}."),
        };
    }

    /// <summary>
    /// Display-only projection of fixed-point thousandths points (no sporting math).
    /// </summary>
    public static string FormatPoints(int thousandths)
    {
        return (thousandths / 1000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }
}
