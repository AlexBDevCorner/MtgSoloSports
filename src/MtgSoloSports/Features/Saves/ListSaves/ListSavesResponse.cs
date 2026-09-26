using MtgSoloSports.Features.Saves;

namespace MtgSoloSports.Features.Saves.ListSaves;

public sealed record ListSavesResponse(IReadOnlyList<SaveSummary> Saves);
