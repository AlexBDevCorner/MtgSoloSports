using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-100 correction: the Chaos field selection handler stays available via DI,
/// but its state-mutating HTTP POST route is deferred to MSS-106 lifecycle integration.
/// </summary>
public sealed class ChaosCupFieldRouteTests
{
    [Fact]
    public async Task ChaosField_MutatingPostRoute_IsUnavailableUntilLifecycleIntegration()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.PostAsync(
                $"/api/saves/{Guid.NewGuid():D}/cups/chaos/field", null);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ChaosField_Handler_RemainsRegisteredForDirectCalls()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using IServiceScope scope = factory.Services.CreateScope();
            SelectChaosCupFieldHandler? handler =
                scope.ServiceProvider.GetService<SelectChaosCupFieldHandler>();
            handler.ShouldNotBeNull();
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-chaos-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
            }));
        return (factory, root);
    }
}
