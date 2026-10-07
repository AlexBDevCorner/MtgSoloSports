using MtgSoloSports.Features.Cups.PlayColorCupIndividualRound;
using MtgSoloSports.Features.Cups.PlayColorCupTeamRound;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// A partly played postseason event owns the save RNG until it completes:
/// playing another event in between would move the RNG under it and leave the
/// save permanently stuck, so it is refused as a conflict.
/// </summary>
public sealed class PostseasonEventOrderTests
{
    [Fact]
    public async Task Team_WhileIndividualInProgress_ConflictsAndIndividualStillCompletes()
    {
        // MSS-067: shared Color Cup template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-eventorder-");
        try
        {
            PlayColorCupIndividualRoundHandler individualStep = new(store);
            for (int round = 1; round <= 3; round++)
            {
                await individualStep.HandleAsync(saveId);
            }

            (long, long) rngBefore = await LoadRngAsync(store, saveId);
            await Should.ThrowAsync<RunColorCupTeamConflictException>(() => new PlayColorCupTeamRoundHandler(store).HandleAsync(saveId));
            await Should.ThrowAsync<RunColorCupTeamConflictException>(() => new RunColorCupTeamHandler(store).HandleAsync(saveId));
            (await LoadRngAsync(store, saveId)).ShouldBe(rngBefore);

            await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Individual_WhileTeamInProgress_Conflicts()
    {
        // MSS-067: shared Color Cup template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-eventorder-");
        try
        {
            PlayColorCupTeamRoundHandler teamStep = new(store);
            await teamStep.HandleAsync(saveId);
            await teamStep.HandleAsync(saveId);

            await Should.ThrowAsync<RunColorCupIndividualConflictException>(() => new PlayColorCupIndividualRoundHandler(store).HandleAsync(saveId));
            await Should.ThrowAsync<RunColorCupIndividualConflictException>(() => new RunColorCupIndividualHandler(store).HandleAsync(saveId));

            await new RunColorCupTeamHandler(store).HandleAsync(saveId);
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
