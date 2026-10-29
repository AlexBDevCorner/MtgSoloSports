namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>No linked Google account (or consent expired/revoked).</summary>
public sealed class DriveNotConnectedException(string message) : InvalidOperationException(message);
