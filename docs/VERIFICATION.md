# Verification record

## Validated repository version

This repository targets **.NET 9** with EF Core SQLite and ASP.NET Core testing packages pinned to **9.0.20**. It was retargeted to match the Visual Studio 2022 development environment. The earlier .NET 10 package is a separate version.

## Executed checks

- SDK: **9.0.318**, runtime **9.0.20**, Linux.
- Release compilation and API publish succeeded.
- Locked dependency restore succeeded with both committed lock files.
- Live .NET 9 Kestrel smoke check passed: startup, demo HTML, note acceptance and background Activity projection.
- Integration tests: **12 passed, 0 failed, 0 skipped** against real SQLite and the ASP.NET Core in-memory HTTP host.
- Project XML and relative Markdown documentation links validated.
- Git whitespace checks passed; local databases, build output and IDE state are excluded.

The test command executed was:

```bash
dotnet test AutomatedActivityTracker.sln -c Release -m:1 -nr:false --logger "trx;LogFileName=integration.trx"
```

The single-process MSBuild flags accommodate the verification environment. Normally `dotnet test AutomatedActivityTracker.sln` is sufficient.

## Integration coverage

| Test area | Verified behaviour |
| --- | --- |
| Note capture and replay | One note/event; original successful response replayed |
| Conflicting key reuse | Different body or route returns 409 |
| Concurrent duplicates | Eight concurrent requests produce one note/event |
| Invalid requests | Missing keys, invalid notes and missing documents do not capture |
| Identity and tenant scope | Anonymous, cross-tenant and wrong-document requests rejected |
| Duration and ownership | Server-calculated four-minute interval, start/end replay and wrong-user rejection |
| Open sessions | Persist without a completed Activity |
| Failure mid-batch | Real projection write rolls back; other event proceeds; delayed retry succeeds |
| Poison event | Visible DeadLetter after five failures and scoped requeue |
| Restart | Pending work survives graceful application-host restart |
| Acceptance atomicity | Queue write failure rolls back business data and receipt |
| Worker idempotency | Reprocessing an existing event does not duplicate its Activity |

## CI configuration versus execution

`.github/workflows/ci.yml` is configured for Windows and Linux restore, Release build, tests and publish validation. It has not been run on GitHub as part of this package preparation. The remote CI result must be checked after pushing. No remote build badge or passing-run claim is included.

## Limits of this verification

Visual Studio/Windows interactive startup, Docker execution, full browser click-through, accessibility, real identity-provider integration, multi-replica operation, abrupt OS process kill, disk exhaustion, production load and public-host deployment have not been tested here. The restart test is a graceful host restart, not a forced process kill. The failure test exercises rollback before commit.
