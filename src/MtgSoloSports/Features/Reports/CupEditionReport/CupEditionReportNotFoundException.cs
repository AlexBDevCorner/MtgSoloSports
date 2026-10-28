namespace MtgSoloSports.Features.Reports.CupEditionReport;

public sealed class CupEditionReportNotFoundException : Exception
{
    public CupEditionReportNotFoundException(string message)
        : base(message)
    {
    }
}
