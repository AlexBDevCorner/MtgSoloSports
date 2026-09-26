using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Athletes.Projections;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads one athlete's career
/// profile from transactional projections plus the save-owned card snapshot.
/// Never decompresses round payloads; the normal path touches only
/// <c>SaveAthletes</c> + <c>AthleteCareers</c> + <c>AthleteSeasonSummaries</c>.
/// When projections are missing (saves created before MSS-013), falls back to
/// an in-memory rebuild from normalized standings/memberships for that single
/// athlete — still without round payloads — so old saves remain readable while
/// new writes stay transactional. Corrupt references abort; missing athletes 404.
/// </summary>
public sealed class GetAthleteProfileHandler
{
    private readonly SaveStore _store;

    public GetAthleteProfileHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetAthleteProfileResponse> HandleAsync(Guid saveId, int athleteId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (athleteId <= 0)
        {
            throw new ArgumentException("Athlete id must be positive.", nameof(athleteId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SaveAthleteEntity athlete = await LoadAthleteAsync(context, saveId, athleteId, cancellationToken).ConfigureAwait(false);
        AthleteCareerEntity? career = await LoadCareerAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        List<AthleteSeasonSummaryEntity> summaries = await LoadSummariesAsync(context, athleteId, cancellationToken).ConfigureAwait(false);

        if (career is null || await IsIncompleteAsync(context, summaries, cancellationToken).ConfigureAwait(false))
        {
            return await BuildFallbackAsync(context, saveId, athlete, cancellationToken).ConfigureAwait(false);
        }

        return MapPersisted(saveId, athlete, career, summaries);
    }

    internal static async Task<SaveAthleteEntity> LoadAthleteAsync(
        SaveDbContext context,
        Guid saveId,
        int athleteId,
        CancellationToken cancellationToken)
    {
        SaveAthleteEntity? athlete = await context.SaveAthletes
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == athleteId, cancellationToken)
            .ConfigureAwait(false);
        if (athlete is null)
        {
            throw new AthleteProfileNotFoundException($"Athlete {athleteId} does not exist in save '{saveId:D}'.");
        }

        return athlete;
    }

    internal static Task<AthleteCareerEntity?> LoadCareerAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        return context.AthleteCareers
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SaveAthleteId == athleteId, cancellationToken);
    }

