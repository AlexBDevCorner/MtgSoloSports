using System.Security.Cryptography;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>PKCE (S256) + unguessable OAuth state helpers. No external packages.</summary>
internal static class GooglePkce
{
    internal static string GenerateState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string GenerateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string ChallengeFor(string verifier)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
