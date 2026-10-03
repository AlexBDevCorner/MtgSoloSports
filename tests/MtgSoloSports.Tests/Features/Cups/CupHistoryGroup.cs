using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>xUnit collection sharing one <see cref="CupHistoryFixture"/> across the Cup history read tests.</summary>
[CollectionDefinition(Name)]
public sealed class CupHistoryGroup : ICollectionFixture<CupHistoryFixture>
{
    public const string Name = "Cup history";
}
