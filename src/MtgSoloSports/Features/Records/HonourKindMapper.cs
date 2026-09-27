using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records;

/// <summary>
/// Maps league kinds to honour kinds. Superleague leagues yield
/// <see cref="HonourKind.SuperleagueTitle"/>; feeder leagues yield
/// <see cref="HonourKind.FeederTitle"/>. Unknown kinds abort; never silently skipped.
/// </summary>
public static class HonourKindMapper
{
    public static HonourKind FromLeagueKind(int leagueKind)
    {
        return leagueKind == (int)LeagueKind.Superleague
            ? HonourKind.SuperleagueTitle
            : HonourKind.FeederTitle;
    }
}
