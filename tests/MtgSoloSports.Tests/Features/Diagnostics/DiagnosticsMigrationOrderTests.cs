using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Diagnostics;

/// <summary>
/// MSS-144 ordering proof for the diagnostics handlers: the save-schema
/// migration (whose rollback path can replace the save file) must run before
/// any <c>SaveDbContext</c> handle is opened, matching every other handler.
/// The ordering is pinned on the handler source (the same deterministic
/// source-assertion technique as the Athlete record holdings guard) because
/// the rollback race itself is not reproducible without a fabricated pending
/// migration; the handlers are still exercised end-to-end by their own tests.
/// </summary>
public sealed class DiagnosticsMigrationOrderTests
{
    public static TheoryData<string> HandlerFiles => new()
    {
        "ValidateLongRunInvariantsHandler.cs",
        "GetLongRunChecksumHandler.cs",
        "GetLongRunStatsHandler.cs",
    };

    [Theory]
    [MemberData(nameof(HandlerFiles))]
    public async Task DiagnosticsHandlers_MigrateBeforeOpeningTheContext(string handlerFileName)
    {
        string path = FindHandlerSource(handlerFileName);
        string source = await File.ReadAllTextAsync(path);
        string code = StripComments(source);

        int migrationIndex = code.IndexOf("_store.EnsureMigratedAsync(", StringComparison.Ordinal);
        int contextIndex = code.IndexOf("_store.OpenDbContext(", StringComparison.Ordinal);
        migrationIndex.ShouldBeGreaterThan(0, $"{handlerFileName} must call EnsureMigratedAsync");
        contextIndex.ShouldBeGreaterThan(0, $"{handlerFileName} must open its context via OpenDbContext");
        migrationIndex.ShouldBeLessThan(
            contextIndex,
            $"{handlerFileName} must migrate before opening the context: the migration-rollback path can replace the save file while a context handle exists.");
    }

    private static string FindHandlerSource(string fileName)
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            string[] matches = Directory.GetFiles(directory.FullName, fileName, SearchOption.AllDirectories);
            if (matches.Length > 0)
            {
                return matches[0];
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Handler source file '{fileName}' was not found.");
    }

    private static string StripComments(string source)
    {
        System.Text.StringBuilder builder = new(source.Length);
        bool inLineComment = false;
        bool inBlockComment = false;
        for (int i = 0; i < source.Length; i++)
        {
            char current = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (inLineComment)
            {
                if (current == '\n')
                {
                    inLineComment = false;
                    _ = builder.Append(current);
                }
                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    i += 1;
                }
                continue;
            }

            if (current == '/' && next == '/')
            {
                inLineComment = true;
                i += 1;
                continue;
            }

            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                i += 1;
                continue;
            }

            _ = builder.Append(current);
        }

        return builder.ToString();
    }
}
