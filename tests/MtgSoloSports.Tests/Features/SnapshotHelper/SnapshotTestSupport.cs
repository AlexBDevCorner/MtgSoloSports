using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Tests.Features.SnapshotHelper;

/// <summary>
/// Shared scaffolding for MSS-133 helper tests: temp roots, configs, bundles
/// and in-process CLI runs. All state lives in isolated temp directories that
/// the owning test deletes; no live container or user data is involved.
/// </summary>
internal static class SnapshotTestSupport
{
    internal static string NewTempDir(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string WriteConfig(string directory, Guid saveId, string? archiveDirectory = null)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "snapshot.config.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                baseUrl = "http://localhost:8080",
                saveId = saveId.ToString("D"),
                workingDirectory = "./snapshot-data",
                archiveDirectory = archiveDirectory ?? "./journalist-archive",
            }));
        return path;
    }

    internal static async Task<string> WriteBundleAsync(string directory, string fileName, byte[] bundle)
    {
        string path = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(path, bundle).ConfigureAwait(false);
        return path;
    }

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(string[] args, HttpMessageHandler? handler)
    {
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = await SaveSnapshot.Program.RunAsync(args, handler, stdout, stderr).ConfigureAwait(false);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    internal static (SaveBundleManifest Manifest, byte[] DatabaseBytes) Unpack(byte[] zipBytes)
    {
        using ZipArchive archive = new(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read);
        ZipArchiveEntry databaseEntry = archive.Entries.Single(e => string.Equals(e.FullName, SaveBundleManifest.DatabaseEntryName, StringComparison.Ordinal));
        ZipArchiveEntry manifestEntry = archive.Entries.Single(e => string.Equals(e.FullName, SaveBundleManifest.ManifestEntryName, StringComparison.Ordinal));
        using StreamReader reader = new(manifestEntry.Open(), Encoding.UTF8);
        SaveBundleManifest manifest = SaveBundleManifest.FromJson(reader.ReadToEnd());
        using MemoryStream databaseBuffer = new();
        using (Stream databaseStream = databaseEntry.Open())
        {
            databaseStream.CopyTo(databaseBuffer);
        }

        return (manifest, databaseBuffer.ToArray());
    }

    internal static byte[] Repack(SaveBundleManifest manifest, byte[] databaseBytes)
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry databaseEntry = archive.CreateEntry(SaveBundleManifest.DatabaseEntryName);
            using (Stream stream = databaseEntry.Open())
            {
                stream.Write(databaseBytes, 0, databaseBytes.Length);
            }

            byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJson());
            ZipArchiveEntry manifestEntry = archive.CreateEntry(SaveBundleManifest.ManifestEntryName);
            using (Stream stream = manifestEntry.Open())
            {
                stream.Write(manifestBytes, 0, manifestBytes.Length);
            }
        }

        return buffer.ToArray();
    }

    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_respond(request));
        }
    }
}
