using System.Text;
using System.Text.Json;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>Live Google OAuth implementation over plain HTTPS (no Google SDK dependency).</summary>
public sealed class GoogleOAuthClient : IGoogleOAuthClient
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    private readonly HttpClient _http;

    public GoogleOAuthClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string codeChallenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeChallenge);
        StringBuilder url = new(AuthEndpoint);
        url.Append("?client_id=").Append(Uri.EscapeDataString(clientId));
        url.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        url.Append("&response_type=code");
        url.Append("&scope=").Append(Uri.EscapeDataString(DriveScopes.DriveFile));
        url.Append("&access_type=offline&prompt=consent");
        url.Append("&state=").Append(Uri.EscapeDataString(state));
        url.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
        url.Append("&code_challenge_method=S256");
        return url.ToString();
    }

    public async Task<OAuthTokenResponse> ExchangeCodeAsync(
        string clientId,
        string clientSecret,
        string redirectUri,
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        using FormUrlEncodedContent body = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        });
        return await PostTokenAsync(body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OAuthTokenResponse> RefreshAsync(
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        using FormUrlEncodedContent body = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        });
        return await PostTokenAsync(body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryRevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            using FormUrlEncodedContent body = new(new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = token });
            using HttpResponseMessage response = await _http.PostAsync(RevokeEndpoint, body, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<OAuthTokenResponse> PostTokenAsync(FormUrlEncodedContent body, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(TokenEndpoint, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DriveUpstreamException("Google token endpoint is unreachable. Check internet access and retry.", ex);
        }

        using (response)
        {
            string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new DriveNotConnectedException(
                        "Google authorization expired or was revoked. Reconnect with Connect Google Drive.");
                }

                throw new DriveUpstreamException($"Google token endpoint failed ({(int)response.StatusCode}). Retry shortly.");
            }

            try
            {
                using JsonDocument json = JsonDocument.Parse(payload);
                JsonElement root = json.RootElement;
                string accessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    throw new DriveUpstreamException("Google token endpoint returned an empty access token.");
                }

                long expiresIn = root.TryGetProperty("expires_in", out JsonElement expires) && expires.TryGetInt64(out long seconds)
                    ? seconds
                    : 3600;
                string? refreshToken = root.TryGetProperty("refresh_token", out JsonElement refresh)
                    ? refresh.GetString()
                    : null;
                string? scope = root.TryGetProperty("scope", out JsonElement scopeElement)
                    ? scopeElement.GetString()
                    : null;
                return new OAuthTokenResponse(accessToken, expiresIn, refreshToken, scope);
            }
            catch (JsonException ex)
            {
                throw new DriveUpstreamException("Google token endpoint returned an unreadable response.", ex);
            }
            catch (KeyNotFoundException ex)
            {
                throw new DriveUpstreamException("Google token endpoint returned an unreadable response.", ex);
            }
        }
    }
}
