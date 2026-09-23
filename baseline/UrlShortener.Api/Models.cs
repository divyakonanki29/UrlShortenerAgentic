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
