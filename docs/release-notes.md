# Release Notes

- Scenario: greenfield
- Released at: 2026-09-23T21:48:26.1209424+00:00
- Design revision: 1

## Approvals
- Implementation approved by: Divya
- Release approved by: Divya

## Test evidence
- Method: dotnet test
- Tests: 16 total, 16 passed, 0 failed

## Policy results
| Stage | Gate | Results |
|-------|------|---------|
| requirements | exit | REQ-001=Pass |
| design | exit | DES-001=Pass |
| implementation | entry | DES-002=Pass |
| implementation | exit | SEC-001=Pass; CMP-001=Pass |
| testing | exit | TST-001=Pass |
| docs | exit | DOC-001=Pass |
| release | entry | CHG-001=Pass; CHG-002=Pass; SEC-002=Pass |

## Artifact manifest (SHA-256, verified by the release exit gate)
| File | SHA-256 |
|------|---------|
| `generated/UrlShortener.Api/Models.cs` | `33c372dc01fb965e…` |
| `generated/UrlShortener.Api/Program.cs` | `4717b5ee951701b3…` |
| `generated/UrlShortener.Api/README.md` | `31521cb921389f92…` |
| `generated/UrlShortener.Api/Store.cs` | `02cf7bde85c03d62…` |
| `generated/UrlShortener.Api/UrlShortener.Api.csproj` | `00c88109c0009dae…` |
| `generated/UrlShortener.Tests/ApiIntegrationTests.cs` | `a96cc8ac3c5cacdb…` |
| `generated/UrlShortener.Tests/ShortenerTests.cs` | `0eb0ce8ebf05acd2…` |
| `generated/UrlShortener.Tests/UrlShortener.Tests.csproj` | `de1470bba3ece028…` |