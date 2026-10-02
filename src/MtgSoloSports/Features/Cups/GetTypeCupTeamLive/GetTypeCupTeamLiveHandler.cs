using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetTypeCupTeamResult;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;

namespace MtgSoloSports.Features.Cups.GetTypeCupTeamLive;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the authoritative
/// live Type Cup team projection for a source season without simulating: the
/// persisted selection (so every participating team is listed, even at zero)
/// plus every persisted round payload (completed groups plus the rounds already
/// played in the current group). Never resimulates; display-only formatting is
/// left to React. Read-only: never takes the per-save lock.
/// Without <paramref name="sourceSeasonNumber"/> returns the latest season with
/// a resolved team selection. Throws <see cref="TypeCupTeamResultNotFoundException"/>
/// (404) when no team selection exists yet.
/// </summary>
public sealed class GetTypeCupTeamLiveHandler
{
    private readonly SaveStore _store;

    public GetTypeCupTeamLiveHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<TypeCupTeamLiveResponse> HandleAsync(
        Guid saveId,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        return await BuildForSourceAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record LiveProjectionInputs(
        SimulationKernel.Rules.RulesV1 Rules,
        List<TypeCupSelectionEntity> Selection,
        int TeamCount,
        List<TypeCupTeamRoundEntity> Rounds,
        Dictionary<string, int> TeamIds);

    internal static async Task<SeasonEntity> LoadSourceAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (explicitSeason is null || !await HasSelectionAsync(context, explicitSeason.Id, cancellationToken).ConfigureAwait(false))
            {
                throw new TypeCupTeamResultNotFoundException(
                    $"Type Cup team event for Season {sourceSeasonNumber.Value} has not been resolved yet.");
            }

            return explicitSeason;
        }

