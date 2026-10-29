namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Localhost-only CSRF guard for Drive mutating endpoints. The app holds Google
/// refresh credentials server-side with no per-user auth, so a cross-origin page
/// posting to localhost could otherwise disconnect or publish. Binding the Docker
/// port to 127.0.0.1 stops LAN access but not same-machine cross-origin posts,
/// so require an exact same-origin (scheme, loopback host literal and port)
/// for Origin/Referer with 403 otherwise, and fail closed when the request
/// authority itself is not loopback (DNS rebinding). Requests without either
/// header (curl, tests) are allowed only when the Host is loopback.
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

        // Fail closed on DNS rebinding / LAN exposure: the publishing
        // endpoints trust the local browser and hold Google refresh tokens,
        // so the request authority itself must be a loopback origin. An
        // attacker-controlled hostname (e.g. evil.example rebound to
        // 127.0.0.1) must never be accepted even when Origin matches Host.
        if (!IsLoopbackHost(request.Host.Host))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(origin) && string.IsNullOrWhiteSpace(referer))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(origin) && !IsAllowedOrigin(origin, request))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(referer) && !IsAllowedOrigin(referer, request))
        {
            return false;
        }

        return true;
    }

    private static bool IsAllowedOrigin(string value, HttpRequest request)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        // Both ends must be loopback literals; a non-loopback Origin (or a
        // non-loopback Host) is never same-origin with this localhost-only
        // service, even when the two hostnames happen to be equal.
        if (!IsLoopbackHost(uri.Host) || !IsLoopbackHost(request.Host.Host))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int originPort = uri.IsDefaultPort ? DefaultPortForScheme(uri.Scheme) : uri.Port;
        int requestPort = request.Host.Port ?? DefaultPortForScheme(request.Scheme);
        return originPort == requestPort;
    }

    private static int DefaultPortForScheme(string scheme) =>
        string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(host, "::1", StringComparison.Ordinal)
        || string.Equals(host, "[::1]", StringComparison.Ordinal);
}
