namespace MtgSoloSports.Features.Diagnostics.LongRunInvariants;

public sealed record LongRunInvariantEntry(string Name, bool Passed, string Detail);
