using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.GetColorCupTeamResult;
using MtgSoloSports.Features.Cups.GetTypeCupTeamResult;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class CupResultImageUrlTests
{
    [Fact]
    public async Task ColorCupResults_CarryCardArt_AndKeepChecksums()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(6161UL, 7272UL);
        try
        {
            await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            await new RunColorCupTeamHandler(store).HandleAsync(saveId);
            string individualChecksum = (await new GetColorCupIndividualResultHandler(store).HandleAsync(saveId)).Checksum;
            string teamChecksum = (await new GetColorCupTeamResultHandler(store).HandleAsync(saveId)).Checksum;
            await StampImagesAsync(store, saveId);

            GetColorCupIndividualResultResponse individual = await new GetColorCupIndividualResultHandler(store).HandleAsync(saveId);
            individual.Standings.Count.ShouldBe(32);
            individual.Standings.ShouldAllBe(m => m.ImageUrl == ImageFor(m.AthleteId));
            individual.Checksum.ShouldBe(individualChecksum);

            GetColorCupTeamResultResponse team = await new GetColorCupTeamResultHandler(store).HandleAsync(saveId);
            team.Legs.Count.ShouldBe(32);
            team.Legs.ShouldAllBe(l => l.ImageUrl == ImageFor(l.AthleteId));
            team.Checksum.ShouldBe(teamChecksum);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TypeCupLegs_CarryCardArt_AndNullStaysNull()
    {
        var (store, root, saveId) = await PrepareTypeCupAsync(6262UL, 7373UL);
        try
        {
            await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);
            string checksum = (await new GetTypeCupTeamResultHandler(store).HandleAsync(saveId, 2)).Checksum;
            int withoutArt;
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                withoutArt = await context.TypeCupSelections.OrderBy(e => e.Id).Select(e => e.SaveAthleteId).FirstAsync();
            }

            await StampImagesAsync(store, saveId, withoutArt);

            GetTypeCupTeamResultResponse team = await new GetTypeCupTeamResultHandler(store).HandleAsync(saveId, 2);
            team.Legs.Count.ShouldBe(8);
            team.Legs.Single(l => l.AthleteId == withoutArt).ImageUrl.ShouldBeNull();
            team.Legs.Where(l => l.AthleteId != withoutArt).ShouldAllBe(l => l.ImageUrl == ImageFor(l.AthleteId));
            team.Checksum.ShouldBe(checksum);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string ImageFor(int athleteId) => $"https://img.test/{athleteId}.jpg";

    private static async Task StampImagesAsync(SaveStore store, Guid saveId, int? withoutArt = null)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        foreach (SaveAthleteEntity athlete in await context.SaveAthletes.ToListAsync().ConfigureAwait(false))
        {
            athlete.ImageUrl = athlete.Id == withoutArt ? null : ImageFor(athlete.Id);
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }
}
