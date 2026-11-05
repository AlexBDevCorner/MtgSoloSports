namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// MSS-093: stable sporting-color folder names for the athlete archive.
/// These are the athlete's stable printed sporting color, never current
/// league membership or nationality. Order follows SportingColor.
/// </summary>
internal static class DriveAthleteColors
{
    internal static readonly IReadOnlyList<string> All =
    [
        "White",
        "Blue",
        "Black",
        "Red",
        "Green",
        "Multicolor",
        "Hybrid",
        "Colorless",
    ];

    internal static string DefaultRootName => "Athletes";
}
