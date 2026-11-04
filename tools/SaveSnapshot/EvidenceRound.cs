namespace MtgSoloSports.SaveSnapshot;

public sealed record EvidenceRound(
    int RoundNumber,
    int WinnerAthleteId,
    string WinnerName,
    string PayloadChecksum,
    string PayloadEncoding,
    IReadOnlyList<RoundPlacement> Placements,
    string EvidenceRef);
