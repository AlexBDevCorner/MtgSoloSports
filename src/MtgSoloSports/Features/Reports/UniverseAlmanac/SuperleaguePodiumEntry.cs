namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record SuperleaguePodiumEntry(
    int SeasonNumber,
    bool IsComplete,
    AlmanacPodium? Champion,
    AlmanacPodium? RunnerUp,
    AlmanacPodium? Third);
