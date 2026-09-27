# Live demo and evaluator walkthrough

## Start

1. Install the .NET 9 SDK.
2. Extract the ZIP, open a terminal in the folder containing the solution.
3. Run `dotnet run --project src/ActivityTracker.Api --launch-profile ActivityTracker.Api`.
4. Open http://localhost:5080 in your browser.
5. Keep the terminal running. Stop with Ctrl+C after the demonstration.

The page and API share an origin, so no CORS configuration is needed. The default profile binds localhost. The UI is real, not a client-only mock. If using an IDE that does not open the browser automatically, open the URL manually.

## A five-minute presentation

| Step | Action | What to explain |
| --- | --- | --- |
| 1 | Save a note | Business response arrives before background Activity projection |
| 2 | Retry same request | Same response is replayed; one note and one event |
| 3 | Start viewing, wait, End view | Server timestamps produce actual elapsed duration |
| 4 | Pause worker, save two notes | Requests still work and Pending rows remain durable |
| 5 | Fail next item once, Resume | One transaction rolls back; other work proceeds; retry completes without duplicate |
| 6 | Test tenant isolation | Alice cannot access case 2; API responds 404 |
| 7 | Switch to Bob | A separate case and timeline are visible |

The pause control prevents new batches; an in-progress batch may finish. This is adequate for inspecting the demo queue. Demo pause/failure controls are intentionally process-wide.

## Restart recovery

Pause the worker, save a note and confirm its Pending row. Stop the application with Ctrl+C, restart with the same command and refresh the browser. The worker starts unpaused and processes the saved row. Do not delete the SQLite file between runs.

## Common setup issues

- `dotnet` not recognized: install the **SDK**, reopen the terminal, check `dotnet --info`.
- NuGet restore fails: the first restore requires internet access to NuGet; check proxy/network configuration.
- 401: use `--launch-profile ActivityTracker.Api`; it sets Development and enables demo identities.
- 400 on action routes: supply an `Idempotency-Key` (UI handles it automatically).
- 409: you reused a key for different bytes, or tried to close an already closed session with a new key.
- No activities while paused: resume the worker; otherwise inspect Pending/DeadLetter status and server logs.
- A viewing session is still open after refresh: the UI restores it; click End view.
- Port occupied: use `dotnet run --project src/ActivityTracker.Api --launch-profile ActivityTracker.Api --urls http://localhost:5081`, then open port 5081.
- Schema changed during your own development: stop the app and delete its `tracker.db` plus matching `-wal` / `-shm` files only if you are happy to discard all demo data. A real deployment should use migrations.

## Public hosting status

No public URL is provisioned by this deliverable. The available website publishing service does not run a .NET process. The Dockerfile and compose configuration give you a runnable deployment unit; the compose configuration is intentionally bound to localhost.

For an evaluator, the simplest live demonstration is running the actual app locally and screen sharing it. For a remotely accessible environment, use a .NET-capable container host or server with persistent disk, one replica, HTTPS, and access restricted to the evaluator. If retaining the fixed demo identities, the entire environment must remain access-restricted and contain only sample data. Real/public use requires replacing DemoIdentity with proper authentication and protecting/removing demo control endpoints. Do not treat the fixed tokens as secrets.

The database path is configurable through `ConnectionStrings__Tracker`. In the Dockerfile it is `/data/tracker.db`; keep `/data` on persistent storage. Docker Compose creates a named volume for this automatically. Without durable storage, restarting a replaced container can discard the database.
