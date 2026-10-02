using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetColorCupTeamResult;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;

namespace MtgSoloSports.Features.Cups.GetColorCupTeamLive;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the authoritative
/// live Color Cup team projection for a source season without simulating: the
/// persisted selection (so every color team is listed, even at zero) plus
/// every persisted round payload (completed groups plus the rounds already
/// played in the current group). Never resimulates; display-only formatting is
/// left to React. Read-only: never takes the per-save lock.
/// Without <paramref name="sourceSeasonNumber"/> returns the latest season with
/// a resolved team selection. Throws <see cref="ColorCupTeamResultNotFoundException"/>
/// (404) when no team selection exists yet.
/// </summary>
public sealed class GetColorCupTeamLiveHandler
{
    private readonly SaveStore _store;

    public GetColorCupTeamLiveHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ColorCupTeamLiveResponse> HandleAsync(
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
        List<ColorCupSelectionEntity> Selection,
        List<ColorCupTeamRoundEntity> Rounds);

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
                throw new ColorCupTeamResultNotFoundException(
                    $"Color Cup team event for Season {sourceSeasonNumber.Value} has not been resolved yet.");
            }

            return explicitSeason;
        }

        List<ColorCupSelectionEntity> any = await context.ColorCupSelections
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new ColorCupTeamResultNotFoundException("Color Cup team event has not been resolved yet.");
        }

        int latestSeasonId = any.Max(e => e.SourceSeasonId);
        SeasonEntity? latest = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latestSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new InvalidOperationException("Color Cup team selection references an unknown season.");
        }

        return latest;
    }

    internal static async Task<bool> HasSelectionAsync(SaveDbContext context, int sourceSeasonId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await context.ColorCupSelections
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
    internal static async Task<ColorCupTeamLiveResponse> BuildForSourceAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        LiveProjectionInputs inputs = await LoadProjectionInputsAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<TeamLiveStandings.LiveContribution> contributions = BuildContributions(inputs, source);
        List<(int TeamId, string TeamName)> roster = inputs.Selection
            .Select(e => e.SportingColor)
            .Distinct()
            .OrderBy(color => color)
            .Select(color => (color, ((SportingColor)color).ToString()))
            .ToList();
        IReadOnlyList<TeamLiveStandings.LiveTeamRow> live = TeamLiveStandings.Compute(roster, contributions);
        List<ColorCupTeamStandingEntity> official = await context.ColorCupTeamStandings
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
        List<ColorCupSelectionEntity> selection = await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (selection.Count == 0)
        {
            throw new ColorCupTeamResultNotFoundException(
                $"Color Cup team event for Season {source.SeasonNumber} has not been resolved yet.");
        }

        ColorCupTeamInvariants.ValidateField(selection, rules);
        List<ColorCupTeamRoundEntity> rounds = await context.ColorCupTeamRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber)
            .ThenBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidateRoundSequence(rounds, rules, source);
        return new LiveProjectionInputs(rules, selection, rounds);
    }

    internal static List<TeamLiveStandings.LiveContribution> BuildContributions(LiveProjectionInputs inputs, SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(source);
        Dictionary<int, ColorCupSelectionEntity> byAthlete = inputs.Selection.ToDictionary(e => e.SaveAthleteId);
        List<TeamLiveStandings.LiveContribution> contributions = new(inputs.Rounds.Count * inputs.Rules.ColorCupColorCount);
        foreach (ColorCupTeamRoundEntity round in inputs.Rounds)
        {
            AddRoundContributions(round, inputs, source, byAthlete, contributions);
        }

        return contributions;
    }

    internal static void AddRoundContributions(
        ColorCupTeamRoundEntity round,
        LiveProjectionInputs inputs,
        SeasonEntity source,
        Dictionary<int, ColorCupSelectionEntity> byAthlete,
        List<TeamLiveStandings.LiveContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(byAthlete);
        ArgumentNullException.ThrowIfNull(contributions);
        ColorCupTeamRoundPayloadDocument document = ColorCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
        ValidateStoredRound(round, document, inputs.Rules, source);
        foreach (var placement in document.Placements)
        {
            if (!byAthlete.TryGetValue(placement.AthleteId, out ColorCupSelectionEntity? row))
            {
                throw new InvalidOperationException(
                    $"Color Cup team group {round.GroupNumber} round {round.RoundNumber} references an athlete outside the persisted selection.");
            }

            if (row.SelectionRank != round.GroupNumber)
            {
                throw new InvalidOperationException(
                    $"Color Cup team group {round.GroupNumber} round {round.RoundNumber} contains athlete {placement.AthleteId} from the wrong rank group.");
            }

            string teamName = ((SportingColor)row.SportingColor).ToString();
            contributions.Add(new TeamLiveStandings.LiveContribution(
                row.SportingColor,
                teamName,
                placement.FinalThousandths,
                placement.BaseThousandths));
        }
    }

    internal static ColorCupTeamLiveResponse AssembleResponse(
        Guid saveId,
        SeasonEntity source,
        LiveProjectionInputs inputs,
        IReadOnlyList<TeamLiveStandings.LiveTeamRow> live,
        List<ColorCupTeamStandingEntity> official)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(official);
        bool isComplete = official.Count > 0;
        List<ColorCupTeamLiveMember> members = isComplete
            ? MapOfficial(live, official)
            : MapProvisional(live);
        ColorCupTeamStandingEntity? champion = isComplete ? official.Single(s => s.TeamRank == 1) : null;
        string checksum = isComplete ? GetColorCupTeamResultHandler.ComputeChecksum(official) : string.Empty;
        int totalRounds = checked(inputs.Rules.ColorCupTeamSize * inputs.Rules.ColorCupTeamGroupRounds);
        (int currentGroup, int currentRound) = NextCursor(inputs.Rounds.Count, totalRounds, inputs.Rules, isComplete);
        (int lastGroup, int lastRound) = inputs.Rounds.Count == 0
            ? (0, 0)
            : (inputs.Rounds[^1].GroupNumber, inputs.Rounds[^1].RoundNumber);
        return new ColorCupTeamLiveResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            inputs.Rules.ColorCupColorCount,
            inputs.Rules.ColorCupTeamSize,
            inputs.Rules.ColorCupTeamGroupRounds,
            inputs.Rounds.Count,
            totalRounds,
            currentGroup,
            currentRound,
            isComplete,
            !isComplete,
            champion?.SportingColor ?? -1,
            champion is null ? string.Empty : ((SportingColor)champion.SportingColor).ToString(),
            checksum,
            lastGroup,
            lastRound,
            members);
    }

    internal static List<ColorCupTeamLiveMember> MapProvisional(IReadOnlyList<TeamLiveStandings.LiveTeamRow> live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return live.Select(row => new ColorCupTeamLiveMember(
            row.TeamId,
            row.TeamName,
            row.ProvisionalRank,
            row.ScoreThousandths,
            row.BaseThousandths,
            nameof(ColorCupMedal.None))).ToList();
    }

    internal static List<ColorCupTeamLiveMember> MapOfficial(
        IReadOnlyList<TeamLiveStandings.LiveTeamRow> live,
        List<ColorCupTeamStandingEntity> official)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(official);
        Dictionary<int, TeamLiveStandings.LiveTeamRow> byTeam = live.ToDictionary(row => row.TeamId);
        List<ColorCupTeamLiveMember> members = new(official.Count);
        foreach (ColorCupTeamStandingEntity team in official.OrderBy(t => t.TeamRank))
        {
            if (!byTeam.TryGetValue(team.SportingColor, out TeamLiveStandings.LiveTeamRow? row))
            {
                throw new InvalidOperationException($"Color Cup team id {team.SportingColor} has official standings but no live projection.");
            }

            // The live sidebar must agree with the official completed-event
            // totals exactly; any drift aborts instead of displaying fiction.
            if (row.ScoreThousandths != team.TeamScoreThousandths || row.BaseThousandths != team.TeamBaseThousandths)
            {
                throw new InvalidOperationException(
                    $"Color Cup team '{row.TeamName}' live total does not match its official completed-event score.");
            }

            members.Add(new ColorCupTeamLiveMember(
                team.SportingColor,
                row.TeamName,
                team.TeamRank,
                team.TeamScoreThousandths,
                team.TeamBaseThousandths,
                ((ColorCupMedal)team.Medal).ToString()));
        }

        if (members.Count != live.Count)
        {
            throw new InvalidOperationException("Color Cup official team count does not match the live projection field.");
        }

        return members;
    }

    internal static void ValidateRoundSequence(
        List<ColorCupTeamRoundEntity> rounds,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(source);
        int totalRounds = checked(rules.ColorCupTeamSize * rules.ColorCupTeamGroupRounds);
        if (rounds.Count > totalRounds)
        {
            throw new InvalidOperationException(
                $"Color Cup team event for Season {source.SeasonNumber} holds {rounds.Count} persisted rounds, more than the {totalRounds} scheduled.");
        }

        for (int index = 0; index < rounds.Count; index++)
        {
            CheckRoundPosition(rounds[index], index, rules, source);
        }
    }

    internal static void CheckRoundPosition(
        ColorCupTeamRoundEntity round,
        int index,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(source);
        int expectedGroup = (index / rules.ColorCupTeamGroupRounds) + 1;
        int expectedRound = (index % rules.ColorCupTeamGroupRounds) + 1;
        if (round.GroupNumber != expectedGroup || round.RoundNumber != expectedRound)
        {
            throw new InvalidOperationException(
                $"Color Cup team event for Season {source.SeasonNumber} has a gap or misorder at persisted position {index + 1}: found group {round.GroupNumber} round {round.RoundNumber}, expected group {expectedGroup} round {expectedRound}.");
        }

        if (round.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException(
                $"Color Cup team group {round.GroupNumber} round {round.RoundNumber} was persisted under rules v{round.RulesVersion} but the save now runs v{rules.Version}; refusing to mix rule versions.");
        }
    }

    internal static void ValidateStoredRound(
        ColorCupTeamRoundEntity round,
        ColorCupTeamRoundPayloadDocument document,
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
                $"Color Cup team group {round.GroupNumber} round {round.RoundNumber} payload identity is corrupt.");
        }

        if (document.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException(
                $"Color Cup team group {round.GroupNumber} round {round.RoundNumber} belongs to season {document.SourceSeasonNumber}, expected {source.SeasonNumber}.");
        }

        if (!string.Equals(document.Checksum, round.PayloadChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Color Cup team group {round.GroupNumber} round {round.RoundNumber} checksum does not match its payload.");
        }

        if (document.Placements.Count == 0)
        {
            throw new InvalidOperationException(
                $"Color Cup team group {round.GroupNumber} round {round.RoundNumber} has no placements.");
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
            return (rules.ColorCupTeamSize, rules.ColorCupTeamGroupRounds);
        }

        return ((completedRounds / rules.ColorCupTeamGroupRounds) + 1, (completedRounds % rules.ColorCupTeamGroupRounds) + 1);
    }
}
