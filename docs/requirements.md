# Requirements — URL Shortener (greenfield)

## In scope
- POST /shorten  -> creates a short code for a long URL
- GET /{code}   -> redirects to the original URL, increments click count
- GET /analytics/{code} -> returns click count + created/last-accessed timestamps
- In-memory persistence (swappable for a real DB later)

## Acceptance criteria
- POST /shorten with an absolute http(s) URL returns 200 and a 7-character code; any other scheme
  (javascript:, data:, file:, ftp:) or a malformed URL returns 400.
- GET /{code} returns 302 to the original URL and counts the click; an unknown code returns 404.
- GET /analytics/{code} returns the click count and timestamps; an unknown code returns 404.
- Under concurrent load no two URLs share a code and no clicks are lost.

## Decisions / ambiguity resolution
- Change type: GREENFIELD - new URL shortener service.
- Ambiguity resolved: custom aliases considered OUT OF SCOPE (not mentioned in the ask).
- Ambiguity resolved: analytics defined as click count + last-accessed timestamp (no PII/referrer tracking).

## Explicit assumptions
- Single-instance deployment (no distributed short-code coordination needed yet)
- No authentication on v1 (flagged as a risk in the final summary)
- No personal data is collected: no IP, user-agent or referrer tracking (enforced by policy CMP-001)