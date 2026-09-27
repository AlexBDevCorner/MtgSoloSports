using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Diagnostics.LongRunInvariants;

/// <summary>
/// Structural long-run invariant runner. Validates the six MSS-031 property
/// areas over normalized tables without decompressing round payloads:
/// league sizes, duplicate active athletes, bonus timing, qualifier counts,
/// nationality immutability and Cup rotation.
/// </summary>
public static class LongRunInvariantChecks
{
    internal sealed record InvariantResult(string Name, bool Passed, string Detail);

    internal sealed record Snapshot(
        IReadOnlyList<SeasonEntity> Seasons,
        IReadOnlyList<LeagueEntity> Leagues,
        IReadOnlyList<SeasonMembershipEntity> Memberships,
        IReadOnlyList<StageStandingEntity> StageRows,
        IReadOnlyList<SeasonStandingEntity> SeasonRows,
        IReadOnlyList<QualifierStandingEntity> Qualifiers,
        IReadOnlyList<SaveAthleteEntity> Athletes,
        IReadOnlyList<TypeCupSelectionEntity> TypeSelections,
        IReadOnlyList<ColorCupSelectionEntity> ColorSelections);

    internal static async Task<Snapshot> LoadSnapshotAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<SeasonEntity> seasons = await context.Seasons.AsNoTracking().OrderBy(e => e.SeasonNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<LeagueEntity> leagues = await context.Leagues.AsNoTracking().OrderBy(e => e.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<StageStandingEntity> stageRows = await context.StageStandings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<SeasonStandingEntity> seasonRows = await context.SeasonStandings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<QualifierStandingEntity> qualifiers = await context.QualifierStandings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<SaveAthleteEntity> athletes = await context.SaveAthletes.AsNoTracking()
            .Select(e => new SaveAthleteEntity { Id = e.Id, Name = e.Name, SportingColor = e.SportingColor, TypeCupNationality = e.TypeCupNationality })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<TypeCupSelectionEntity> typeSelections = await context.TypeCupSelections.AsNoTracking()
            .Select(e => new TypeCupSelectionEntity
            {
                Id = e.Id,
                SourceSeasonId = e.SourceSeasonId,
                SourceSeasonNumber = e.SourceSeasonNumber,
                CreatureType = e.CreatureType,
                SaveAthleteId = e.SaveAthleteId,
                SelectionRank = e.SelectionRank,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ColorCupSelectionEntity> colorSelections = await context.ColorCupSelections.AsNoTracking()
            .Select(e => new ColorCupSelectionEntity
            {
                Id = e.Id,
                SourceSeasonId = e.SourceSeasonId,
                SourceSeasonNumber = e.SourceSeasonNumber,
                SportingColor = e.SportingColor,
                SaveAthleteId = e.SaveAthleteId,
                SelectionRank = e.SelectionRank,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new Snapshot(seasons, leagues, memberships, stageRows, seasonRows, qualifiers, athletes, typeSelections, colorSelections);
    }

    internal static IReadOnlyList<InvariantResult> ValidateAll(Snapshot snapshot, SimulationKernel.Rules.RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rules);
        return
        [
            CheckLeagueSizes(snapshot, rules),
            CheckNoDuplicateActiveAthletes(snapshot, rules),
            CheckBonusTiming(snapshot, rules),
            CheckQualifierCounts(snapshot, rules),
            CheckNationalityImmutability(snapshot),
            CheckCupRotation(snapshot),
        ];
    }

    internal static void ThrowIfFailed(IReadOnlyList<InvariantResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        List<InvariantResult> failed = results.Where(r => !r.Passed).ToList();
        if (failed.Count != 0)
        {
            throw new InvalidOperationException(
                "Long-run invariants failed: " + string.Join("; ", failed.Select(f => $"{f.Name}: {f.Detail}")));
        }
    }

    internal static InvariantResult CheckLeagueSizes(Snapshot snapshot, SimulationKernel.Rules.RulesV1 rules)
    {
        foreach (SeasonEntity season in snapshot.Seasons)
        {
            List<LeagueEntity> leagues = snapshot.Leagues.Where(l => l.SeasonId == season.Id).ToList();
            int expectedLeagues = season.SeasonNumber == 1 ? rules.RegularLeagueCount : rules.RegularLeagueCount + 1;
            if (leagues.Count != expectedLeagues)
            {
                return new InvariantResult("league_sizes", false,
                    $"Season {season.SeasonNumber} has {leagues.Count} leagues, expected {expectedLeagues}.");
            }

            foreach (LeagueEntity league in leagues)
            {
                int members = snapshot.Memberships.Count(m => m.SeasonId == season.Id && m.LeagueId == league.Id);
                if (members != rules.LeagueSize)
                {
                    return new InvariantResult("league_sizes", false,
                        $"Season {season.SeasonNumber} league '{league.Name}' has {members} members, expected {rules.LeagueSize}.");
                }
            }
        }

        return new InvariantResult("league_sizes", true, $"Checked {snapshot.Seasons.Count} seasons.");
    }

    internal static InvariantResult CheckNoDuplicateActiveAthletes(Snapshot snapshot, SimulationKernel.Rules.RulesV1 rules)
    {
        foreach (SeasonEntity season in snapshot.Seasons)
        {
            List<int> active = snapshot.Memberships
                .Where(m => m.SeasonId == season.Id && m.LeagueId.HasValue)
                .Select(m => m.SaveAthleteId)
                .ToList();
            int distinct = active.Distinct().Count();
            if (distinct != active.Count)
            {
                return new InvariantResult("no_duplicates", false,
                    $"Season {season.SeasonNumber} has duplicate active athletes ({active.Count - distinct} duplicates).");
            }

            int expectedActive = season.SeasonNumber == 1
                ? rules.RegularLeagueCount * rules.LeagueSize
                : (rules.RegularLeagueCount + 1) * rules.LeagueSize;
            if (active.Count != expectedActive)
            {
                return new InvariantResult("no_duplicates", false,
                    $"Season {season.SeasonNumber} has {active.Count} active athletes, expected {expectedActive}.");
            }

            HashSet<int> activeIds = active.ToHashSet();
            foreach (StageStandingEntity row in snapshot.StageRows.Where(r => r.SeasonId == season.Id))
            {
                if (!activeIds.Contains(row.SaveAthleteId))
                {
                    return new InvariantResult("no_duplicates", false,
                        $"Season {season.SeasonNumber} stage {row.StageNumber} references pool athlete {row.SaveAthleteId}.");
                }
            }
        }

        return new InvariantResult("no_duplicates", true, $"Checked {snapshot.Seasons.Count} seasons.");
    }

    internal static InvariantResult CheckBonusTiming(Snapshot snapshot, SimulationKernel.Rules.RulesV1 rules)
    {
        int maxRound = rules.RoundBonusThousandths.Max() * rules.RoundsPerStage * rules.SuperleagueBonusMultiplier;
        int maxStage = rules.StageBonusThousandths.Max() * rules.SuperleagueBonusMultiplier;
        int maxEarned = checked(maxRound + maxStage);
        foreach (StageStandingEntity row in snapshot.StageRows)
        {
            if (row.EarnedBonusThousandths < 0 || row.EarnedBonusThousandths > maxEarned)
            {
                return new InvariantResult("bonus_timing", false,
                    $"Stage standing {row.Id} earned bonus {row.EarnedBonusThousandths} out of range 0..{maxEarned}.");
            }

            if (row.StageNumber < 1 || row.StageNumber > rules.StagesPerSeason)
            {
                return new InvariantResult("bonus_timing", false,
                    $"Stage standing {row.Id} has corrupt stage {row.StageNumber}.");
            }
        }

        return new InvariantResult("bonus_timing", true, $"Checked {snapshot.StageRows.Count} stage standings (max earned {maxEarned}).");
    }

    internal static InvariantResult CheckQualifierCounts(Snapshot snapshot, SimulationKernel.Rules.RulesV1 rules)
    {
        var groups = snapshot.Qualifiers.GroupBy(q => (q.FromSeasonId, q.ToSeasonId)).ToList();
        foreach (var group in groups)
        {
            List<QualifierStandingEntity> rows = group.ToList();
            if (rows.Count != rules.QualifierSize)
            {
                return new InvariantResult("qualifier_counts", false,
                    $"Qualifier {group.Key.FromSeasonId}->{group.Key.ToSeasonId} has {rows.Count} athletes, expected {rules.QualifierSize}.");
            }

            int qualified = rows.Count(r => r.IsQualified);
            if (qualified != rules.QualifierWinners)
            {
                return new InvariantResult("qualifier_counts", false,
                    $"Qualifier {group.Key.FromSeasonId}->{group.Key.ToSeasonId} has {qualified} winners, expected {rules.QualifierWinners}.");
            }

            int incumbents = rows.Count(r => r.Role == (int)QualifierRole.Incumbent);
            int challengers = rows.Count(r => r.Role == (int)QualifierRole.Challenger);
            if (incumbents != rules.SuperleagueQualifierIncumbentCount || challengers != rules.FeederQualifierCount)
            {
                return new InvariantResult("qualifier_counts", false,
                    $"Qualifier {group.Key.FromSeasonId}->{group.Key.ToSeasonId} roles {incumbents}/{challengers}, expected {rules.SuperleagueQualifierIncumbentCount}/{rules.FeederQualifierCount}.");
            }

            if (!rows.Select(r => r.QualifierRank).ToHashSet().SetEquals(Enumerable.Range(1, rules.QualifierSize)))
            {
                return new InvariantResult("qualifier_counts", false,
                    $"Qualifier {group.Key.FromSeasonId}->{group.Key.ToSeasonId} has corrupt ranks.");
            }
        }

        return new InvariantResult("qualifier_counts", true, $"Checked {groups.Count} qualifier transitions.");
    }

    internal static InvariantResult CheckNationalityImmutability(Snapshot snapshot)
    {
        Dictionary<int, string?> nationalityByAthlete = snapshot.Athletes
            .ToDictionary(a => a.Id, a => string.IsNullOrWhiteSpace(a.TypeCupNationality) ? null : a.TypeCupNationality.Trim());
        Dictionary<int, HashSet<string>> typesByAthlete = new();
        foreach (TypeCupSelectionEntity selection in snapshot.TypeSelections)
        {
            if (!typesByAthlete.TryGetValue(selection.SaveAthleteId, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                typesByAthlete[selection.SaveAthleteId] = set;
            }

            set.Add(selection.CreatureType);
        }

        foreach ((int athleteId, HashSet<string> types) in typesByAthlete)
        {
            if (types.Count > 1)
            {
                return new InvariantResult("nationality_immutability", false,
                    $"Athlete {athleteId} was selected for {types.Count} types.");
            }

            if (nationalityByAthlete.TryGetValue(athleteId, out string? nationality) && nationality is not null)
            {
                string selected = types.Single();
                if (!string.Equals(selected, nationality, StringComparison.Ordinal))
                {
                    return new InvariantResult("nationality_immutability", false,
                        $"Athlete {athleteId} nationality '{nationality}' mismatches selection '{selected}'.");
                }
            }
        }

        return new InvariantResult("nationality_immutability", true,
            $"Checked {snapshot.Athletes.Count} athletes, {snapshot.TypeSelections.Count} type selections.");
    }

    internal static InvariantResult CheckCupRotation(Snapshot snapshot)
    {
        List<int> colorSeasons = snapshot.ColorSelections.Select(e => e.SourceSeasonNumber).Distinct().OrderBy(e => e).ToList();
        List<int> typeSeasons = snapshot.TypeSelections.Select(e => e.SourceSeasonNumber).Distinct().OrderBy(e => e).ToList();
        foreach (int season in colorSeasons)
        {
            if (season % 2 == 0)
            {
                return new InvariantResult("cup_rotation", false,
                    $"Color Cup resolved for even season {season}; odd seasons only.");
            }

            if (typeSeasons.Contains(season))
            {
                return new InvariantResult("cup_rotation", false,
                    $"Season {season} has both Color and Type Cup selections.");
            }
        }

        foreach (int season in typeSeasons)
        {
            if (season % 2 != 0)
            {
                return new InvariantResult("cup_rotation", false,
                    $"Type Cup resolved for odd season {season}; even seasons only.");
            }
        }

        return new InvariantResult("cup_rotation", true,
            $"Checked {colorSeasons.Count} Color Cups, {typeSeasons.Count} Type Cups.");
    }
}
