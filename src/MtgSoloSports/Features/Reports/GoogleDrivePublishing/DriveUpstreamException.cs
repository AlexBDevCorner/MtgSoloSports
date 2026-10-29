namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>Google network/API failure. Never rolls back or corrupts simulation.</summary>
public sealed class DriveUpstreamException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
