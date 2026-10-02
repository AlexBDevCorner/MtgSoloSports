namespace MtgSoloSports.Features.Cups.GetTypeCupTeamLive;

/// <summary>
/// One provisional (or, once complete, official) Type Cup live team row.
/// Built only from persisted selection plus persisted round payloads (and, once
/// the event is complete, the official persisted team standings) so refresh and
/// reload agree exactly and replay never resimulates. Display-only formatting
/// happens in React; no sporting math lives in the UI.
/// </summary>
public sealed record TypeCupTeamLiveMember(
    string CreatureType,
    string TeamName,
    int TeamRank,
    int TeamScoreThousandths,
    int TeamBaseThousandths,
    string Medal);
