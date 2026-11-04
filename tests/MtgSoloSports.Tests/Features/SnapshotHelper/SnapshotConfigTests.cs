using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.SnapshotHelper;

/// <summary>
/// MSS-133: the documented setup copies the tracked example config (which
/// carries // comments) and pastes the exact save id. Loading that shape must
/// succeed instead of failing as invalid JSON.
/// </summary>
public sealed class SnapshotConfigTests
{
    [Fact]
    public void TrackedExampleConfig_WithSaveIdPasted_LoadsDespiteComments()
    {
        string examplePath = FindExamplePath();
        string example = File.ReadAllText(examplePath);
        example.ShouldContain("//");

        Guid saveId = Guid.NewGuid();
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-config-");
        try
        {
            string configPath = Path.Combine(configDir, "snapshot.config.json");
            File.WriteAllText(configPath, example.Replace(
                "PASTE-EXACT-SAVE-ID-HERE",
                saveId.ToString("D"),
                StringComparison.Ordinal));

            SaveSnapshot.SnapshotConfig config = SaveSnapshot.SnapshotConfig.Load(configPath);

            config.SaveId.ShouldBe(saveId);
            config.BaseUrl.ShouldBe("http://localhost:8080");
            Path.IsPathRooted(config.WorkingDirectory).ShouldBeTrue();
            config.WorkingDirectory.ShouldEndWith("snapshot-data");
        }
        finally
        {
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public void TrackedExampleConfig_Placeholder_FailsOnSaveId_NotOnJson()
    {
        string examplePath = FindExamplePath();
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-config-");
        try
        {
            string configPath = Path.Combine(configDir, "snapshot.config.json");
            File.Copy(examplePath, configPath, overwrite: true);

            Should.Throw<InvalidOperationException>(() => SaveSnapshot.SnapshotConfig.Load(configPath))
                .Message.ShouldContain("saveId");
        }
        finally
        {
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    private static string FindExamplePath()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "tools", "SaveSnapshot", "snapshot.config.example.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Tracked snapshot.config.example.json was not found.");
    }
}
