using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Superleague;

/// <summary>
/// MSS-150 golden equivalence: pins every persisted byte of the Superleague
/// qualifier path for a dedicated seed so the qualifier-engine unification
/// provably changes nothing. The values below were captured from the
/// pre-refactor engines; they must stay unchanged and must never be
/// re-captured on failure. The RNG state also proves the RNG row commits in
/// the same transaction as the results.
/// </summary>
public sealed class SuperleagueQualifierGoldenEquivalenceTests
{
    private const string Checksum = "43246d93d02e0496e6b0fe9012733dd1ba1ada7333fac5b4169f1e6528468bfb";
    private const string PayloadHash = "e1fd063482cf16fa4a2c92331fcad17d884a8b303160f15c2392b01a377cdf75";
    private const string JsonPrefix = "{\"version\":1,\"rulesVersion\":3,\"fromSeasonNumber\":2,\"toSeasonNumber\":3,\"roundNumber\":1,\"rngBeforeState\":5936284276031871404";
    private const ulong RngBeforeState = 5936284276031871404UL;
    private const ulong RngBeforeStream = 9393UL;
    private const ulong RngAfterState = 9697511563622950044UL;
    private const ulong RngAfterStream = 9393UL;
    private const long RngRowState = -8749232510086601572L;
    private const string Standings =
        "1:1542:566432:532000:4:True:1:43:3|2:771:537629:506000:1:True:1:34:3|3:1027:515000:515000:2:True:1:37:3|4:770:471000:471000:1:True:1:34:2|5:1061:449168:419000:0:True:0:49:17|6:519:440000:440000:1:True:1:31:4|7:1420:436028:407000:0:True:0:49:23|8:1098:435907:407000:2:True:0:49:18|9:1028:410000:410000:1:False:1:37:4|10:1327:389961:364000:0:False:0:49:22|11:261:384000:384000:0:False:1:28:3|12:1285:377000:377000:0:False:1:40:4|13:1282:366000:366000:0:False:1:40:2|14:1498:364736:340000:0:False:0:49:24|15:1026:363726:345000:0:False:1:37:2|16:773:362000:362000:0:False:1:34:4|17:1543:360000:360000:1:False:1:43:4|18:6:354000:354000:1:False:1:25:4|19:262:338000:338000:1:False:1:28:4|20:1797:328161:311000:0:False:1:46:4|21:1794:319000:319000:1:False:1:46:2|22:514:304000:304000:0:False:1:31:2|23:259:277000:277000:0:False:1:28:2|24:1796:272000:272000:0:False:1:46:3|25:1279:270692:253000:0:False:0:49:20|26:2:270000:270000:0:False:1:25:2|27:1283:265262:250000:0:False:1:40:3|28:1289:260872:244000:0:False:0:49:21|29:1175:242163:227000:0:False:0:49:19|30:1540:241000:241000:0:False:1:43:2|31:515:225000:225000:0:False:1:31:3|32:3:198000:198000:0:False:1:25:3";

    [Fact]
    public async Task SuperleaguePath_MatchesPreRefactorGolden()
    {
        var (store, root, saveId) = await PostseasonTestSaves.PrepareQualifierAsync(8282UL, 9393UL);
        try
        {
            RunQualifierResponse response = await new RunQualifierHandler(store).HandleAsync(saveId);

            response.QualifierSize.ShouldBe(32);
            response.Rounds.ShouldBe(16);
            response.Winners.ShouldBe(8);
            response.Standings.Count.ShouldBe(32);
            response.Checksum.ShouldBe(Checksum);
            response.RngBeforeState.ShouldBe(RngBeforeState);
            response.RngBeforeStream.ShouldBe(RngBeforeStream);
            response.RngAfterState.ShouldBe(RngAfterState);
            response.RngAfterStream.ShouldBe(RngAfterStream);

            (long rowState, long rowStream) = await PostseasonTestSaves.LoadRngAsync(store, saveId);
            rowState.ShouldBe(RngRowState);
            rowStream.ShouldBe(9393L);

            (await HashPayloadsAsync(store, saveId, QualifierBoundary.Superleague, -1)).ShouldBe(PayloadHash);
            (await StandingsFingerprintAsync(store, saveId, QualifierBoundary.Superleague, -1)).ShouldBe(Standings);

            // The Superleague payload never carries boundary/color keys (the
            // identity lives on the entity columns) and keeps its exact
            // pre-refactor field order.
            string firstJson = await FirstPayloadJsonAsync(store, saveId, QualifierBoundary.Superleague, -1);
            firstJson.StartsWith(JsonPrefix, StringComparison.Ordinal).ShouldBeTrue();
            firstJson.ShouldNotContain("boundary");
            firstJson.ShouldNotContain("sportingColor");
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    internal static async Task<string> HashPayloadsAsync(SaveStore store, Guid saveId, QualifierBoundary boundary, int color)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        (int sourceId, int nextId) = await LoadTransitionIdsAsync(context).ConfigureAwait(false);
        List<string> payloads = await context.QualifierRounds
            .AsNoTracking()
            .Where(e => e.FromSeasonId == sourceId && e.ToSeasonId == nextId
                && e.QualifierBoundary == (int)boundary && e.QualifierSportingColor == color)
            .OrderBy(e => e.RoundNumber)
            .Select(e => e.PayloadJson)
            .ToListAsync()
            .ConfigureAwait(false);
        payloads.Count.ShouldBe(16);
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (string json in payloads)
        {
            builder.Append(json);
            builder.Append('\n');
        }

        return Convert.ToHexStringLower(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    internal static async Task<string> StandingsFingerprintAsync(SaveStore store, Guid saveId, QualifierBoundary boundary, int color)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        (int sourceId, int nextId) = await LoadTransitionIdsAsync(context).ConfigureAwait(false);
        List<QualifierStandingEntity> standings = await context.QualifierStandings
            .AsNoTracking()
            .Where(e => e.FromSeasonId == sourceId && e.ToSeasonId == nextId
                && e.QualifierBoundary == (int)boundary && e.QualifierSportingColor == color)
            .OrderBy(e => e.QualifierRank)
            .ToListAsync()
            .ConfigureAwait(false);
        standings.Count.ShouldBe(boundary == QualifierBoundary.Superleague ? 32 : 16);
        return string.Join("|", standings.Select(s =>
            $"{s.QualifierRank}:{s.SaveAthleteId}:{s.QualifierScoreThousandths}:{s.BaseScoreThousandths}:{s.RoundWins}:{s.IsQualified}:{s.Role}:{s.FromLeagueId}:{s.FromSeasonRank}"));
    }

    internal static async Task<string> FirstPayloadJsonAsync(SaveStore store, Guid saveId, QualifierBoundary boundary, int color)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        (int sourceId, int nextId) = await LoadTransitionIdsAsync(context).ConfigureAwait(false);
        return await context.QualifierRounds
            .AsNoTracking()
            .Where(e => e.FromSeasonId == sourceId && e.ToSeasonId == nextId
                && e.QualifierBoundary == (int)boundary && e.QualifierSportingColor == color && e.RoundNumber == 1)
            .Select(e => e.PayloadJson)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    internal static async Task<(int SourceId, int NextId)> LoadTransitionIdsAsync(SaveDbContext context)
    {
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);
        return (source.Id, next.Id);
    }
}
