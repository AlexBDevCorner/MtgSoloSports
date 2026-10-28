namespace MtgSoloSports.Features.Reports.CupTeamReport;

public sealed class CupTeamReportNotFoundException : Exception
{
    public CupTeamReportNotFoundException(string message)
        : base(message)
    {
    }
}