        List<TypeCupSelectionEntity> any = await context.TypeCupSelections
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new TypeCupTeamResultNotFoundException("Type Cup team event has not been resolved yet.");
        }

        int latestSeasonId = any.Max(e => e.SourceSeasonId);
        SeasonEntity? latest = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latestSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new InvalidOperationException("Type Cup team selection references an unknown season.");
        }

        return latest;
    }

    internal static async Task<bool> HasSelectionAsync(SaveDbContext context, int sourceSeasonId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await context.TypeCupSelections
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == sourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the live projection for an already-resolved source season on any
    /// context (fresh reads or a just-committed advance transaction). Shared by
    /// the GET endpoint and the advance mutation so incremental refresh and
    /// reload agree exactly.
    /// </summary>
    internal static async Task<TypeCupTeamLiveResponse> BuildForSourceAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        LiveProjectionInputs inputs = await LoadProjectionInputsAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<TeamLiveStandings.LiveContribution> contributions = BuildContributions(inputs, source);
        List<(int TeamId, string TeamName)> roster = inputs.TeamIds
            .OrderBy(kvp => kvp.Value)
            .Select(kvp => (kvp.Value, kvp.Key))
            .ToList();
        IReadOnlyList<TeamLiveStandings.LiveTeamRow> live = TeamLiveStandings.Compute(roster, contributions);
        List<TypeCupTeamStandingEntity> official = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.TeamRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return AssembleResponse(saveId, source, inputs, live, official);
    }

    internal static async Task<LiveProjectionInputs> LoadProjectionInputsAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        List<TypeCupSelectionEntity> selection = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (selection.Count == 0)
        {
            throw new TypeCupTeamResultNotFoundException(
                $"Type Cup team event for Season {source.SeasonNumber} has not been resolved yet.");
        }

        int teamCount = TypeCupTeamInvariants.ValidateField(selection, rules);
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber)
            .ThenBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidateRoundSequence(rounds, rules, source);
        Dictionary<string, int> teamIds = RunTypeCupTeamHandler.BuildTeamIds(selection);
        return new LiveProjectionInputs(rules, selection, teamCount, rounds, teamIds);
    }

    internal static List<TeamLiveStandings.LiveContribution> BuildContributions(LiveProjectionInputs inputs, SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(source);
        Dictionary<int, TypeCupSelectionEntity> byAthlete = inputs.Selection.ToDictionary(e => e.SaveAthleteId);
        List<TeamLiveStandings.LiveContribution> contributions = new(inputs.Rounds.Count * inputs.TeamCount);
        foreach (TypeCupTeamRoundEntity round in inputs.Rounds)
        {
            AddRoundContributions(round, inputs, source, byAthlete, contributions);
        }

        return contributions;
    }

    internal static void AddRoundContributions(
        TypeCupTeamRoundEntity round,
        LiveProjectionInputs inputs,
        SeasonEntity source,
        Dictionary<int, TypeCupSelectionEntity> byAthlete,
        List<TeamLiveStandings.LiveContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(byAthlete);
        ArgumentNullException.ThrowIfNull(contributions);
        TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
        ValidateStoredRound(round, document, inputs.Rules, source);
        foreach (var placement in document.Placements)
        {
            if (!byAthlete.TryGetValue(placement.AthleteId, out TypeCupSelectionEntity? row))
            {
                throw new InvalidOperationException(
                    $"Type Cup team group {round.GroupNumber} round {round.RoundNumber} references an athlete outside the persisted selection.");
            }

            if (row.SelectionRank != round.GroupNumber)
            {
                throw new InvalidOperationException(
                    $"Type Cup team group {round.GroupNumber} round {round.RoundNumber} contains athlete {placement.AthleteId} from the wrong rank group.");
            }

            if (!inputs.TeamIds.TryGetValue(row.CreatureType, out int teamId))
            {
                throw new InvalidOperationException($"Type Cup team '{row.CreatureType}' has no deterministic team id.");
            }

            contributions.Add(new TeamLiveStandings.LiveContribution(
                teamId,
                row.CreatureType,
                placement.FinalThousandths,
                placement.BaseThousandths));
        }
    }

    internal static TypeCupTeamLiveResponse AssembleResponse(
        Guid saveId,
        SeasonEntity source,
        LiveProjectionInputs inputs,
        IReadOnlyList<TeamLiveStandings.LiveTeamRow> live,
        List<TypeCupTeamStandingEntity> official)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(official);
        bool isComplete = official.Count > 0;
        List<TypeCupTeamLiveMember> members = isComplete
            ? MapOfficial(live, official)
            : MapProvisional(live);
        string champion = isComplete ? official.Single(s => s.TeamRank == 1).CreatureType : string.Empty;
        string checksum = isComplete ? GetTypeCupTeamResultHandler.ComputeChecksum(official) : string.Empty;
        int totalRounds = checked(inputs.Rules.TypeCupMinTeamSize * inputs.Rules.TypeCupGroupRounds);
        (int currentGroup, int currentRound) = NextCursor(inputs.Rounds.Count, totalRounds, inputs.Rules, isComplete);
        (int lastGroup, int lastRound) = inputs.Rounds.Count == 0
            ? (0, 0)
            : (inputs.Rounds[^1].GroupNumber, inputs.Rounds[^1].RoundNumber);
        return new TypeCupTeamLiveResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            inputs.TeamCount,
            inputs.Rules.TypeCupMinTeamSize,
            inputs.Rules.TypeCupGroupRounds,
            inputs.Rounds.Count,
            totalRounds,
            currentGroup,
            currentRound,
            isComplete,
            !isComplete,
            champion,
            checksum,
            lastGroup,
            lastRound,
            members);
    }

    internal static List<TypeCupTeamLiveMember> MapProvisional(IReadOnlyList<TeamLiveStandings.LiveTeamRow> live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return live.Select(row => new TypeCupTeamLiveMember(
            row.TeamName,
            row.TeamName,
            row.ProvisionalRank,
            row.ScoreThousandths,
            row.BaseThousandths,
            nameof(TypeCupMedal.None))).ToList();
    }

    internal static List<TypeCupTeamLiveMember> MapOfficial(
        IReadOnlyList<TeamLiveStandings.LiveTeamRow> live,
        List<TypeCupTeamStandingEntity> official)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(official);
        Dictionary<string, TeamLiveStandings.LiveTeamRow> byTeam = live.ToDictionary(
            row => row.TeamName, StringComparer.Ordinal);
        List<TypeCupTeamLiveMember> members = new(official.Count);
        foreach (TypeCupTeamStandingEntity team in official.OrderBy(t => t.TeamRank))
        {
            if (!byTeam.TryGetValue(team.CreatureType, out TeamLiveStandings.LiveTeamRow? row))
            {
                throw new InvalidOperationException($"Type Cup team '{team.CreatureType}' has official standings but no live projection.");
            }

            // The live sidebar must agree with the official completed-event
            // totals exactly; any drift aborts instead of displaying fiction.
            if (row.ScoreThousandths != team.TeamScoreThousandths || row.BaseThousandths != team.TeamBaseThousandths)
            {
                throw new InvalidOperationException(
                    $"Type Cup team '{team.CreatureType}' live total does not match its official completed-event score.");
            }

            members.Add(new TypeCupTeamLiveMember(
                team.CreatureType,
                team.CreatureType,
                team.TeamRank,
                team.TeamScoreThousandths,
                team.TeamBaseThousandths,
                ((TypeCupMedal)team.Medal).ToString()));
        }

        if (members.Count != live.Count)
        {
            throw new InvalidOperationException("Type Cup official team count does not match the live projection field.");
        }

        return members;
    }

    internal static void ValidateRoundSequence(
        List<TypeCupTeamRoundEntity> rounds,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(source);
        int totalRounds = checked(rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds);
        if (rounds.Count > totalRounds)
        {
            throw new InvalidOperationException(
                $"Type Cup team event for Season {source.SeasonNumber} holds {rounds.Count} persisted rounds, more than the {totalRounds} scheduled.");
        }

        for (int index = 0; index < rounds.Count; index++)
        {
            CheckRoundPosition(rounds[index], index, rules, source);
        }
    }

    internal static void CheckRoundPosition(
        TypeCupTeamRoundEntity round,
        int index,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(source);
        int expectedGroup = (index / rules.TypeCupGroupRounds) + 1;
        int expectedRound = (index % rules.TypeCupGroupRounds) + 1;
        if (round.GroupNumber != expectedGroup || round.RoundNumber != expectedRound)
        {
            throw new InvalidOperationException(
                $"Type Cup team event for Season {source.SeasonNumber} has a gap or misorder at persisted position {index + 1}: found group {round.GroupNumber} round {round.RoundNumber}, expected group {expectedGroup} round {expectedRound}.");
        }

        if (round.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException(
                $"Type Cup team group {round.GroupNumber} round {round.RoundNumber} was persisted under rules v{round.RulesVersion} but the save now runs v{rules.Version}; refusing to mix rule versions.");
        }
    }

    internal static void ValidateStoredRound(
        TypeCupTeamRoundEntity round,
        TypeCupTeamRoundPayloadDocument document,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(source);
        if (document.GroupNumber != round.GroupNumber || document.RoundNumber != round.RoundNumber)
        {
            throw new InvalidOperationException(
                $"Type Cup team group {round.GroupNumber} round {round.RoundNumber} payload identity is corrupt.");
        }

        if (document.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException(
                $"Type Cup team group {round.GroupNumber} round {round.RoundNumber} belongs to season {document.SourceSeasonNumber}, expected {source.SeasonNumber}.");
        }

        if (!string.Equals(document.Checksum, round.PayloadChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Type Cup team group {round.GroupNumber} round {round.RoundNumber} checksum does not match its payload.");
        }

        if (document.Placements.Count == 0)
        {
            throw new InvalidOperationException(
                $"Type Cup team group {round.GroupNumber} round {round.RoundNumber} has no placements.");
        }
    }

    internal static (int Group, int Round) NextCursor(
        int completedRounds,
        int totalRounds,
        SimulationKernel.Rules.RulesV1 rules,
        bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (isComplete || completedRounds >= totalRounds)
        {
            return (rules.TypeCupMinTeamSize, rules.TypeCupGroupRounds);
        }

        return ((completedRounds / rules.TypeCupGroupRounds) + 1, (completedRounds % rules.TypeCupGroupRounds) + 1);
    }
}
