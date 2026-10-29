using System.Security.Cryptography;
using System.Text;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Meaningful-content fingerprint for idempotent publishing. The report
/// Markdown carries a volatile <c>Generated:</c> timestamp line that must not
/// trigger Drive writes; everything else (snapshot status, tables, fixed-point
/// numbers, provisional/final labels) is meaningful and hashed.
/// </summary>
internal static class DrivePublishingFingerprint
{
    internal static string NormalizeMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        StringBuilder builder = new(markdown.Length);
        foreach (string line in markdown.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("Generated:", StringComparison.Ordinal))
            {
                continue;
            }

            builder.Append(line.TrimEnd());
            builder.Append('\n');
        }

        return builder.ToString();
    }

    internal static string ComputeHex(string markdown)
    {
        string normalized = NormalizeMarkdown(markdown);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
