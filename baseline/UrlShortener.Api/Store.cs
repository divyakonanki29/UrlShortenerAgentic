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
/// </summary>
public class InMemoryUrlStore : IUrlStore
{
    private readonly ConcurrentDictionary<string, ShortUrlEntry> _entries = new();
    private static readonly Random Rng = new();
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public ShortUrlEntry Create(string originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl) || !Uri.IsWellFormedUriString(originalUrl, UriKind.Absolute))
            throw new ArgumentException("Url must be a well-formed absolute URI", nameof(originalUrl));

        string code;
        do
        {
            code = GenerateCode(7);
        } while (_entries.ContainsKey(code)); // collision retry

        var entry = new ShortUrlEntry { Code = code, OriginalUrl = originalUrl };
        _entries[code] = entry;
        return entry;
    }

    public ShortUrlEntry? Get(string code) => _entries.GetValueOrDefault(code);

    public void RecordClick(string code)
    {
        if (_entries.TryGetValue(code, out var entry))
        {
            entry.ClickCount++;
            entry.LastAccessedAt = DateTimeOffset.UtcNow;
        }
    }

    private static string GenerateCode(int length)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = Alphabet[Rng.Next(Alphabet.Length)];
        return new string(chars);
    }
}
