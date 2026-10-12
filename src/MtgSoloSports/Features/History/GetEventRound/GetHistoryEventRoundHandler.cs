using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.History.GetEventRound;

/// <summary>
/// Endpoint -&gt; Handler direct call. Returns one stored postseason event round
/// in the league replay presentation shape. Grouped (team) events require a
/// group; single-stage events reject one. Type Cup tournaments additionally
/// carry stage identity (tournamentPhase plus qualificationGroup): callers
/// that name a squad rank group/round shared by several stages must also name
/// the stage. Read-only; never resimulates; never picks a wrong stage via
/// FirstOrDefault.
/// </summary>
public sealed class GetHistoryEventRoundHandler
{
    private readonly SaveStore _store;

    public GetHistoryEventRoundHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<EventRoundView> HandleAsync(
        Guid saveId,
        int seasonNumber,
        string eventKey,
        int round,
        int? group,
        CancellationToken cancellationToken = default,
        int? tournamentPhase = null,
        int? qualificationGroup = null)
    {
        HistoryEventRows.ValidateRequest(saveId, seasonNumber, eventKey);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        PostseasonEvents.EventShape shape = string.Equals(eventKey, PostseasonEvents.Qualifier, StringComparison.Ordinal)
            ? new PostseasonEvents.EventShape(1, rules.QualifierRounds)
            : PostseasonEvents.Shape(eventKey, rules);
        if (shape.IsGrouped && (group is null || group < 1 || group > shape.GroupCount))
        {
            throw new ArgumentException($"{PostseasonEvents.Title(eventKey)} rounds need a group between 1 and {shape.GroupCount}.", nameof(group));
        }

        if (!shape.IsGrouped && group is not null)
        {
            throw new ArgumentException($"{PostseasonEvents.Title(eventKey)} has no groups.", nameof(group));
        }

        if (round < 1 || round > shape.RoundsPerGroup)
        {
            throw new ArgumentException($"Round must be between 1 and {shape.RoundsPerGroup}.", nameof(round));
        }

        bool isTypeCup = string.Equals(eventKey, PostseasonEvents.TypeCupTeam, StringComparison.Ordinal);
        if (!isTypeCup && (tournamentPhase is not null || qualificationGroup is not null))
        {
            throw new ArgumentException($"{PostseasonEvents.Title(eventKey)} has no tournament stages.", nameof(tournamentPhase));
        }

        if (isTypeCup)
        {
            ValidateTypeCupStage(tournamentPhase, qualificationGroup);
        }

        SeasonEntity season = await HistoryEventRows.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        List<HistoryEventRows.StoredRound> rows = await HistoryEventRows.LoadAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false);
        HistoryEventRows.StoredRound row = ResolveRow(eventKey, rows, group, round, tournamentPhase, qualificationGroup);
        return await EventRoundViews.BuildAsync(
            context,
            seasonNumber,
            eventKey,
            row.Group,
            row.Round,
            row.RulesVersion,
            row.Checksum,
            row.Before,
            row.After,
            HistoryEventRows.DecodePlacements(eventKey, row.PayloadJson),
            cancellationToken,
            row.TournamentPhase,
            row.QualificationGroup).ConfigureAwait(false);
    }

    internal static void ValidateTypeCupStage(int? tournamentPhase, int? qualificationGroup)
    {
        if (tournamentPhase is null && qualificationGroup is null)
        {
            return;
        }

        if (tournamentPhase is null || qualificationGroup is null)
        {
            throw new ArgumentException("A Type Cup stage needs both a tournament phase and a qualification group.", nameof(qualificationGroup));
        }

        if (tournamentPhase != 0 && tournamentPhase != 1 && tournamentPhase != 2)
        {
            throw new ArgumentException($"Tournament phase {tournamentPhase} is outside the Type Cup.", nameof(tournamentPhase));
        }

        if (qualificationGroup < 0)
        {
            throw new ArgumentException($"Qualification group {qualificationGroup} is outside the Type Cup.", nameof(qualificationGroup));
        }

        if ((tournamentPhase == 0 || tournamentPhase == 2) && qualificationGroup != 0)
        {
            throw new ArgumentException("Legacy and Final stages use qualification group 0.", nameof(qualificationGroup));
        }

        if (tournamentPhase == 1 && qualificationGroup < 1)
        {
            throw new ArgumentException("Qualification stages use qualification group 1 or greater.", nameof(qualificationGroup));
        }
    }

    internal static HistoryEventRows.StoredRound ResolveRow(
        string eventKey,
        List<HistoryEventRows.StoredRound> rows,
        int? group,
        int round,
        int? tournamentPhase,
        int? qualificationGroup)
    {
        ArgumentNullException.ThrowIfNull(rows);
        bool isTypeCup = string.Equals(eventKey, PostseasonEvents.TypeCupTeam, StringComparison.Ordinal);
        if (!isTypeCup)
        {
            List<HistoryEventRows.StoredRound> matches = rows
                .Where(r => r.Group == group && r.Round == round)
                .ToList();
            if (matches.Count == 0)
            {
                throw new HistoryNotFoundException($"{PostseasonEvents.Title(eventKey)} round {round} has not been played.");
            }

            if (matches.Count > 1)
            {
                throw new InvalidOperationException($"{PostseasonEvents.Title(eventKey)} has corrupt duplicate rounds.");
            }

            return matches[0];
        }

        if (tournamentPhase is not null && qualificationGroup is not null)
        {
            List<HistoryEventRows.StoredRound> staged = rows
                .Where(r => r.Group == group && r.Round == round
                    && r.TournamentPhase == tournamentPhase && r.QualificationGroup == qualificationGroup)
                .ToList();
            if (staged.Count == 0)
            {
                throw new HistoryNotFoundException(
                    $"{PostseasonEvents.Title(eventKey)} stage {tournamentPhase}/{qualificationGroup} group {group} round {round} has not been played.");
            }

            if (staged.Count > 1)
            {
                throw new InvalidOperationException($"{PostseasonEvents.Title(eventKey)} has corrupt duplicate rounds.");
            }

            return staged[0];
        }

        List<HistoryEventRows.StoredRound> legacy = rows
            .Where(r => r.Group == group && r.Round == round)
            .ToList();
        if (legacy.Count == 0)
        {
            throw new HistoryNotFoundException($"{PostseasonEvents.Title(eventKey)} round {round} has not been played.");
        }

        if (legacy.Count > 1)
        {
            throw new HistoryNotFoundException(
                $"{PostseasonEvents.Title(eventKey)} group {group} round {round} exists in {legacy.Count} tournament stages; specify tournamentPhase and qualificationGroup.");
        }

        return legacy[0];
    }
}
