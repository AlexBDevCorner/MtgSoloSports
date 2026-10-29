namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public sealed record OAuthTokenResponse(
    string AccessToken,
    long ExpiresInSeconds,
    string? RefreshToken,
    string? Scope);
