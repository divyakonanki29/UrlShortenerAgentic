# Design — URL Shortener API (revision 1)

## Endpoints
| Method | Path              | Request                | Response                          |
|--------|-------------------|-------------------------|------------------------------------|
| POST   | /shorten          | { "url": "..." }       | 200 { "code": "...", "shortUrl": "..." } / 400 |
| GET    | /{code}           | -                       | 302 redirect to original URL / 404 |
| GET    | /analytics/{code} | -                       | 200 { "clicks": n, "createdAt": "...", "lastAccessedAt": "..." } / 404 |

## Data model
- ShortUrlEntry { Code (string, PK), OriginalUrl (string), CreatedAt, LastAccessedAt, ClickCount }
- No personal data (IP, user-agent, referrer) is stored.

## Input validation
- Only absolute http/https URLs with a host are accepted, so the redirect endpoint can never
  serve javascript:, data: or file: targets.

## Short code generation
- Base62-encoded random 7-character code from Random.Shared (thread-safe), reserved atomically with
  ConcurrentDictionary.TryAdd; a collision retries generation (bounded at 10 attempts) instead of failing.

## Concurrency
- Click count and last-accessed time are updated together under a per-entry lock; readers receive a
  snapshot copy, so analytics never shows a count and timestamp out of step.

## Persistence
- In-memory ConcurrentDictionary for this prototype; interface is small enough to
  swap for EF Core + SQL later without touching the endpoint layer.