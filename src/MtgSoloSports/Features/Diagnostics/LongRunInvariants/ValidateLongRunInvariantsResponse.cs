namespace MtgSoloSports.Features.Diagnostics.LongRunInvariants;

public sealed record ValidateLongRunInvariantsResponse(
    Guid SaveId,
    bool Passed,
    IReadOnlyList<LongRunInvariantEntry> Invariants);
