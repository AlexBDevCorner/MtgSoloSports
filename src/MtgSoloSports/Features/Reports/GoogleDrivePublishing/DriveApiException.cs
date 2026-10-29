using System.Net;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>Typed Drive API failure with the HTTP status for refresh/retry decisions.</summary>
public sealed class DriveApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;

    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized || StatusCode == HttpStatusCode.Forbidden;

    public bool IsTransient => StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
