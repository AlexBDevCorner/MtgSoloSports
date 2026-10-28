using System.Globalization;
using System.Text;
using MtgSoloSports.Features.Athletes.GetProfile;
using MtgSoloSports.Features.Athletes.GetRecordHoldings;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.AthleteReport;

/// <summary>
/// Markdown rendering for athlete reports. Display-only projections of
/// fixed-point thousandths; never changes sporting math.
/// </summary>
internal static class AthleteReportMarkdown
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        GetAthleteProfileResponse profile,
        GetAthleteRecordHoldingsResponse holdings)
    {
        StringBuilder sb = new();
        AppendHeader(sb, saveId, metadata, rules, seasonCount, profile);
        AppendIdentity(sb, profile);
        AppendCareer(sb, profile);
        AppendSeasons(sb, profile);
        AppendHonours(sb, profile);
        AppendMovements(sb, profile);
        AppendSelections(sb, profile);
        AppendCupHistory(sb, profile);
        AppendHoldings(sb, holdings);
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, Guid saveId, SaveMetadataEntity metadata, SimulationKernel.Rules.RulesV1 rules, int seasonCount, GetAthleteProfileResponse profile)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Athlete report — {profile.Card.Name}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Save: {metadata.Name} (`{saveId:D}`) · Season {metadata.CurrentSeason} · Phase {metadata.Phase}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Scope: athlete {profile.AthleteId} · Snapshot: {seasonCount} season(s), last active Season {profile.Career.LastSeasonNumber} Stage {profile.Career.LastStageNumber}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Report: {ReportContract.AthleteReportType} v{ReportContract.SchemaVersion} · Rules v{rules.Version} · Schema v{metadata.SchemaVersion}");
        sb.AppendLine();
        sb.AppendLine("Units: championship/stage/base scores are fixed-point thousandths (divide by 1000, e.g. 77000 = 77.000). Bonus is thousandths of a percent (e.g. 7552 = +7.552%). `null`/— means unavailable or not-applicable; 0 is an exact zero. League totals are official season championship points; weighted Career Leaders points are never substituted.");
        sb.AppendLine();
    }

    private static void AppendIdentity(StringBuilder sb, GetAthleteProfileResponse profile)
    {
        sb.AppendLine("## Identity");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Athlete id: {profile.AthleteId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Name: {ReportMarkdown.Cell(profile.Card.Name)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Sporting color: {ReportMarkdown.Cell(profile.Card.SportingColorName)} ({profile.Card.SportingColor})");
        string types = profile.Card.CreatureTypes.Count == 0 ? "— (none)" : string.Join(", ", profile.Card.CreatureTypes);
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Creature types: {ReportMarkdown.Cell(types)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Type line: {ReportMarkdown.Cell(profile.Card.TypeLine)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Mana cost: {ReportMarkdown.Cell(profile.Card.ManaCost)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Front colors: {ReportMarkdown.Cell(profile.Card.FrontColors)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Set: {ReportMarkdown.Cell(profile.Card.SetCode)}");
        string nationality = string.IsNullOrWhiteSpace(profile.Card.TypeCupNationality) ? "Uncapped" : profile.Card.TypeCupNationality;
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Type Cup nationality: {ReportMarkdown.Cell(nationality)}");
        string status = profile.Career.IsActive ? $"Active · {profile.Career.CurrentLeagueName}" : "Common pool";
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Status: {ReportMarkdown.Cell(status)}");
        sb.AppendLine();
    }

    private static void AppendCareer(StringBuilder sb, GetAthleteProfileResponse profile)
    {
        sb.AppendLine("## Career");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Seasons active: {profile.Career.SeasonsActive}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Round wins: {profile.Career.RoundWins} · Stage wins: {profile.Career.StageWins} · Stage podiums: {profile.Career.StagePodiums} ({profile.Career.StageWins}W · {profile.Career.StageSeconds}2nd · {profile.Career.StageThirds}3rd)");
        string best = profile.Career.BestSeasonFinish.HasValue && profile.Career.BestSeasonNumber.HasValue
            ? $"P{profile.Career.BestSeasonFinish.Value} (Season {profile.Career.BestSeasonNumber.Value})"
            : "— (no ranked season yet)";
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Best finish: {best}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Lifetime earned bonus: {ReportMarkdown.FormatBonus(profile.Career.LifetimeEarnedBonusThousandths)} ({profile.Career.LifetimeEarnedBonusThousandths} thousandths)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Current effective bonus: {ReportMarkdown.FormatBonus(profile.Career.CurrentEffectiveBonusThousandths)} ({profile.Career.CurrentEffectiveBonusThousandths} thousandths)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Updated through: Season {profile.Career.LastSeasonNumber} · Stage {profile.Career.LastStageNumber}");
        sb.AppendLine();
    }

    private static void AppendSeasons(StringBuilder sb, GetAthleteProfileResponse profile)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Seasons ({profile.Seasons.Count})");
        sb.AppendLine();
        if (profile.Seasons.Count == 0)
        {
            sb.AppendLine("No seasons yet. The athlete has no persisted season summaries.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Season | League | Rank | Round W | Stage W | 2nd/3rd | Earned bonus | Championship pts |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (AthleteSeasonDto season in profile.Seasons)
        {
            string league = season.WasActive
                ? $"{ReportMarkdown.Cell(season.LeagueName)}" + (season.LeagueLevel is not null ? $" ({season.LeagueLevel})" : string.Empty)
                : "Common pool";
            string rank = season.SeasonRank.HasValue ? $"P{season.SeasonRank.Value}" + (season.IsChampion ? " · Champion" : string.Empty) : "— (pool/unranked)";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {season.SeasonNumber} | {league} | {rank} | {season.RoundWins} | {season.StageWins} | {season.StageSeconds}/{season.StageThirds} | {ReportMarkdown.FormatBonus(season.EarnedBonusThousandths)} | {ReportMarkdown.FormatPoints(season.TotalChampionshipPointsThousandths)} |");
        }

        sb.AppendLine();
        sb.AppendLine("Season championship points are official league totals (32-position table, no bonus multiplier). Stage/base totals are display sums from the same persisted rows.");
        sb.AppendLine();
    }

    private static void AppendHonours(StringBuilder sb, GetAthleteProfileResponse profile)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Honours ({profile.Honours.Count})");
        sb.AppendLine();
        if (profile.Honours.Count == 0)
        {
            sb.AppendLine("No official honours yet. League podiums (1st/2nd/3rd) plus Color Cup and Type Cup podiums persist here.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Season | League | Honour |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (AthleteHonourDto honour in profile.Honours)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {honour.SeasonNumber} | {ReportMarkdown.Cell(honour.LeagueName)} | {ReportMarkdown.Cell(honour.HonourKind)} |");
        }

        sb.AppendLine();
    }

    private static void AppendMovements(StringBuilder sb, GetAthleteProfileResponse profile)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Movements ({profile.Movements.Count})");
        sb.AppendLine();
        if (profile.Movements.Count == 0)
        {
            sb.AppendLine("No postseason movements yet. Promotion, relegation, qualifier entries and pool returns appear here.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Seasons | Movement | From → To |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (AthleteMovementDto movement in profile.Movements)
        {
            string rank = movement.FromSeasonRank > 0 ? $" · P{movement.FromSeasonRank}" : string.Empty;
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {movement.FromSeasonNumber} → {movement.ToSeasonNumber} | {ReportMarkdown.Cell(movement.Kind)}{rank} | {ReportMarkdown.Cell(movement.FromLeagueName)} → {ReportMarkdown.Cell(movement.ToLeagueName)} |");
        }

        sb.AppendLine();
    }

    private static void AppendSelections(StringBuilder sb, GetAthleteProfileResponse profile)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Cup selections ({profile.CupSelections.Count})");
        sb.AppendLine();
        if (profile.CupSelections.Count == 0)
        {
            sb.AppendLine("No Cup selections yet. Selection order is #1–#4 by rating within each color or creature-type team.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Season | Cup | Team | Rank |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (AthleteCupSelectionDto selection in profile.CupSelections)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {selection.SourceSeasonNumber} | {ReportMarkdown.Cell(selection.CupKind)} | {ReportMarkdown.Cell(selection.Team)} | #{selection.SelectionRank} |");
        }

        sb.AppendLine();
    }

    private static void AppendCupHistory(StringBuilder sb, GetAthleteProfileResponse profile)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Cup history ({profile.CupHistory.Count})");
        sb.AppendLine();
        if (profile.CupHistory.Count == 0)
        {
            sb.AppendLine("No Cup appearances yet. Completed Color Cup (individual and team) and Type Cup (team) results appear here, newest seasons first.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Season | Event | Team | Place | Result |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (AthleteCupHistoryDto entry in profile.CupHistory)
        {
            string stage = string.IsNullOrWhiteSpace(entry.TournamentStage) ? string.Empty : $" · {entry.TournamentStage}";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {entry.SourceSeasonNumber} | {ReportMarkdown.Cell(entry.EventName)} ({ReportMarkdown.Cell(entry.Event)}){stage} | {ReportMarkdown.Cell(entry.TeamName)} | P{entry.Place} | {ReportMarkdown.Cell(entry.Medal)} · {ReportMarkdown.FormatPoints(entry.ScoreThousandths)} |");
        }

        sb.AppendLine();
        sb.AppendLine("Team rows show the team's final place; the athlete's own leg contribution is not a separate official place. Type Cup qualification-only rows keep the appearance without honours; only Final ranks 1–3 hold titles.");
        sb.AppendLine();
    }

    private static void AppendHoldings(StringBuilder sb, GetAthleteRecordHoldingsResponse holdings)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Record holdings ({holdings.Holdings.Count})");
        sb.AppendLine();
        if (holdings.Holdings.Count == 0)
        {
            sb.AppendLine("Holds no outright career records right now. Ties share records; only an outright higher value replaces holders.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Record | Value |");
        sb.AppendLine("| --- | --- |");
        foreach (RecordHoldingEntry holding in holdings.Holdings)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {ReportMarkdown.Cell(holding.Label)} ({ReportMarkdown.Cell(holding.RecordKey)}) | {ReportMarkdown.Cell(holding.ValueDisplay)} |");
        }

        sb.AppendLine();
    }
}
