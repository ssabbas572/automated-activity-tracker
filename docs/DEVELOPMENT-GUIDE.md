# Step-by-step development and understanding

This guide explains the solution in the order you should learn it. First run the demo using the README. Then open the referenced files while reading. The short README is for the evaluator; this longer guide is for your preparation.

## 1. Understand the problem before choosing patterns

A lawyer adds a note. Saving the note is the immediate task. Recording the corresponding activity should happen automatically, and activity processing should not delay that task. However, if the note succeeds and the tracking event disappears, our audit trail is incomplete.

There are therefore two stages:

1. **Capture reliably:** save the business action and a small durable event together.
2. **Process later:** read the event and create the Activity record.

Memory line: **Action + event save together; Activity builds later.**

Roman Urdu: *Note aur uska event aik saath save hotay hain. Activity background worker baad mein banata hai.*

## 2. Understand the tables

| Entity | Purpose | Example |
| --- | --- | --- |
| Case | Legal matter, owned by a tenant | Riverside Estate, tenant north |
| User | Person and tenant membership for this demo | Alice, north |
| Document | Document belonging to a case and tenant | Estate summary |
| Note | Actual business data | “Reviewed executor instructions” |
| ViewSession | Durable start/end of one viewing interval | 12:00 to 12:04 |
| RequestReceipt | Remembers successful requests | key `abc`, response JSON |
| QueueItem | Work waiting for the worker | Pending NoteAdded event |
| Activity | Readable audit/billing candidate | DocumentViewed, 240 seconds |

A QueueItem is not the final Activity. Separating them is what makes asynchronous processing visible and testable.

The session is necessary because an HTTP GET lasts milliseconds while a person may read a document for several minutes. Request duration is not viewing duration.

## 3. Read Program.cs: the composition root

`Program.cs` registers services, creates the initial database and maps routes.

- `AddDbContextFactory<TrackerDb>` lets the worker create a fresh database context per operation and also supplies scoped contexts for requests.
- `AddSingleton(TimeProvider.System)` gives a clock abstraction. Tests replace it with a manually controlled clock.
- `AddHostedService<ActivityWorker>` starts and stops the polling worker with the application.
- Authentication maps a demo token to a user and tenant; authorization protects the API group.
- `.AddEndpointFilter<CaptureFilter>()` attaches the capture mechanism to that group.

DI memory line: **Request owns its context; worker creates its own context.** Never enqueue a request-scoped DbContext or HttpContext for use after the request ends.

## 4. Follow one note request

Send the note example in `ActivityTracker.http`, or click Save note.

1. Authentication establishes Alice/north.
2. Body buffering runs before minimal API model binding so request bytes can later be fingerprinted.
3. The filter finds `Capture("NoteAdded")` on the route.
4. It validates the idempotency key and checks that the case belongs to north.
5. It hashes method + path + query + exact body bytes.
6. It opens a database write transaction and looks for an existing receipt.
7. If there is no receipt, the note handler adds a Note to the context. It contains no tracking-writing code.
8. The filter adds the immutable ActivityEvent to the queue and records the response receipt.
9. One SaveChanges and transaction commit persist all three records.
10. The HTTP response returns. The worker can process the event afterward.

If saving any of these database records fails, the transaction rolls back. The client can safely retry with the same key. If the commit succeeded but the response was lost, that retry receives the stored response.

Notice the boundary: the request must wait for **durable acceptance**, but never waits for `QueueProcessor.ProcessBatchAsync`.

## 5. Understand the shared capture filter

Open `Tracking/CaptureFilter.cs`. A filter wraps a handler through `next(invocation)`:

- Before `next`: authenticate scope, deduplicate, begin transaction.
- `next`: execute business logic.
- After successful `next`: save event and receipt, commit.
- Failed result: dispose without commit.

Why metadata instead of checking URL strings? A route's declaration says which event it represents. New route types can opt in explicitly without editing one giant path-switch statement. The filter still uses route values for the case and session identifiers.

Not every GET should be tracked. Listing the Activity timeline has no Capture metadata, so polling the timeline does not create more activities.

## 6. Add a new discrete action

Suppose you need “case call logged.”

1. Define the business request and entity, if it needs persistence.
2. Map a route in the authenticated API group using `{caseId:int}`.
3. Validate its business inputs.
4. Add its business entity to the shared TrackerDb; do not commit elsewhere or call a remote billing service inside the handler.
5. Return a JSON result.
6. Append `.WithMetadata(new Capture("CallLogged"))`.
7. Add an integration test proving one business record and one event after retries.

The existing filter queues it; the worker copies its type to an Activity. No new worker switch is required for another discrete activity. If the new event requires extra business fields, evolve ActivityEvent and its validation deliberately.

## 7. Understand duration

Start request:

```http
PUT /api/cases/1/documents/101/views/{new-guid}
Authorization: Bearer demo-alice
Idempotency-Key: unique-start-key
```

It persists a ViewSession using the server clock. It does not create a billable duration yet. `CaptureMode.StartSession` still provides the shared transaction and duplicate protection.

End request:

```http
POST /api/cases/1/documents/101/views/{same-guid}/end
Authorization: Bearer demo-alice
Idempotency-Key: unique-end-key
```

The endpoint verifies case, document, tenant and user ownership, closes the session, and the filter queues its start and end. For a start at 12:00 and end at 12:04, the Activity's `DurationMs` is 240000. The four-minute integration test advances a fake server clock; it does not sleep four minutes.

A retry of start returns the original start response even if the session has since ended. That is intentional response replay, not a fresh read. Use the sessions API for current state.

Memory line: **Open is a moment; viewing is an interval.**