    internal static Task<List<AthleteSeasonSummaryEntity>> LoadSummariesAsync(
        SaveDbContext context,
        int athleteId,
        CancellationToken cancellationToken)
    {
        return context.AthleteSeasonSummaries
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken);
    }

    internal static async Task<bool> IsIncompleteAsync(
        SaveDbContext context,
        List<AthleteSeasonSummaryEntity> summaries,
        CancellationToken cancellationToken)
    {
        List<int> seasonIds = await context.Seasons
            .AsNoTracking()
            .Select(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (summaries.Count != seasonIds.Count)
        {
            return true;
        }

        HashSet<int> covered = summaries.Select(s => s.SeasonId).ToHashSet();
        return !seasonIds.All(covered.Contains);
    }

    internal static GetAthleteProfileResponse MapPersisted(
        Guid saveId,
        SaveAthleteEntity athlete,
        AthleteCareerEntity career,
        List<AthleteSeasonSummaryEntity> summaries)
    {
        AthleteCardDto card = MapCard(athlete);
        AthleteCareerDto careerDto = MapCareer(career);
        List<AthleteSeasonDto> seasons = summaries
            .OrderBy(s => s.SeasonNumber)
            .Select(MapSeason)
            .ToList();
        return new GetAthleteProfileResponse(saveId, athlete.Id, card, careerDto, seasons);
    }

    internal static async Task<GetAthleteProfileResponse> BuildFallbackAsync(
        SaveDbContext context,
        Guid saveId,
        SaveAthleteEntity athlete,
        CancellationToken cancellationToken)
    {
        // Single-athlete rebuild without persisting and without round payloads.
        // Uses the same pure calculator as transactional writes.
        RulesV1Snapshot snapshot = await LoadRulesSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        AthleteProjectionUpdater.AthleteHistory history =
            await AthleteProjectionUpdater.LoadHistoryAsync(context, athlete.Id, cancellationToken).ConfigureAwait(false);
        return MapFallback(saveId, athlete, history, snapshot.Rules);
    }

    internal sealed record RulesV1Snapshot(SimulationKernel.Rules.RulesV1 Rules);

    internal static async Task<RulesV1Snapshot> LoadRulesSnapshotAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        return new RulesV1Snapshot(rules);
    }

    internal static GetAthleteProfileResponse MapFallback(
        Guid saveId,
        SaveAthleteEntity athlete,
        AthleteProjectionUpdater.AthleteHistory history,
        SimulationKernel.Rules.RulesV1 rules)
    {
        Dictionary<int, SeasonMembershipEntity> membershipBySeason = history.Memberships.ToDictionary(e => e.SeasonId);
        Dictionary<int, SeasonStandingEntity> finalBySeason = history.SeasonRows.ToDictionary(e => e.SeasonId);
        List<AthleteSeasonSummaryEntity> summaries = [];
        foreach (SeasonEntity season in history.Seasons.OrderBy(s => s.SeasonNumber))
        {
            membershipBySeason.TryGetValue(season.Id, out SeasonMembershipEntity? membership);
            finalBySeason.TryGetValue(season.Id, out SeasonStandingEntity? final);
            List<StageStandingEntity> seasonStages = history.StageRows.Where(r => r.SeasonId == season.Id).ToList();
            summaries.Add(AthleteProjectionUpdater.BuildSeasonSummary(
                season, membership, final, seasonStages, history.LeaguesById));
        }

        AthleteCareerEntity career = AthleteProjectionUpdater.BuildCareer(athlete.Id, history, rules);
        return MapPersisted(saveId, athlete, career, summaries);
    }

    internal static AthleteCardDto MapCard(SaveAthleteEntity athlete)
    {
        List<string>? types = JsonSerializer.Deserialize<List<string>>(athlete.CreatureTypesJson);
        SportingColor color = (SportingColor)athlete.SportingColor;
        return new AthleteCardDto(
            athlete.Name,
            athlete.SportingColor,
            color.ToString(),
            types ?? [],
            athlete.FrontColors,
            athlete.ManaCost,
            athlete.TypeLine,
            athlete.ImageUrl,
            athlete.SetCode,
            athlete.IsArtifact,
            athlete.HasDevoid,
            athlete.HasHybridMana);
    }

    internal static AthleteCareerDto MapCareer(AthleteCareerEntity career)
    {
        checked
        {
            int podiums = career.StageWins + career.StageSeconds + career.StageThirds;
            return new AthleteCareerDto(
                career.SeasonsActive,
                career.IsActive,
                career.CurrentLeagueId,
                career.CurrentLeagueName,
                career.CurrentLeagueKind,
                career.RoundWins,
                career.StageWins,
                career.StageSeconds,
                career.StageThirds,
                podiums,
                career.BestSeasonFinish,
                career.BestSeasonNumber,
                career.LifetimeEarnedBonusThousandths,
                career.CurrentEffectiveBonusThousandths,
                career.LastSeasonNumber,
                career.LastStageNumber);
        }
    }

    internal static AthleteSeasonDto MapSeason(AthleteSeasonSummaryEntity summary)
    {
        return new AthleteSeasonDto(
            summary.SeasonNumber,
            summary.SeasonId,
            summary.WasActive,
            summary.LeagueId,
            summary.LeagueName,
            summary.LeagueKind,
            summary.RoundWins,
            summary.StageWins,
            summary.StageSeconds,
            summary.StageThirds,
            summary.SeasonRank,
            summary.IsChampion,
            summary.EarnedBonusThousandths,
            summary.TotalChampionshipPointsThousandths,
            summary.TotalStageScoreThousandths,
            summary.TotalBaseScoreThousandths);
    }
}
