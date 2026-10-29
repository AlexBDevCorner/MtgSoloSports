namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Recoverable publishing conflict: ambiguous names, cross-save reuse, stale
/// remote edits or missing grants. Never mutates sporting state; the caller
/// surfaces the message and asks the user to adopt/confirm explicitly.
/// </summary>
public sealed class DriveConflictException(string message) : InvalidOperationException(message);
