using Microsoft.AspNetCore.Http;
using MtgSoloSports.Features.Reports.GoogleDrivePublishing;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

public sealed class DriveCsrfGuardTests
{
    [Fact]
    public void SameOrigin_IsAllowed()
    {
        DefaultHttpContext context = NewContext("http", "localhost", 8080);
        context.Request.Headers.Origin = "http://localhost:8080";
        DriveCsrfGuard.IsSafe(context.Request).ShouldBeTrue();
    }

    [Fact]
    public void SameOriginRefererWithPath_IsAllowed()
    {
        DefaultHttpContext context = NewContext("http", "localhost", 8080);
        context.Request.Headers.Referer = "http://localhost:8080/saves/abc/reports?drive=connected";
        DriveCsrfGuard.IsSafe(context.Request).ShouldBeTrue();
    }

    [Fact]
    public void DifferentLoopbackPort_IsBlocked()
    {
        // http://localhost:5174 posting to http://localhost:8080 is a
        // different origin even though both hosts are loopback.
        DefaultHttpContext context = NewContext("http", "localhost", 8080);
        context.Request.Headers.Origin = "http://localhost:5174";
        DriveCsrfGuard.IsSafe(context.Request).ShouldBeFalse();
    }

    [Fact]
    public void DifferentScheme_IsBlocked()
    {
        DefaultHttpContext context = NewContext("http", "localhost", 8080);
        context.Request.Headers.Origin = "https://localhost:8080";
        DriveCsrfGuard.IsSafe(context.Request).ShouldBeFalse();
    }

    [Fact]
    public void LoopbackHostMismatch_IsBlocked()
    {
        // localhost vs 127.0.0.1 are different origins; never conflate
        // loopback literals.
        DefaultHttpContext context = NewContext("http", "localhost", 8080);
        context.Request.Headers.Origin = "http://127.0.0.1:8080";
        DriveCsrfGuard.IsSafe(context.Request).ShouldBeFalse();
    }

    [Fact]
    public void EvilOrigin_IsBlocked()
    {
        DefaultHttpContext context = NewContext("http", "localhost", 8080);
        context.Request.Headers.Origin = "https://evil.example";
        DriveCsrfGuard.IsSafe(context.Request).ShouldBeFalse();
    }

    [Fact]
    public void RebindingMatchingHost_IsBlocked()
    {
        // DNS rebinding: attacker hostname resolves to 127.0.0.1 and both
        // Host and Origin are evil.example. Exact Host/Origin equality must
        // not imply trust because the authority is not loopback.
        DefaultHttpContext context = NewContext("http", "evil.example", 8080);
        context.Request.Headers.Origin = "http://evil.example:8080";
        DriveCsrfGuard.IsSafe(context.Request).ShouldBeFalse();
    }

    [Fact]
    public void RebindingWithoutHeaders_IsBlocked()
    {
        DefaultHttpContext context = NewContext("http", "evil.example", 8080);
        DriveCsrfGuard.IsSafe(context.Request).ShouldBeFalse();
    }

    [Fact]
    public void NoHeadersLoopback_IsAllowed()
    {
        DefaultHttpContext context = NewContext("http", "localhost", 8080);
        DriveCsrfGuard.IsSafe(context.Request).ShouldBeTrue();
    }

    private static DefaultHttpContext NewContext(string scheme, string host, int? port)
    {
        DefaultHttpContext context = new();
        context.Request.Scheme = scheme;
        context.Request.Host = port.HasValue ? new HostString(host, port.Value) : new HostString(host);
        return context;
    }
}
