using MtgSoloSports.Features.Qualifiers;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.Tests.Features;
using MtgSoloSports.Tests.Features.Superleague;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Qualifiers;

/// <summary>
/// MSS-150 golden equivalence: pins every persisted byte of the feeder
/// boundary qualifier path for a dedicated seed, covering both the F1↔F2 and
/// F2↔F3 parameterizations, so the qualifier-engine unification provably
/// changes nothing. The values below were captured from the pre-refactor
/// engines; they must stay unchanged and must never be re-captured on failure.
/// The Superleague run is canonical-order setup for the feeder events.
/// </summary>
public sealed class FeederQualifierGoldenEquivalenceTests
{
    private const string SuperleagueChecksum = "f403e5b33a9b35a64d88d7df2f260d0a4df26272570495bca582539e6b9bca84";
    private const string F1F2Checksum = "47c4e4b69a49e7795070b95fac61042a5edf07acaa01335d7d7a18de35397cdd";
    private const string F1F2PayloadHash = "03fd455220d81b339d9109c415a566c4f0b8fcf7d11b43df189fde2716bdbff5";
    private const string F2F3PayloadHash = "1e3746cd821e4a1a620ff3f94f85b118856480e8da5115bfe61de22912aafdb0";
    private const string F1F2JsonPrefix = "{\"version\":1,\"rulesVersion\":3,\"fromSeasonNumber\":2,\"toSeasonNumber\":3,\"boundary\":1,\"sportingColor\":0,\"roundNumber\":1,\"rngBeforeState\":6882008139806530994";
    private const string F2F3JsonPrefix = "{\"version\":1,\"rulesVersion\":3,\"fromSeasonNumber\":2,\"toSeasonNumber\":3,\"boundary\":2,\"sportingColor\":0,\"roundNumber\":1,";
    private const ulong F1F2RngBeforeState = 6882008139806530994UL;
    private const ulong F1F2RngAfterState = 10618843066538582498UL;
    private const long SuperleagueRngRowState = 6882008139806530994L;
    private const long FinalRngRowState = 5082927336127824050L;
    private const string F1F2Standings =
        "1:126:763743:718000:2:True:0:25:21|2:107:638389:605000:3:True:0:25:18|3:123:627612:593000:1:True:0:25:20|4:82:611238:595000:1:True:1:26:11|5:102:608730:591000:2:True:1:26:14|6:128:605241:573000:0:True:0:25:23|7:70:596207:579000:1:True:1:26:9|8:139:591789:556000:0:True:0:25:24|9:85:580625:563000:1:False:1:26:12|10:111:577384:547000:1:False:0:25:19|11:112:568899:552000:2:False:1:26:16|12:127:559387:528000:0:False:0:25:22|13:91:550476:519000:1:False:0:25:17|14:81:530001:516000:1:False:1:26:10|15:105:456472:442000:0:False:1:26:15|16:95:444728:435000:0:False:1:26:13";
    private const string F2F3Standings =
        "1:148:672275:652000:0:True:0:26:21|2:150:653643:645000:2:True:1:27:14|3:118:651418:631000:4:True:0:26:18|4:159:627947:618000:2:True:1:27:15|5:198:616860:600000:0:True:0:26:24|6:169:588090:580000:2:True:1:27:16|7:146:578650:570000:0:True:1:27:13|8:142:574345:558000:1:True:0:26:20|9:116:559221:543000:1:False:0:26:17|10:160:550124:534000:1:False:0:26:23|11:138:542010:536000:1:False:1:27:12|12:154:535071:518000:0:False:0:26:22|13:99:529161:521000:1:False:1:27:9|14:114:496950:490000:0:False:1:27:11|15:104:476783:470000:0:False:1:27:10|16:129:459440:446000:1:False:0:26:19";

    [Fact]
    public async Task FeederPath_MatchesPreRefactorGolden()
    {
        var (store, root, saveId) = await PostseasonTestSaves.PrepareQualifierAsync(6464UL, 7575UL);
        try
        {
            // Canonical order: the Superleague qualifier runs first.
            RunQualifierResponse superleague = await new RunQualifierHandler(store).HandleAsync(saveId);
            superleague.Checksum.ShouldBe(SuperleagueChecksum);
            (long superState, long _) = await PostseasonTestSaves.LoadRngAsync(store, saveId);
            superState.ShouldBe(SuperleagueRngRowState);

            // Named action under test: the single-event feeder run.
            RunFeederQualifierResponse white = await new BoundaryQualifierRunner(store).HandleAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White);
            white.QualifierSize.ShouldBe(16);
            white.Rounds.ShouldBe(16);
            white.Winners.ShouldBe(8);
            white.Checksum.ShouldBe(F1F2Checksum);
            white.RngBeforeState.ShouldBe(F1F2RngBeforeState);
            white.RngBeforeStream.ShouldBe(7575UL);
            white.RngAfterState.ShouldBe(F1F2RngAfterState);
            white.RngAfterStream.ShouldBe(7575UL);
            (await SuperleagueQualifierGoldenEquivalenceTests.HashPayloadsAsync(
                store, saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White)).ShouldBe(F1F2PayloadHash);
            (await SuperleagueQualifierGoldenEquivalenceTests.StandingsFingerprintAsync(
                store, saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White)).ShouldBe(F1F2Standings);

            // The feeder payload always carries its boundary/color identity in
            // the persisted bytes, between the season numbers and the round
            // number, exactly as before the unification.
            string f1f2Json = await SuperleagueQualifierGoldenEquivalenceTests.FirstPayloadJsonAsync(
                store, saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White);
            f1f2Json.StartsWith(F1F2JsonPrefix, StringComparison.Ordinal).ShouldBeTrue();

            // Bulk finishes the remaining 15 events (canonical order) so the
            // F2↔F3 parameterization also executes and persists.
            RunAllQualifiersResponse all = await new RunAllQualifiersHandler(store).HandleAsync(saveId);
            all.TotalStandings.ShouldBe(288);
            all.TotalRounds.ShouldBe(272);
            (long finalState, long finalStream) = await PostseasonTestSaves.LoadRngAsync(store, saveId);
            finalState.ShouldBe(FinalRngRowState);
            finalStream.ShouldBe(7575L);

            (await SuperleagueQualifierGoldenEquivalenceTests.HashPayloadsAsync(
                store, saveId, QualifierBoundary.Feeder2Feeder3, (int)SportingColor.White)).ShouldBe(F2F3PayloadHash);
            (await SuperleagueQualifierGoldenEquivalenceTests.StandingsFingerprintAsync(
                store, saveId, QualifierBoundary.Feeder2Feeder3, (int)SportingColor.White)).ShouldBe(F2F3Standings);
            string f2f3Json = await SuperleagueQualifierGoldenEquivalenceTests.FirstPayloadJsonAsync(
                store, saveId, QualifierBoundary.Feeder2Feeder3, (int)SportingColor.White);
            f2f3Json.StartsWith(F2F3JsonPrefix, StringComparison.Ordinal).ShouldBeTrue();
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }
}
