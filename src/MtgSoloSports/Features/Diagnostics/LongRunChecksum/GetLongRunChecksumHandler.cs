using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Diagnostics.LongRunChecksum;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Computes the stable
/// long-run sporting fingerprint from normalized columns only: ordered round
/// checksums, stage/season standing fingerprints, season memberships and RNG
/// state. Never selects <c>Rounds.PayloadJson</c> and never decompresses round
/// payloads, so the checksum stays practical for hundreds of seasons.
/// Read-only: no lock, no RNG mutation.
/// </summary>
public sealed class GetLongRunChecksumHandler
{
    private readonly SaveStore _store;

    public GetLongRunChecksumHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetLongRunChecksumResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);

        Dictionary<int, int> seasonNumbers = await context.Seasons
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber, cancellationToken)
            .ConfigureAwait(false);

        List<LongRunChecksumCalculator.SeasonInput> rounds = await LoadRoundsAsync(context, seasonNumbers, cancellationToken).ConfigureAwait(false);
        List<LongRunChecksumCalculator.StageInput> stages = await LoadStagesAsync(context, seasonNumbers, cancellationToken).ConfigureAwait(false);
        List<LongRunChecksumCalculator.SeasonStandingInput> finals = await LoadFinalsAsync(context, seasonNumbers, cancellationToken).ConfigureAwait(false);
        List<LongRunChecksumCalculator.MembershipInput> memberships = await LoadMembershipsAsync(context, seasonNumbers, cancellationToken).ConfigureAwait(false);
        (ulong rngState, ulong rngStream) = await LoadRngAsync(context, cancellationToken).ConfigureAwait(false);

        string checksum = LongRunChecksumCalculator.Compute(rounds, stages, finals, memberships, rngState, rngStream);
        IReadOnlyList<(int SeasonNumber, string Checksum)> perSeason =
            LongRunChecksumCalculator.ComputePerSeason(rounds, stages, finals, memberships);

        return new GetLongRunChecksumResponse(
            saveId,
            seasonNumbers.Count,
            rounds.Count,
            stages.Count,
            finals.Count,
            rngState,
            rngStream,
            checksum,
            perSeason.Select(e => new LongRunSeasonChecksumEntry(e.SeasonNumber, e.Checksum)).ToList());
    }

    internal static async Task<List<LongRunChecksumCalculator.SeasonInput>> LoadRoundsAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        CancellationToken cancellationToken)
    {
        List<RoundProbe> rows = await context.Rounds
            .AsNoTracking()
            .Select(e => new RoundProbe(e.SeasonId, e.LeagueId, e.StageNumber, e.RoundNumber, e.PayloadChecksum))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<LongRunChecksumCalculator.SeasonInput> inputs = new(rows.Count);
        foreach (RoundProbe row in rows)
        {
            if (!seasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Round references unknown season {row.SeasonId}.");
            }

            inputs.Add(new LongRunChecksumCalculator.SeasonInput(
                seasonNumber, row.LeagueId, row.StageNumber, row.RoundNumber, row.PayloadChecksum));
        }

        return inputs;
    }

    internal static async Task<List<LongRunChecksumCalculator.StageInput>> LoadStagesAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        CancellationToken cancellationToken)
    {
        List<StageProbe> rows = await context.StageStandings
            .AsNoTracking()
            .Select(e => new StageProbe(
                e.SeasonId, e.LeagueId, e.StageNumber, e.StageRank, e.SaveAthleteId,
                e.StageScoreThousandths, e.ChampionshipPointsThousandths, e.EarnedBonusThousandths))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<LongRunChecksumCalculator.StageInput> inputs = new(rows.Count);
        foreach (StageProbe row in rows)
        {
            if (!seasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Stage standing references unknown season {row.SeasonId}.");
            }

            inputs.Add(new LongRunChecksumCalculator.StageInput(
                seasonNumber, row.LeagueId, row.StageNumber, row.StageRank, row.SaveAthleteId,
                row.StageScoreThousandths, row.ChampionshipPointsThousandths, row.EarnedBonusThousandths));
        }

        return inputs;
    }

    internal static async Task<List<LongRunChecksumCalculator.SeasonStandingInput>> LoadFinalsAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        CancellationToken cancellationToken)
    {
        List<FinalProbe> rows = await context.SeasonStandings
            .AsNoTracking()
            .Select(e => new FinalProbe(
                e.SeasonId, e.LeagueId, e.SeasonRank, e.SaveAthleteId,
                e.TotalChampionshipPointsThousandths, e.TotalStageScoreThousandths))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<LongRunChecksumCalculator.SeasonStandingInput> inputs = new(rows.Count);
        foreach (FinalProbe row in rows)
        {
            if (!seasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Season standing references unknown season {row.SeasonId}.");
            }

            inputs.Add(new LongRunChecksumCalculator.SeasonStandingInput(
                seasonNumber, row.LeagueId, row.SeasonRank, row.SaveAthleteId,
                row.TotalChampionshipPointsThousandths, row.TotalStageScoreThousandths));
        }

        return inputs;
    }

    internal static async Task<List<LongRunChecksumCalculator.MembershipInput>> LoadMembershipsAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        CancellationToken cancellationToken)
    {
        List<MembershipProbe> rows = await context.SeasonMemberships
            .AsNoTracking()
            .Select(e => new MembershipProbe(e.SeasonId, e.SaveAthleteId, e.LeagueId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<LongRunChecksumCalculator.MembershipInput> inputs = new(rows.Count);
        foreach (MembershipProbe row in rows)
        {
            if (!seasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Membership references unknown season {row.SeasonId}.");
            }

            inputs.Add(new LongRunChecksumCalculator.MembershipInput(seasonNumber, row.SaveAthleteId, row.LeagueId));
        }

        return inputs;
    }

    internal static async Task<(ulong State, ulong Stream)> LoadRngAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        RngStateEntity row = await context.RngStates
            .AsNoTracking()
            .SingleAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        unchecked
        {
            return ((ulong)row.State, (ulong)row.Stream);
        }
    }

    private sealed record RoundProbe(int SeasonId, int LeagueId, int StageNumber, int RoundNumber, string PayloadChecksum);

    private sealed record StageProbe(
        int SeasonId, int LeagueId, int StageNumber, int StageRank, int SaveAthleteId,
        int StageScoreThousandths, int ChampionshipPointsThousandths, int EarnedBonusThousandths);

    private sealed record FinalProbe(
        int SeasonId, int LeagueId, int SeasonRank, int SaveAthleteId,
        int TotalChampionshipPointsThousandths, int TotalStageScoreThousandths);

    private sealed record MembershipProbe(int SeasonId, int SaveAthleteId, int? LeagueId);
}
