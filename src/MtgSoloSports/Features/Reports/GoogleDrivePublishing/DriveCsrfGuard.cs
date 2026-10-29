namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Localhost-only CSRF guard for Drive mutating endpoints. The app holds Google
/// refresh credentials server-side with no per-user auth, so a cross-origin page
/// posting to localhost could otherwise disconnect or publish. Binding the Docker
/// port to 127.0.0.1 stops LAN access but not same-machine cross-origin posts,
/// so reject non-local Origins/Referers with 403. Requests without either header
/// (curl, tests, same-origin navigations) are allowed.
/// </summary>
public static class DriveCsrfGuard
{
    public static IResult? BlockIfUnsafe(HttpRequest request) =>
        IsSafe(request) ? null : Results.Json(
            new { error = "Cross-origin Drive request was blocked (Origin/Referer check). Use the Reports page on the local app origin." },
            statusCode: StatusCodes.Status403Forbidden);

    public static bool IsSafe(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? origin = request.Headers.Origin;
        string? referer = request.Headers.Referer;
        if (string.IsNullOrWhiteSpace(origin) && string.IsNullOrWhiteSpace(referer))
        {
            return true;
        }

        string requestHost = request.Host.Host;
        if (!string.IsNullOrWhiteSpace(origin) && !IsAllowedOrigin(origin, requestHost))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(referer) && !IsAllowedOrigin(referer, requestHost))
        {
            return false;
        }

        return true;
    }

    private static bool IsAllowedOrigin(string value, string requestHost)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string host = uri.Host;
        if (IsLoopbackHost(host))
        {
            return true;
        }

        return string.Equals(host, requestHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(host, "::1", StringComparison.Ordinal)
        || string.Equals(host, "[::1]", StringComparison.Ordinal);
}
