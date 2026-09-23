namespace Orchestrator;

public static class ApiTemplates
{
    public const string ModelsCs = """
        namespace UrlShortener.Api;

        public class ShortUrlEntry
        {
            public required string Code { get; init; }
            public required string OriginalUrl { get; init; }
            public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
            public DateTimeOffset? LastAccessedAt { get; set; }
            public long ClickCount { get; set; }
        }

        public record ShortenRequest(string Url);
        public record ShortenResponse(string Code, string ShortUrl);
        public record AnalyticsResponse(long Clicks, DateTimeOffset CreatedAt, DateTimeOffset? LastAccessedAt);
        """;

    public const string StoreCs = """
        using System.Collections.Concurrent;

        namespace UrlShortener.Api;

        public interface IUrlStore
        {
            ShortUrlEntry Create(string originalUrl);
            ShortUrlEntry? Get(string code);
            void RecordClick(string code);
        }

        /// <summary>
        /// In-memory store for the prototype. Swap this implementation for an
        /// EF Core-backed one later without touching the endpoint layer.
        ///
        /// Thread-safety: codes are reserved atomically (TryAdd), click updates
        /// happen under a per-entry lock, and callers only ever receive
        /// snapshots, so a reader never sees a half-applied update.
        /// </summary>
        public class InMemoryUrlStore : IUrlStore
        {
            private readonly ConcurrentDictionary<string, ShortUrlEntry> _entries = new();
            private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            private const int CodeLength = 7;
            private const int MaxCodeAttempts = 10;

            public ShortUrlEntry Create(string originalUrl)
            {
                if (!IsAllowedUrl(originalUrl))
                    throw new ArgumentException("Url must be an absolute http or https URI", nameof(originalUrl));

                for (int attempt = 0; attempt < MaxCodeAttempts; attempt++)
                {
                    var entry = new ShortUrlEntry { Code = GenerateCode(), OriginalUrl = originalUrl };
                    if (_entries.TryAdd(entry.Code, entry)) // atomic reserve: no check-then-act race
                        return Snapshot(entry);
                }

                // 62^7 codes make this practically unreachable; fail loudly rather than loop forever.
                throw new InvalidOperationException($"Could not allocate a unique short code after {MaxCodeAttempts} attempts");
            }

            public ShortUrlEntry? Get(string code) =>
                _entries.TryGetValue(code, out var entry) ? Snapshot(entry) : null;

            public void RecordClick(string code)
            {
                if (!_entries.TryGetValue(code, out var entry)) return;
                lock (entry)
                {
                    entry.ClickCount++;
                    entry.LastAccessedAt = DateTimeOffset.UtcNow;
                }
            }

            /// <summary>
            /// Only http/https URLs with a host may become redirect targets; this
            /// blocks javascript:, data:, file: and similar schemes.
            /// </summary>
            public static bool IsAllowedUrl(string? url) =>
                Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrEmpty(uri.Host);

            private static ShortUrlEntry Snapshot(ShortUrlEntry entry)
            {
                lock (entry)
                {
                    return new ShortUrlEntry
                    {
                        Code = entry.Code,
                        OriginalUrl = entry.OriginalUrl,
                        CreatedAt = entry.CreatedAt,
                        LastAccessedAt = entry.LastAccessedAt,
                        ClickCount = entry.ClickCount
                    };
                }
            }

            // Random.Shared is thread-safe, unlike a shared `new Random()` instance.
            private static string GenerateCode() =>
                string.Create(CodeLength, 0, static (chars, _) =>
                {
                    for (int i = 0; i < chars.Length; i++)
                        chars[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
                });
        }
        """;

    public const string ProgramCs = """
        using UrlShortener.Api;

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<IUrlStore, InMemoryUrlStore>();

        var app = builder.Build();

        app.MapPost("/shorten", (ShortenRequest req, IUrlStore store, HttpRequest http) =>
        {
            try
            {
                var entry = store.Create(req.Url);
                var shortUrl = $"{http.Scheme}://{http.Host}/{entry.Code}";
                return Results.Ok(new ShortenResponse(entry.Code, shortUrl));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/{code}", (string code, IUrlStore store) =>
        {
            var entry = store.Get(code);
            if (entry is null) return Results.NotFound();
            store.RecordClick(code);
            return Results.Redirect(entry.OriginalUrl, permanent: false);
        });

        app.MapGet("/analytics/{code}", (string code, IUrlStore store) =>
        {
            var entry = store.Get(code);
            if (entry is null) return Results.NotFound();
            return Results.Ok(new AnalyticsResponse(entry.ClickCount, entry.CreatedAt, entry.LastAccessedAt));
        });

        app.Run();

        public partial class Program { } // exposed for WebApplicationFactory in tests
        """;

