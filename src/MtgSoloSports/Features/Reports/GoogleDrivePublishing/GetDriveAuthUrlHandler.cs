using Microsoft.Extensions.Options;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Starts the OAuth user-consent flow: mints a single-use unguessable state +
/// PKCE verifier, stores them server-side with a 10-minute expiry, and returns
/// the Google consent URL for the browser. Secrets never leave the server.
/// </summary>
public sealed class GetDriveAuthUrlHandler
{
    private readonly SaveStore _saves;
    private readonly DrivePublishingStore _publishing;
    private readonly IGoogleOAuthClient _oauth;
    private readonly IOptions<DrivePublishingOptions> _options;
    private readonly TimeProvider _timeProvider;

    public GetDriveAuthUrlHandler(
        SaveStore saves,
        DrivePublishingStore publishing,
        IGoogleOAuthClient oauth,
        IOptions<DrivePublishingOptions> options,
        TimeProvider timeProvider)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public sealed record AuthUrlResult(string AuthUrl);

    public async Task<AuthUrlResult> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        DrivePublishingOptions options = _options.Value;
        if (!options.IsGoogleConfigured)
        {
            throw new DriveNotConfiguredException(
                "Google Drive publishing is not configured. Set the DrivePublishing Google client id/secret on the server (see docs/drive-publishing.md), then retry.");
        }

        await _saves.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await _saves.OpenAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        catch (SaveNotFoundException)
        {
            throw;
        }

        string state = GooglePkce.GenerateState();
        string verifier = GooglePkce.GenerateVerifier();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await _publishing.SavePendingAsync(
            new DrivePublishingStore.PendingAuthDocument(state, saveId, verifier, now, now.AddMinutes(10)),
            cancellationToken).ConfigureAwait(false);

        string url = _oauth.BuildAuthorizationUrl(
            options.GoogleClientId,
            options.GoogleRedirectUri,
            state,
            GooglePkce.ChallengeFor(verifier));
        return new AuthUrlResult(url);
    }
}
