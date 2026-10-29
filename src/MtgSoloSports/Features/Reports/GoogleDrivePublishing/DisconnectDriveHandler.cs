using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Disconnects the linked Google account: best-effort server-side revoke,
/// then deletes the stored tokens. Destination mappings are kept so a later
/// reconnect publishes to the same files. Never touches sporting state.
/// </summary>
public sealed class DisconnectDriveHandler
{
    private readonly SaveStore _saves;
    private readonly DrivePublishingStore _publishing;
    private readonly IGoogleOAuthClient _oauth;

    public DisconnectDriveHandler(
        SaveStore saves,
        DrivePublishingStore publishing,
        IGoogleOAuthClient oauth)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
    }

    public async Task HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
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

        DrivePublishingStore.ConnectionDocument? connection =
            await _publishing.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is not null)
        {
            if (!string.IsNullOrWhiteSpace(connection.RefreshToken))
            {
                await _oauth.TryRevokeAsync(connection.RefreshToken, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(connection.AccessToken))
            {
                await _oauth.TryRevokeAsync(connection.AccessToken, cancellationToken).ConfigureAwait(false);
            }
        }

        await _publishing.ClearConnectionAsync(cancellationToken).ConfigureAwait(false);
    }
}
