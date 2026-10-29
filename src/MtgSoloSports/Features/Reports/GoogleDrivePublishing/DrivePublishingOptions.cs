using Microsoft.Extensions.Hosting;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// App-level Google Drive publishing configuration. Everything lives outside
/// sporting saves: OAuth client credentials come from server-side configuration
/// (environment/user-secrets, never committed), while refresh tokens and the
/// per-save destination mapping live in a permission-restricted directory
/// under the persistent Docker volume. Game saves and backup ZIPs never carry
/// publishing state.
/// </summary>
public sealed class DrivePublishingOptions
{
    public const string SectionName = "DrivePublishing";

    /// <summary>
    /// Absolute path, or a path relative to the host content root.
    /// Defaults to a local <c>drive-publishing</c> directory; Compose maps it
    /// to <c>/data/drive-publishing</c> so tokens survive container restarts.
    /// </summary>
    public string StorePath { get; set; } = "drive-publishing";

    /// <summary>Google Cloud OAuth client id for the personal OAuth app. Empty means publishing is unconfigured.</summary>
    public string GoogleClientId { get; set; } = string.Empty;

    /// <summary>Google Cloud OAuth client secret. Never logged, never returned to the frontend.</summary>
    public string GoogleClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Public callback URL registered in the Google Cloud console. Defaults to
    /// the localhost Docker callback; override for host-run port differences.
    /// </summary>
    public string GoogleRedirectUri { get; set; } = "http://localhost:8080/api/reports/drive/callback";

    public bool IsGoogleConfigured =>
        !string.IsNullOrWhiteSpace(GoogleClientId) && !string.IsNullOrWhiteSpace(GoogleClientSecret);

    public string ResolveStoreRoot(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string configured = string.IsNullOrWhiteSpace(StorePath) ? "drive-publishing" : StorePath.Trim();
        return Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
    }
}
