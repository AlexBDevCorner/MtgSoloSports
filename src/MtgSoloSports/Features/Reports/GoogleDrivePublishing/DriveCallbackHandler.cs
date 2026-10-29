using Microsoft.Extensions.Options;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// OAuth callback: consumes the single-use state (CSRF protection), exchanges
/// the code with PKCE server-side, and persists only the refresh token (+ a
/// short-lived access token) with owner-only file permissions. The browser is
/// then redirected back to the save's Reports page; tokens never appear in
/// URLs, logs or frontend responses.
/// </summary>
public sealed class DriveCallbackHandler
{
    private readonly DrivePublishingStore _publishing;
    private readonly IGoogleOAuthClient _oauth;
    private readonly IOptions<DrivePublishingOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly DriveAutoPublishTracker _tracker;

    public DriveCallbackHandler(
        DrivePublishingStore publishing,
        IGoogleOAuthClient oauth,
        IOptions<DrivePublishingOptions> options,
        TimeProvider timeProvider,
        DriveAutoPublishTracker tracker)
    {
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
    }

    public sealed record CallbackResult(bool Success, Guid SaveId, string RedirectTo);

    public async Task<CallbackResult> HandleAsync(
        string? code,
        string? state,
        string? error,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new ArgumentException("Missing authorization state. Restart Connect Google Drive.", nameof(state));
        }

        DrivePublishingStore.PendingAuthDocument? pending =
            await _publishing.TakePendingAsync(state, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            throw new ArgumentException(
                "Invalid or expired authorization state. Restart Connect Google Drive.", nameof(state));
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return new CallbackResult(false, pending.SaveId, ErrorRedirect(pending.SaveId,
                "Google authorization was denied or cancelled. Reconnect to grant the drive.file scope."));
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Missing authorization code. Restart Connect Google Drive.", nameof(code));
        }

        DrivePublishingOptions options = _options.Value;
        if (!options.IsGoogleConfigured)
        {
            throw new DriveNotConfiguredException(
                "Google Drive publishing is not configured on the server. Set the DrivePublishing Google client id/secret, then reconnect.");
        }

        OAuthTokenResponse tokens = await ExchangeCodeAsync(options, code, pending.CodeVerifier, cancellationToken).ConfigureAwait(false);
        await SaveConnectionAsync(tokens, cancellationToken).ConfigureAwait(false);

        await UnpauseAutoPublishingAsync(cancellationToken).ConfigureAwait(false);

        return new CallbackResult(true, pending.SaveId, $"/saves/{pending.SaveId:D}/reports?drive=connected");
    }

    private async Task<OAuthTokenResponse> ExchangeCodeAsync(
        DrivePublishingOptions options,
        string code,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        OAuthTokenResponse tokens = await _oauth.ExchangeCodeAsync(
            options.GoogleClientId,
            options.GoogleClientSecret,
            options.GoogleRedirectUri,
            code,
            codeVerifier,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            // Google only returns a refresh token on first consent; without one
            // the link cannot survive a restart, so ask the user to reconnect.
            throw new DriveNotConnectedException(
                "Google did not return a refresh token. Disconnect and reconnect with Connect Google Drive (approval prompt) so publishing survives restarts.");
        }

        return tokens;
    }

    private async Task SaveConnectionAsync(OAuthTokenResponse tokens, CancellationToken cancellationToken)
    {
        string refreshToken = tokens.RefreshToken ?? string.Empty;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new DriveNotConnectedException(
                "Google did not return a refresh token. Disconnect and reconnect with Connect Google Drive (approval prompt) so publishing survives restarts.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        await _publishing.SaveConnectionAsync(
            new DrivePublishingStore.ConnectionDocument(
                refreshToken,
                Email: null,
                Scopes: string.IsNullOrWhiteSpace(tokens.Scope) ? DriveScopes.DriveFile : tokens.Scope,
                ConnectedUtc: now,
                AccessToken: tokens.AccessToken,
                AccessTokenExpiresUtc: now.AddSeconds(Math.Max(60, tokens.ExpiresInSeconds - 60))),
            cancellationToken).ConfigureAwait(false);
    }

    public static string ErrorRedirect(Guid saveId, string message)
    {
        string trimmed = message.Trim();
        if (trimmed.Length > 200)
        {
            trimmed = trimmed[..200];
        }

        return $"/saves/{saveId:D}/reports?drive=error&message={Uri.EscapeDataString(trimmed)}";
    }

    /// <summary>
    /// Reconnect catch-up: a restored connection unpauses automatic publishing
    /// for every enabled save with an unpublished committed checkpoint and
    /// signals the pump. No Google calls; the worker performs the uploads.
    /// </summary>
    private async Task UnpauseAutoPublishingAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DrivePublishingStore.DestinationDocument> all;
        try
        {
            all = await _publishing.ListDestinationsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (DrivePublishingStore.DestinationDocument destination in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!destination.AutoPublishEnabled || !DriveAutoPublishStatus.HasPendingRequest(destination))
            {
                continue;
            }

            if (!destination.AutoPausedForAuth && destination.AutoNextRetryUtc.HasValue && destination.AutoNextRetryUtc.Value > now)
            {
                _tracker.Signal(destination.SaveId);
                continue;
            }

            SemaphoreSlim gate = DriveSaveGates.ForSave(destination.SaveId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DrivePublishingStore.DestinationDocument? current =
                    await _publishing.GetDestinationAsync(destination.SaveId, cancellationToken).ConfigureAwait(false);
                if (current is null || !current.AutoPublishEnabled || !DriveAutoPublishStatus.HasPendingRequest(current))
                {
                    continue;
                }

                await _publishing.SaveDestinationAsync(current with
                {
                    AutoPausedForAuth = false,
                    AutoLastError = null,
                    AutoNextRetryUtc = now,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                continue;
            }
            finally
            {
                gate.Release();
            }

            _tracker.Signal(destination.SaveId);
        }
    }
}
