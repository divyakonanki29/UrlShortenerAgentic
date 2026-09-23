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