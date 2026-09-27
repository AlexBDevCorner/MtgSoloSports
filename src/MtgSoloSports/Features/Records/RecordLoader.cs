using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records;

/// <summary>
/// Loads normalized record inputs without decompressing round payloads.
/// Every query projects only identity and aggregate columns; neither
/// <c>Rounds.PayloadJson</c> nor <c>QualifierRounds.PayloadJson</c> is selected.
/// All sporting values stay integer-only. Corrupt references abort.
/// </summary>
public static class RecordLoader
{
    internal sealed record RecordInputs(
        Dictionary<int, string> AthleteNames,
        List<RecordCalculator.SeasonStandingInput> SeasonStandings,
        List<RecordCalculator.StageStandingInput> StageStandings,
        List<RecordCalculator.MembershipInput> Memberships,
        List<RecordCalculator.CareerInput> Careers,
        List<RecordCalculator.PromotionInput> Promotions);

    internal static async Task<RecordInputs> LoadAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> seasonNumbers = await context.Seasons
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> leagueKinds = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Kind, cancellationToken)
            .ConfigureAwait(false);

        List<RecordCalculator.SeasonStandingInput> seasonStandings = await LoadSeasonStandingsAsync(
            context, leagueKinds, cancellationToken).ConfigureAwait(false);
        List<RecordCalculator.StageStandingInput> stageStandings = await LoadStageStandingsAsync(
            context, seasonNumbers, cancellationToken).ConfigureAwait(false);
        List<RecordCalculator.MembershipInput> memberships = await LoadMembershipsAsync(
            context, seasonNumbers, leagueKinds, cancellationToken).ConfigureAwait(false);
        List<RecordCalculator.CareerInput> careers = await LoadCareersAsync(
            context, cancellationToken).ConfigureAwait(false);
        List<RecordCalculator.PromotionInput> promotions = await LoadPromotionsAsync(
            context, names, cancellationToken).ConfigureAwait(false);
        return new RecordInputs(names, seasonStandings, stageStandings, memberships, careers, promotions);
    }

    internal static async Task<List<RecordCalculator.SeasonStandingInput>> LoadSeasonStandingsAsync(
        SaveDbContext context,
        Dictionary<int, int> leagueKinds,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingProbe> rows = await context.SeasonStandings
            .AsNoTracking()
            .Select(e => new SeasonStandingProbe(e.SeasonId, e.LeagueId, e.SaveAthleteId, e.IsChampion))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> seasonNumbers = await context.Seasons
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber, cancellationToken)
            .ConfigureAwait(false);
        List<RecordCalculator.SeasonStandingInput> inputs = new(rows.Count);
        foreach (SeasonStandingProbe row in rows)
        {
            if (!seasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Season standing references unknown season {row.SeasonId}.");
            }

            if (!leagueKinds.TryGetValue(row.LeagueId, out int kind))
            {
                throw new InvalidOperationException($"Season standing references unknown league {row.LeagueId}.");
            }

            inputs.Add(new RecordCalculator.SeasonStandingInput(
                row.SeasonId, seasonNumber, kind, row.SaveAthleteId, row.IsChampion));
        }

        return inputs;
    }

    internal static async Task<List<RecordCalculator.StageStandingInput>> LoadStageStandingsAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        CancellationToken cancellationToken)
    {
        List<StageStandingProbe> rows = await context.StageStandings
            .AsNoTracking()
            .Select(e => new StageStandingProbe(e.SeasonId, e.StageNumber, e.SaveAthleteId, e.StageRank))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<RecordCalculator.StageStandingInput> inputs = new(rows.Count);
        foreach (StageStandingProbe row in rows)
        {
            if (!seasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Stage standing references unknown season {row.SeasonId}.");
            }

            inputs.Add(new RecordCalculator.StageStandingInput(
                row.SeasonId, seasonNumber, row.StageNumber, row.SaveAthleteId, row.StageRank));
        }

        return inputs;
    }

    internal static async Task<List<RecordCalculator.MembershipInput>> LoadMembershipsAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        Dictionary<int, int> leagueKinds,
        CancellationToken cancellationToken)
    {
        List<MembershipProbe> rows = await context.SeasonMemberships
            .AsNoTracking()
            .Select(e => new MembershipProbe(e.SeasonId, e.LeagueId, e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<RecordCalculator.MembershipInput> inputs = new(rows.Count);
        foreach (MembershipProbe row in rows)
        {
            if (!seasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Membership references unknown season {row.SeasonId}.");
            }

            int? kind = null;
            bool isActive = row.LeagueId is not null;
            if (row.LeagueId is not null)
            {
                if (!leagueKinds.TryGetValue(row.LeagueId.Value, out int leagueKind))
                {
                    throw new InvalidOperationException($"Membership references unknown league {row.LeagueId.Value}.");
                }

                kind = leagueKind;
            }

            inputs.Add(new RecordCalculator.MembershipInput(
                row.SeasonId, seasonNumber, row.SaveAthleteId, kind, isActive));
        }

        return inputs;
    }

    internal static async Task<List<RecordCalculator.CareerInput>> LoadCareersAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<CareerProbe> rows = await context.AthleteCareers
            .AsNoTracking()
            .Select(e => new CareerProbe(e.SaveAthleteId, e.RoundWins, e.StageWins, e.CurrentEffectiveBonusThousandths))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<RecordCalculator.CareerInput> inputs = new(rows.Count);
        foreach (CareerProbe row in rows)
        {
            inputs.Add(new RecordCalculator.CareerInput(
                row.SaveAthleteId, row.RoundWins, row.StageWins, row.EffectiveBonusThousandths));
        }

        return inputs;
    }

    /// <summary>
    /// Counts promotions (inaugural + automatic + qualifier challenger winners)
    /// and relegations (automatic + failed qualifier incumbents) per athlete
    /// from normalized movement and qualifier tables only.
    /// </summary>
    internal static async Task<List<RecordCalculator.PromotionInput>> LoadPromotionsAsync(
        SaveDbContext context,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        List<MovementProbe> movements = await context.Movements
            .AsNoTracking()
            .Select(e => new MovementProbe(e.SaveAthleteId, e.Kind))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<QualifierProbe> qualifiers = await context.QualifierStandings
            .AsNoTracking()
            .Select(e => new QualifierProbe(e.SaveAthleteId, e.Role, e.IsQualified))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> promoCounts = [];
        Dictionary<int, int> relegCounts = [];
        foreach (int athleteId in names.Keys)
        {
            promoCounts[athleteId] = 0;
            relegCounts[athleteId] = 0;
        }

        foreach (MovementProbe movement in movements)
        {
            if (!promoCounts.ContainsKey(movement.SaveAthleteId))
            {
                throw new InvalidOperationException($"Movement references unknown athlete {movement.SaveAthleteId}.");
            }

            if (movement.Kind is (int)MovementKind.InauguralPromotion or (int)MovementKind.AutomaticPromotion)
            {
                promoCounts[movement.SaveAthleteId] = checked(promoCounts[movement.SaveAthleteId] + 1);
            }
            else if (movement.Kind == (int)MovementKind.AutomaticRelegation)
            {
                relegCounts[movement.SaveAthleteId] = checked(relegCounts[movement.SaveAthleteId] + 1);
            }
        }

        foreach (QualifierProbe qualifier in qualifiers)
        {
            if (!promoCounts.ContainsKey(qualifier.SaveAthleteId))
            {
                throw new InvalidOperationException($"Qualifier standing references unknown athlete {qualifier.SaveAthleteId}.");
            }

            if (qualifier.Role == (int)QualifierRole.Challenger && qualifier.IsQualified)
            {
                promoCounts[qualifier.SaveAthleteId] = checked(promoCounts[qualifier.SaveAthleteId] + 1);
            }
            else if (qualifier.Role == (int)QualifierRole.Incumbent && !qualifier.IsQualified)
            {
                relegCounts[qualifier.SaveAthleteId] = checked(relegCounts[qualifier.SaveAthleteId] + 1);
            }
        }

        return promoCounts.Select(kv => new RecordCalculator.PromotionInput(
            kv.Key, kv.Value, relegCounts[kv.Key])).ToList();
    }

    private sealed record SeasonStandingProbe(int SeasonId, int LeagueId, int SaveAthleteId, bool IsChampion);

    private sealed record StageStandingProbe(int SeasonId, int StageNumber, int SaveAthleteId, int StageRank);

    private sealed record MembershipProbe(int SeasonId, int? LeagueId, int SaveAthleteId);

    private sealed record CareerProbe(int SaveAthleteId, int RoundWins, int StageWins, int EffectiveBonusThousandths);

    private sealed record MovementProbe(int SaveAthleteId, int Kind);

    private sealed record QualifierProbe(int SaveAthleteId, int Role, bool IsQualified);
}
