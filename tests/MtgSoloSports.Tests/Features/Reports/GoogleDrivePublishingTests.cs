using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Features.Reports.GoogleDrivePublishing;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

public sealed class GoogleDrivePublishingTests
{
    [Fact]
    public async Task ConnectFlow_LinksAccountAndReportsStatus()
    {
        var (factory, root, oauth, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await AssertNotConnectedAsync(client, saveId);
            Guid linked = await ConnectOverHttpAsync(factory, client, saveId);
            linked.ShouldBe(saveId);
            _ = drive;
            _ = oauth;
            using HttpResponseMessage status = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status");
            status.StatusCode.ShouldBe(HttpStatusCode.OK);
            using JsonDocument json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("connected").GetBoolean().ShouldBeTrue();
            json.RootElement.GetProperty("configured").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Publish_CreatesBothFilesMatchingDownloads()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Status.ShouldBe(HttpStatusCode.OK);
            published.Outcomes.ShouldBe(["dossier:created", "almanac:created"]);
            await AssertStoredMatchesDownloadAsync(client, saveId, drive);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Republish_Unchanged_SkipsWritesWithoutDuplicates()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            _ = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            int filesAfterFirst = drive.Files.Count;
            filesAfterFirst.ShouldBe(3);
            PublishResult second = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            second.Outcomes.ShouldBe(["dossier:unchanged", "almanac:unchanged"]);
            drive.Files.Count.ShouldBe(filesAfterFirst);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdvanceStage_Republish_UpdatesInPlaceWithSameIds()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult first = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            PublishResult second = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            second.Status.ShouldBe(HttpStatusCode.OK);
            second.Outcomes.ShouldBe(["dossier:updated", "almanac:updated"]);
            second.FileId("dossier").ShouldBe(first.FileId("dossier"));
            second.FileId("almanac").ShouldBe(first.FileId("almanac"));
            drive.Files.Count.ShouldBe(3);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdoptedCustomNames_UpdateInPlaceWithoutExtraFiles()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFile("dossier-adopted", "folder-seasons", "season-old.md", "# old dossier");
            drive.SeedFile("almanac-adopted", "folder-mtshistory", "universe-almanac-newtest.md", "# old almanac");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileId":"dossier-adopted","dossierFileName":"season-17-dossier.md","almanacFileId":"almanac-adopted","almanacFileName":"universe-almanac-newtest.md"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Outcomes.ShouldBe(["dossier:updated", "almanac:updated"]);
            published.FileId("dossier").ShouldBe("dossier-adopted");
            published.FileId("almanac").ShouldBe("almanac-adopted");
            drive.Files["dossier-adopted"].Content.ShouldContain("# Season dossier");
            drive.Files["almanac-adopted"].Content.ShouldContain("# Universe almanac");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AmbiguousNames_StopWithAdoptionGuidance()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            _ = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            int baseline = drive.Files.Count;
            // Plant two same-named candidates in the Seasons folder, then clear
            // the adopted dossier id so the next publish takes the create path.
            string seasonsId = drive.FoldersFor("folder-mtshistory", "Seasons").Single();
            drive.SeedFile("dup-1", seasonsId, "clash.md", "a");
            drive.SeedFile("dup-2", seasonsId, "clash.md", "b");
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileName":"clash.md"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Outcome("dossier").ShouldBe("failed");
            published.Detail("dossier")!.ShouldContain("Adopt exactly one file");
            drive.Files.Count.ShouldBe(baseline + 2);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StaleRemoteEdit_NeedsExplicitOverwrite()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult first = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            string dossierId = first.FileId("dossier")!;
            drive.Files[dossierId] = drive.Files[dossierId] with { Content = "# hand-edited by user\n" };
            PublishResult blocked = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            blocked.Outcome("dossier").ShouldBe("failed");
            blocked.Detail("dossier")!.ShouldContain("edited independently");
            drive.Files[dossierId].Content.ShouldBe("# hand-edited by user\n");
            PublishResult forced = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: true);
            forced.Outcome("dossier").ShouldBe("updated");
            drive.Files[dossierId].Content.ShouldContain("# Season dossier");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedUpload_IsRecoverableAndSportingStateUnchanged()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult first = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            (long rng, long stream, _) = await ReadRngAndStandingsAsync(root, saveId);
            drive.FailUpdatesFor.Add(first.FileId("dossier")!);
            PublishResult failed = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            failed.Outcome("dossier").ShouldBe("failed");
            // The failed publish must not touch simulation state at all.
            (long rng2, long stream2, int standings2) = await ReadRngAndStandingsAsync(root, saveId);
            rng2.ShouldBe(rng);
            stream2.ShouldBe(stream);
            _ = standings2;
            drive.FailUpdatesFor.Clear();
            PublishResult retried = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            retried.Outcome("dossier").ShouldBe("updated");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CrossSaveReuse_NeedsExplicitConfirmation()
    {
        var (factory, root, _, _) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            await store.SaveDestinationAsync(new DrivePublishingStore.DestinationDocument(
                Guid.NewGuid(), "Other save", "folder-shared", "mtshistory", null,
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null), CancellationToken.None);
            using HttpResponseMessage conflict = await PutJsonAsync(client, $"/api/saves/{saveId:D}/reports/drive/destination",
                """{"mtshistoryFolderId":"folder-shared"}""");
            conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            using HttpResponseMessage accepted = await PutJsonAsync(client, $"/api/saves/{saveId:D}/reports/drive/destination",
                """{"mtshistoryFolderId":"folder-shared","confirmCrossSave":true}""");
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
            PublishResult blocked = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            blocked.Status.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingConfigOrAuth_FailsGracefully()
    {
        var (factory, root, _, _) = CreateFactory(configured: false);
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient noRedirect = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using HttpResponseMessage authUrl = await noRedirect.GetAsync($"/api/saves/{saveId:D}/reports/drive/auth-url");
            authUrl.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            using HttpResponseMessage publish = await noRedirect.PostAsync(
                $"/api/saves/{saveId:D}/reports/drive/publish",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            publish.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Disconnect_ClearsConnectionAndBlocksPublish()
    {
        var (factory, root, oauth, _) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            using HttpResponseMessage disconnect = await client.PostAsync(
                $"/api/saves/{saveId:D}/reports/drive/disconnect", null);
            disconnect.StatusCode.ShouldBe(HttpStatusCode.OK);
            oauth.Revoked.Count.ShouldBeGreaterThanOrEqualTo(1);
            PublishResult blocked = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            blocked.Status.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExpiredAccessToken_RefreshesAndSucceeds()
    {
        var (factory, root, oauth, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory, accessToken: "stale-access", expiresInSeconds: -60);
            drive.OnlyAcceptTokens.Add("access-1");
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Status.ShouldBe(HttpStatusCode.OK);
            oauth.RefreshCalls.ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record PublishResult(
        HttpStatusCode Status,
        string[] Outcomes,
        string Body)
    {
        public string Outcome(string report) =>
            Outcomes.Single(o => o.StartsWith(report + ":", StringComparison.Ordinal)).Split(':')[1];

        public string? Detail(string report)
        {
            using JsonDocument json = JsonDocument.Parse(Body);
            foreach (JsonElement file in json.RootElement.GetProperty("files").EnumerateArray())
            {
                if (string.Equals(file.GetProperty("report").GetString(), report, StringComparison.Ordinal))
                {
                    return file.GetProperty("detail").GetString();
                }
            }

            return null;
        }

        public string? FileId(string report)
        {
            using JsonDocument json = JsonDocument.Parse(Body);
            foreach (JsonElement file in json.RootElement.GetProperty("files").EnumerateArray())
            {
                if (string.Equals(file.GetProperty("report").GetString(), report, StringComparison.Ordinal))
                {
                    return file.GetProperty("fileId").GetString();
                }
            }

            return null;
        }
    }

    private static async Task AssertNotConnectedAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage status = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status").ConfigureAwait(false);
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await status.Content.ReadAsStringAsync().ConfigureAwait(false));
        json.RootElement.GetProperty("connected").GetBoolean().ShouldBeFalse();
    }

    private static async Task<Guid> ConnectOverHttpAsync(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        Guid saveId)
    {
        using HttpResponseMessage auth = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/auth-url").ConfigureAwait(false);
        auth.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument authJson = JsonDocument.Parse(await auth.Content.ReadAsStringAsync().ConfigureAwait(false));
        string url = authJson.RootElement.GetProperty("authUrl").GetString()!;
        string state = ExtractQueryValue(url, "state");
        using HttpClient noRedirect = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage callback = await noRedirect.GetAsync(
            $"/api/reports/drive/callback?code=valid-code&state={Uri.EscapeDataString(state)}").ConfigureAwait(false);
        callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        string location = callback.Headers.Location!.ToString();
        location.ShouldContain($"/saves/{saveId:D}/reports?drive=connected");
        return saveId;
    }

    private static string ExtractQueryValue(string url, string key)
    {
        string query = new Uri(url).Query.TrimStart('?');
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        throw new InvalidOperationException($"Query value '{key}' not found.");
    }

    private static async Task SeedConnectionAsync(
        WebApplicationFactory<Program> factory,
        string accessToken = "access-seed",
        long expiresInSeconds = 3600)
    {
        DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
        await store.SaveConnectionAsync(new DrivePublishingStore.ConnectionDocument(
            "refresh-1", null, DriveScopes.DriveFile, DateTimeOffset.UtcNow,
            accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds)), CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task PutDestinationAsync(HttpClient client, Guid saveId, string json)
    {
        using HttpResponseMessage response = await PutJsonAsync(client, $"/api/saves/{saveId:D}/reports/drive/destination", json).ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> PutJsonAsync(HttpClient client, string url, string json) =>
        client.PutAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<PublishResult> PostPublishAsync(
        HttpClient client,
        Guid saveId,
        bool confirmCrossSave,
        bool allowOverwrite)
    {
        string body = JsonSerializer.Serialize(new { confirmCrossSave, allowOverwriteEditedRemote = allowOverwrite });
        using HttpResponseMessage response = await client.PostAsync(
            $"/api/saves/{saveId:D}/reports/drive/publish",
            new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new PublishResult(response.StatusCode, [], text);
        }

        using JsonDocument json = JsonDocument.Parse(text);
        List<string> outcomes = json.RootElement.GetProperty("files").EnumerateArray()
            .Select(f => $"{f.GetProperty("report").GetString()}:{f.GetProperty("outcome").GetString()}")
            .ToList();
        return new PublishResult(response.StatusCode, [.. outcomes], text);
    }

    private static async Task AssertStoredMatchesDownloadAsync(
        HttpClient client,
        Guid saveId,
        FakeDriveClient drive)
    {
        using HttpResponseMessage status = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status").ConfigureAwait(false);
        using JsonDocument statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync().ConfigureAwait(false));
        int season = statusJson.RootElement.GetProperty("currentSeason").GetInt32();
        string seasonsId = drive.FoldersFor("folder-mtshistory", "Seasons").Single();
        string dossierId = drive.FileIn(seasonsId, $"season-{season}-dossier.md").Single();
        string almanacId = drive.FileIn("folder-mtshistory", null).Single(id => drive.Files[id].Name.EndsWith(".md", StringComparison.Ordinal));
        using HttpResponseMessage dossier = await client.GetAsync($"/api/saves/{saveId:D}/reports/season/{season}?format=md").ConfigureAwait(false);
        using HttpResponseMessage almanac = await client.GetAsync($"/api/saves/{saveId:D}/reports/almanac?format=md").ConfigureAwait(false);
        Normalize(await dossier.Content.ReadAsStringAsync().ConfigureAwait(false)).ShouldBe(Normalize(drive.Files[dossierId].Content));
        Normalize(await almanac.Content.ReadAsStringAsync().ConfigureAwait(false)).ShouldBe(Normalize(drive.Files[almanacId].Content));
    }

    private static string Normalize(string markdown)
    {
        StringBuilder builder = new();
        foreach (string line in markdown.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("Generated:", StringComparison.Ordinal))
            {
                builder.AppendLine(line.TrimEnd());
            }
        }

        return builder.ToString();
    }

    private static async Task<(long Rng, long Stream, int Standings)> ReadRngAndStandingsAsync(string root, Guid saveId)
    {
        string path = Path.Combine(root, "saves", $"{saveId:N}.db");
        var options = new DbContextOptionsBuilder<SaveDbContext>().UseSqlite($"Data Source={path}").Options;
        using var context = new SaveDbContext(options);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        int standings = await context.SeasonStandings.AsNoTracking().CountAsync().ConfigureAwait(false);
        return (rng.State, rng.Stream, standings);
    }

    private static (WebApplicationFactory<Program> Factory, string Root, FakeOAuthClient OAuth, FakeDriveClient Drive) CreateFactory(
        bool configured = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-drive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FakeOAuthClient oauth = new();
        FakeDriveClient drive = new();
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
                services.Configure<DrivePublishingOptions>(o =>
                {
                    o.StorePath = Path.Combine(root, "drive-publishing");
                    o.GoogleClientId = configured ? "test-client" : string.Empty;
                    o.GoogleClientSecret = configured ? "test-secret" : string.Empty;
                });
                services.AddSingleton<IGoogleOAuthClient>(oauth);
                services.AddSingleton<IGoogleDriveClient>(drive);
            }));
        return (factory, root, oauth, drive);
    }

    private sealed class FakeOAuthClient : IGoogleOAuthClient
    {
        public List<string> Revoked { get; } = [];

        public int RefreshCalls { get; private set; }

        public string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string codeChallenge) =>
            $"https://accounts.google.test/auth?client_id={clientId}&state={Uri.EscapeDataString(state)}&challenge={codeChallenge}";

        public Task<OAuthTokenResponse> ExchangeCodeAsync(
            string clientId,
            string clientSecret,
            string redirectUri,
            string code,
            string codeVerifier,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OAuthTokenResponse("access-initial", 3600, "refresh-1", DriveScopes.DriveFile));

        public Task<OAuthTokenResponse> RefreshAsync(
            string clientId,
            string clientSecret,
            string refreshToken,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            if (string.Equals(refreshToken, "bad-refresh", StringComparison.Ordinal))
            {
                throw new DriveNotConnectedException("expired");
            }

            return Task.FromResult(new OAuthTokenResponse($"access-{RefreshCalls}", 3600, refreshToken, DriveScopes.DriveFile));
        }

        public Task<bool> TryRevokeAsync(string token, CancellationToken cancellationToken = default)
        {
            Revoked.Add(token);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeDriveClient : IGoogleDriveClient
    {
        public readonly Dictionary<string, FakeFile> Files = new(StringComparer.Ordinal);

        public readonly HashSet<string> FailUpdatesFor = new(StringComparer.Ordinal);

        public readonly HashSet<string> OnlyAcceptTokens = new(StringComparer.Ordinal);

        public bool FailNextCreate { get; set; }

        private int _nextId;

        private static long _stamp;

        public void SeedFile(string id, string parentId, string name, string content)
        {
            Files[id] = new FakeFile(id, name, "text/markdown", [parentId], content, Stamp(), false, $"https://drive.test/file/{id}");
        }

        public IReadOnlyList<string> FoldersFor(string parentId, string name) =>
            Files.Values
                .Where(f => !f.Trashed
                    && string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal)
                    && f.Parents.Contains(parentId, StringComparer.Ordinal)
                    && string.Equals(f.Name, name, StringComparison.Ordinal))
                .Select(f => f.Id)
                .ToList();

        public IReadOnlyList<string> FileIn(string parentId, string? name) =>
            Files.Values
                .Where(f => !f.Trashed
                    && f.Parents.Contains(parentId, StringComparer.Ordinal)
                    && (name is null || string.Equals(f.Name, name, StringComparison.Ordinal)))
                .Select(f => f.Id)
                .ToList();

        public Task<DriveFile?> GetFileAsync(string accessToken, string fileId, CancellationToken cancellationToken = default)
        {
            CheckToken(accessToken);
            Files.TryGetValue(fileId, out FakeFile? file);
            return Task.FromResult<DriveFile?>(file?.ToDriveFile());
        }

        public Task<string?> DownloadFileAsync(string accessToken, string fileId, CancellationToken cancellationToken = default)
        {
            CheckToken(accessToken);
            Files.TryGetValue(fileId, out FakeFile? file);
            return Task.FromResult(file?.Content);
        }

        public Task<IReadOnlyList<DriveFile>> ListByNameInFolderAsync(
            string accessToken,
            string folderId,
            string name,
            CancellationToken cancellationToken = default)
        {
            CheckToken(accessToken);
            IReadOnlyList<DriveFile> matches = Files.Values
                .Where(f => !f.Trashed
                    && f.Parents.Contains(folderId, StringComparer.Ordinal)
                    && string.Equals(f.Name, name, StringComparison.Ordinal))
                .Select(f => f.ToDriveFile())
                .ToList();
            return Task.FromResult(matches);
        }

        public Task<DriveFile?> FindFolderAsync(
            string accessToken,
            string parentFolderId,
            string name,
            CancellationToken cancellationToken = default)
        {
            CheckToken(accessToken);
            List<DriveFile> folders = Files.Values
                .Where(f => !f.Trashed
                    && string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal)
                    && f.Parents.Contains(parentFolderId, StringComparer.Ordinal)
                    && string.Equals(f.Name, name, StringComparison.Ordinal))
                .Select(f => f.ToDriveFile())
                .ToList();
            return folders.Count switch
            {
                0 => Task.FromResult<DriveFile?>(null),
                1 => Task.FromResult<DriveFile?>(folders[0]),
                _ => throw new DriveConflictException("ambiguous"),
            };
        }

        public Task<DriveFile> CreateFolderAsync(
            string accessToken,
            string parentFolderId,
            string name,
            CancellationToken cancellationToken = default)
        {
            CheckToken(accessToken);
            string id = $"folder-{++_nextId}";
            FakeFile file = new(id, name, "application/vnd.google-apps.folder", [parentFolderId], string.Empty, Stamp(), false, $"https://drive.test/file/{id}");
            Files[id] = file;
            return Task.FromResult(file.ToDriveFile());
        }

        public Task<DriveFile> CreateMarkdownAsync(
            string accessToken,
            string folderId,
            string name,
            string content,
            CancellationToken cancellationToken = default)
        {
            CheckToken(accessToken);
            string id = $"file-{++_nextId}";
            FakeFile file = new(id, name, "text/markdown", [folderId], content, Stamp(), false, $"https://drive.test/file/{id}");
            Files[id] = file;
            if (FailNextCreate)
            {
                FailNextCreate = false;
                throw new DriveUpstreamException("simulated create timeout after apply");
            }

            return Task.FromResult(file.ToDriveFile());
        }

        public Task<DriveFile> UpdateContentAsync(
            string accessToken,
            string fileId,
            string content,
            string? newName,
            CancellationToken cancellationToken = default)
        {
            CheckToken(accessToken);
            if (FailUpdatesFor.Contains(fileId))
            {
                throw new DriveUpstreamException("simulated update failure");
            }

            FakeFile file = Files[fileId];
            FakeFile updated = file with { Content = content, Name = newName ?? file.Name, ModifiedTime = Stamp() };
            Files[fileId] = updated;
            return Task.FromResult(updated.ToDriveFile());
        }

        private void CheckToken(string accessToken)
        {
            if (OnlyAcceptTokens.Count > 0 && !OnlyAcceptTokens.Contains(accessToken))
            {
                throw new DriveApiException(HttpStatusCode.Unauthorized, "stale token");
            }
        }

        private static string Stamp() => $"2026-01-01T00:00:{Interlocked.Increment(ref _stamp):D4}Z";

        public sealed record FakeFile(
            string Id,
            string Name,
            string MimeType,
            List<string> Parents,
            string Content,
            string ModifiedTime,
            bool Trashed,
            string WebViewLink)
        {
            public DriveFile ToDriveFile() =>
                new(Id, Name, MimeType, Parents, ModifiedTime, null, Trashed, WebViewLink);
        }
    }
}
