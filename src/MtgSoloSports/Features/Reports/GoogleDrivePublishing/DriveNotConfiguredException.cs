namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>Missing Google OAuth client configuration (operator setup required).</summary>
public sealed class DriveNotConfiguredException(string message) : InvalidOperationException(message);
