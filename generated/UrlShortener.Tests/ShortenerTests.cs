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