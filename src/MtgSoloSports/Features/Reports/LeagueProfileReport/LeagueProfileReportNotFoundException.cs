namespace MtgSoloSports.Features.Reports.LeagueProfileReport;

public sealed class LeagueProfileReportNotFoundException : Exception
{
    public LeagueProfileReportNotFoundException(string message)
        : base(message)
    {
    }
}
