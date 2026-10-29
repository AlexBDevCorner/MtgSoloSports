namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>OAuth user-consent client. Secrets stay server-side; only the auth URL leaves the server.</summary>
public interface IGoogleOAuthClient
{
    string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string codeChallenge);

    Task<OAuthTokenResponse> ExchangeCodeAsync(
        string clientId,
        string clientSecret,
        string redirectUri,
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default);

    Task<OAuthTokenResponse> RefreshAsync(
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>Best-effort revoke; returns false instead of throwing for upstream failures.</summary>
    Task<bool> TryRevokeAsync(string token, CancellationToken cancellationToken = default);
}