    public const string TestsCsMinimal = """
        using Xunit;
        using UrlShortener.Api;

        namespace UrlShortener.Tests;

        public class ShortenerTests
        {
            [Fact]
            public void Create_ReturnsEntryWithMatchingUrl()
            {
                var store = new InMemoryUrlStore();
                var entry = store.Create("https://example.com/some/long/path");
                Assert.Equal("https://example.com/some/long/path", entry.OriginalUrl);
                Assert.Equal(7, entry.Code.Length);
            }
        }
        """;

    public const string TestsCsFull = """
        using Xunit;
        using UrlShortener.Api;

        namespace UrlShortener.Tests;

        public class ShortenerTests
        {
            [Fact]
            public void Create_ReturnsEntryWithMatchingUrl()
            {
                var store = new InMemoryUrlStore();
                var entry = store.Create("https://example.com/some/long/path");
                Assert.Equal("https://example.com/some/long/path", entry.OriginalUrl);
                Assert.Equal(7, entry.Code.Length);
            }

            [Fact]
            public void Create_RejectsInvalidUrl()
            {
                var store = new InMemoryUrlStore();
                Assert.Throws<ArgumentException>(() => store.Create("not-a-url"));
            }

            [Theory]
            [InlineData("javascript:alert(1)")]
            [InlineData("data:text/html,<script>alert(1)</script>")]
            [InlineData("file:///etc/passwd")]
            [InlineData("ftp://example.com/file")]
            public void Create_RejectsNonHttpSchemes(string url)
            {
                var store = new InMemoryUrlStore();
                Assert.Throws<ArgumentException>(() => store.Create(url));
            }

            [Fact]
            public void RecordClick_IncrementsCountAndSetsLastAccessed()
            {
                var store = new InMemoryUrlStore();
                var entry = store.Create("https://example.com");
                store.RecordClick(entry.Code);
                store.RecordClick(entry.Code);

                var refreshed = store.Get(entry.Code)!;
                Assert.Equal(2, refreshed.ClickCount);
                Assert.NotNull(refreshed.LastAccessedAt);
            }

            [Fact]
            public void RecordClick_UnderConcurrency_LosesNoUpdates()
            {
                var store = new InMemoryUrlStore();
                var entry = store.Create("https://example.com/hot");

                Parallel.For(0, 5_000, _ => store.RecordClick(entry.Code));

                Assert.Equal(5_000, store.Get(entry.Code)!.ClickCount);
            }

            [Fact]
            public void Create_UnderConcurrency_IssuesUniqueCodes()
            {
                var store = new InMemoryUrlStore();
                var codes = new System.Collections.Concurrent.ConcurrentBag<string>();

                Parallel.For(0, 10_000, i => codes.Add(store.Create($"https://example.com/{i}").Code));

                Assert.Equal(10_000, codes.Distinct().Count());
                Assert.All(codes, c => Assert.NotNull(store.Get(c)));
            }
        }
        """;

    /// <summary>
    /// End-to-end tests through the real HTTP pipeline (routing, model binding,
    /// status codes, redirects) using an in-process test server.
    /// </summary>
    public const string IntegrationTestsCs = """
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
        """;

    public const string ApiCsproj = """
        <Project Sdk="Microsoft.NET.Sdk.Web">

          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <RootNamespace>UrlShortener.Api</RootNamespace>
          </PropertyGroup>

        </Project>
        """;

    public const string TestsCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <IsPackable>false</IsPackable>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.0.11" />
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
            <PackageReference Include="xunit" Version="2.9.2" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
          </ItemGroup>

          <ItemGroup>
            <ProjectReference Include="..\UrlShortener.Api\UrlShortener.Api.csproj" />
          </ItemGroup>

        </Project>
        """;

    public const string ApiReadme = """
        # UrlShortener.Api

        Generated by the agentic orchestrator's Implementation + Docs stages.

        ## Endpoints
        - `POST /shorten` — body `{ "url": "https://..." }`, returns the short code + short URL.
          Only absolute `http`/`https` URLs are accepted; anything else (including
          `javascript:`, `data:`, `file:`) returns 400.
        - `GET /{code}` — 302 redirect to the original URL; 404 for unknown codes
        - `GET /analytics/{code}` — returns click count and timestamps; 404 for unknown codes

        ## Run
        ```
        dotnet run --project generated/UrlShortener.Api
        ```

        ## Concurrency guarantees
        - Short codes are reserved atomically, so concurrent requests never share a code.
        - Click counts are updated under a per-entry lock, so no clicks are lost under load.

        ## Known limitations
        - In-memory store only (data lost on restart)
        - No authentication on the analytics endpoint
        - No rate limiting, so short codes can be enumerated slowly
        - Single-instance only; code uniqueness is per process, not distributed
        """;
}
