using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using UrlShortener.Api;

namespace UrlShortener.Tests;

public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        // Don't follow redirects: the redirect response itself is what we assert on.
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private async Task<ShortenResponse> ShortenAsync(string url)
    {
        var response = await _client.PostAsJsonAsync("/shorten", new ShortenRequest(url));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ShortenResponse>())!;
    }

    [Fact]
    public async Task Shorten_ValidUrl_ReturnsCodeAndShortUrl()
    {
        var result = await ShortenAsync("https://example.com/some/long/path");

        Assert.Equal(7, result.Code.Length);
        Assert.EndsWith("/" + result.Code, result.ShortUrl);
    }

    [Fact]
    public async Task Shorten_InvalidUrl_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/shorten", new ShortenRequest("not-a-url"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Shorten_NonHttpScheme_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/shorten", new ShortenRequest("javascript:alert(document.cookie)"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Redirect_KnownCode_RedirectsToOriginalUrl()
    {
        var result = await ShortenAsync("https://example.com/target");

        var response = await _client.GetAsync("/" + result.Code);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://example.com/target", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Redirect_UnknownCode_Returns404()
    {
        var response = await _client.GetAsync("/doesNotExist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Analytics_AfterRedirects_ReportsClickCountAndLastAccessed()
    {
        var result = await ShortenAsync("https://example.com/tracked");

        var before = await _client.GetFromJsonAsync<AnalyticsResponse>("/analytics/" + result.Code);
        Assert.Equal(0, before!.Clicks);
        Assert.Null(before.LastAccessedAt);

        await _client.GetAsync("/" + result.Code);
        await _client.GetAsync("/" + result.Code);

        var after = await _client.GetFromJsonAsync<AnalyticsResponse>("/analytics/" + result.Code);
        Assert.Equal(2, after!.Clicks);
        Assert.NotNull(after.LastAccessedAt);
    }

    [Fact]
    public async Task Analytics_UnknownCode_Returns404()
    {
        var response = await _client.GetAsync("/analytics/doesNotExist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}