## 8. Understand duplicate protection at two boundaries

**HTTP boundary:** a unique tenant/user/key receipt prevents the business action from executing twice. The stored fingerprint prevents accidentally reusing the same key for another action. An exact-body fingerprint treats different JSON whitespace as a different request, so retries must preserve the body.

**Worker boundary:** the event ID is the Activity primary key. A unique session index also prevents two final Activities for one viewing session. The Activity insert and acknowledgement are in the same transaction.

Checking “does it exist?” without a transaction or unique constraint is insufficient: two concurrent callers can both observe “no.” This solution serializes SQLite writers in immediate transactions and keeps unique constraints in the schema.

Roman Urdu: *Same key se retry karo to pehla result milta hai. Naya kaam ho to nayi key banao.*

## 9. Follow background processing

Open `Tracking/ActivityWorker.cs`:

1. Poll up to 20 due Pending IDs.
2. For each ID, create a fresh context and transaction.
3. Reload the row and check it is still due.
4. Deserialize and validate the event's scope and duration.
5. Insert the Activity if its ID is absent.
6. Mark the queue item Completed.
7. Commit both database changes together.

Each item commits separately. Failure on item 2 does not undo item 1, and item 3 can continue. The failing item remains Pending for a later attempt. No memory-only “in-flight” acknowledgement is needed because all state changes are transactional.

The worker's outer loop also handles a database polling failure. It logs the error and retries later; the durable rows remain in SQLite.

## 10. Understand failures precisely

| Failure point | Outcome |
| --- | --- |
| Business action before request commit | Note, receipt and queue event roll back |
| Response lost after request commit | Same-key retry returns the receipt |
| Worker crashes before its transaction commit | Activity and completion update roll back; event remains pending |
| Worker crashes after commit | Activity exists and queue is completed |
| One event repeatedly fails | Backoff, then visible DeadLetter after five failures |
| Client never ends a view | Open session persists; no completed duration Activity |
| SQLite is temporarily write-locked | Busy response may be 503; retry same request key |

The demo fault happens **after an actual Activity database insert inside the transaction**, before completion/commit. This tests rollback of real work, not just throwing before anything happened.

## 11. Understand tenant isolation

Tenant identity is not taken from a body field. Demo authentication supplies it from a server-owned mapping. A caller cannot select another tenant by sending `TenantId` in JSON.

Every case/document lookup checks the tenant. Sessions additionally check the owning user. Activity reads filter by case and tenant. The worker revalidates event scope, and composite foreign keys prevent a record from referencing a case under the wrong tenant.

This is a demonstration of the scoping design. Anyone knowing a fixed demo token can impersonate that demo identity; real deployment needs validated identity-provider tokens and real memberships.

## 12. Reproduce and read the tests

Run `dotnet test` at the solution root. Open `TrackerTests.cs` afterward.

Tests send real HTTP requests through the ASP.NET Core in-memory host into the actual filter, handlers and SQLite database. Each test gets a separate temporary database. Background timing is disabled in tests; the test invokes the actual processor at controlled moments.

Look especially at:

- Concurrent duplicate requests: database-level duplicate handling.
- Duration test: exactly four minutes, wrong-user rejection, start/end replay.
- Mid-batch failure: rollback + continuation + delayed retry.
- Poison event: maximum attempts and requeue.
- Restart: dispose one host, create another against the same database, process retained work.

The restart test is a graceful host restart, not a forced OS process kill. The failure test exercises the before-commit transaction boundary; a dedicated process-kill test would add confidence for production.

## 13. Present this in an interview

A concise explanation:

> I used a shared endpoint filter so the business handlers do not write tracking records. The filter commits the action, idempotency receipt and outbox event together in SQLite. A hosted worker creates Activities later, with per-event transactions, retry backoff and visible dead letters. Discrete activities store a timestamp; duration activities use server-timed viewing sessions. Tenant scope comes from identity, with scoped queries and database constraints.

If asked why not just `Task.Run`: that work is untracked and can disappear on shutdown, and may outlive request-scoped services.

If asked why not a channel: a channel works for a smaller in-process guarantee, but this design also preserves accepted work across restarts.

If asked whether this is exactly once: processing attempts are at least once; transactional, idempotent database effects prevent duplicate final Activities within this design.

If asked how to scale: introduce safe claims/leases and a database designed for concurrent writers, or relay the outbox to a broker. Preserve idempotency and tenant scope across the new boundary.

## 14. Suggested development order for rebuilding it yourself

1. Scaffold an empty .NET Web API and add the EF Core SQLite package.
2. Add Case, User, Document, Note and seed data; make the business endpoints work.
3. Add ViewSession and start/end endpoints using server timestamps.
4. Add QueueItem, Activity and RequestReceipt with constraints.
5. Write the shared filter and metadata; prove one note produces one queued event.
6. Add the background worker and per-item transactions.
7. Add idempotency replay/conflict checks and concurrency tests.
8. Add backoff/dead letters and deterministic failure tests.
9. Add tenant/user boundaries and negative tests.
10. Add a thin demo UI and write the short decision-focused README.

Avoid building a generic framework first. Use the smallest design that demonstrates the requirements and exposes its limits honestly.

## Reference documentation

- ASP.NET Core endpoint filters: https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/min-api-filters
- Hosted background services: https://learn.microsoft.com/aspnet/core/fundamentals/host/hosted-services
- EF Core transactions: https://learn.microsoft.com/ef/core/saving/transactions
- Microsoft.Data.Sqlite transactions: https://learn.microsoft.com/dotnet/standard/data/sqlite/transactions
- SQLite async limitations: https://learn.microsoft.com/dotnet/standard/data/sqlite/async
