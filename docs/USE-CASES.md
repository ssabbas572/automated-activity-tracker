# Use cases and expected outputs

Run the API with its Development profile and open http://localhost:5080. These scenarios use seeded sample data. Counts below refer only to the actions in each scenario; existing records may also appear. IDs and timestamps are generated at runtime.

## UC-01 — Add a case note

**Actor:** Alice, tenant north, case 1.

1. Enter `Reviewed executor instructions.` and select **Save note**.
2. Observe the response and wait for the worker.

**Expected:** HTTP 201, one Note, one RequestReceipt and one queued event. The timeline eventually shows one `NoteAdded` Activity with user `alice`, case 1, tenant `north`, and null duration. The queue item becomes Completed. The API does not wait for Activity projection.

## UC-02 — Retry without duplicating work

After UC-01, select **Retry same request**.

**Expected:** original HTTP status and JSON, including the original note ID, are returned. The response includes `Idempotency-Replayed: true`. There is still one note, receipt, queue event and final Activity for this operation.

Selecting **Save note** again uses a fresh key and intentionally creates a new action, even when the text is identical.

## UC-03 — Reject conflicting key reuse

Use `ActivityTracker.http` to save a note with a chosen key. Change its text while retaining the same key, then resend.

**Expected:** HTTP 409 with a problem detail explaining that the key was already used for a different request. No second note or event is created. Exact body bytes are fingerprinted; changes to JSON formatting also count as a different request.

## UC-04 — Open a document

As Alice, select **Open document**.

**Expected:** document 101 content appears and a discrete `DocumentOpened` event is queued. Its final Activity has null duration. Opening does not start a viewing session automatically.

## UC-05 — Record a viewing interval

1. Select **Start viewing**.
2. Wait approximately ten seconds.
3. Select **End view**.

**Expected:** start persists a ViewSession and receipt, without a completed duration event. End closes the session and queues one `DocumentViewed` event. Its Activity has start/end timestamps and approximately 10,000 milliseconds duration. The browser displays seconds; the server determines the duration.

Retrying the original start key does not reset the start time. Retrying the original end key does not create another Activity. Ending the closed session using a fresh key returns HTTP 409.

## UC-06 — Leave a session unfinished

Start a view and close or refresh the page without ending it.

**Expected:** the session remains open in SQLite. No completed duration Activity is produced. The UI can restore an open session for that user. Ending it later includes the intervening elapsed time. There is no idle detection, automatic abandonment timeout or focus tracking.

## UC-07 — Queue work while processing is paused

Pause the worker, then save two notes using separate Save note clicks.

**Expected:** both requests succeed and two Pending queue items appear. Their Activities appear only after processing. Resume the worker; the items become Completed and two Activities are created. A batch already in progress may finish after Pause.

## UC-08 — Recover from a failure after a database write

1. Pause the worker and save two notes.
2. Select **Fail next item once**.
3. Resume the worker.

**Expected:** one item's Activity insert rolls back before commit, its attempts becomes 1 and it stays Pending for retry. The other item can complete. After the retry becomes due, the failed item completes with exactly one Activity. The final failed-attempt count remains 1. The failure warning in server logs is expected.

The test suite invokes this transaction boundary directly and verifies rollback and continuation. It does not rely on catching the brief intermediate UI state.

## UC-09 — Recover after restarting the application

Pause, save a note and confirm Pending. Stop the app, then restart against the same database.

**Expected:** the note and event survive. The worker starts unpaused and processes the event. Deleting the database would reset data and is not a recovery test.

## UC-10 — Keep tenant data separate

As Alice, select **Test tenant isolation**, or request `/api/cases/2/activities` with Alice's token.

**Expected:** HTTP 404. Switch to Bob to access case 2 normally. North and south users see separate timelines and queue items. Alice cannot access document 201 by putting it under case 1 either.

## UC-11 — Protect session ownership

Using the HTTP client, start a session with Alice's token, then attempt to end it with Alex's token.

**Expected:** HTTP 404 and the session remains unchanged. Both users belong to north, but a view can only be closed by its owner. This restriction is separate from case-level tenant access.

## UC-12 — Reject invalid requests

| Request | Expected status | Records created |
| --- | --- | --- |
| API request without a valid token | 401 | None |
| Tracked action without Idempotency-Key | 400 | None |
| Blank note or note exceeding 4,000 characters | 400 | None |
| Unknown document | 404 | None |
| Cross-tenant case | 404 | None |
| Successful key reused with changed input | 409 | No additional records |

A rejected request does not reserve its key. After correcting invalid input, it may be submitted again.

## UC-13 — Retain permanently failing work

The automated poison-event test deliberately corrupts a queued payload and advances the test clock through retries.

**Expected:** five failed attempts, DeadLetter state, retained queue row and no Activity. A tenant-scoped requeue request returns it to Pending with attempts reset. Fix the underlying cause before requeueing; requeue does not repair a payload.

The one-shot UI failure normally succeeds on retry and does not itself demonstrate a permanent poison event.

## UC-14 — Concurrent retries and atomic acceptance

The integration suite sends eight concurrent copies of the same request.

**Expected:** one Note and one event. Unique constraints and the transaction protect the check-and-write sequence.

A separate test installs a temporary SQLite trigger to reject queue insertion.

**Expected:** HTTP 500, with no committed Note, receipt or event. After removing the trigger, the same key can succeed. This proves that business data cannot commit independently of its required capture event.

## End-to-end record count example

On a fresh database: open a document, save a note, retry that note, start viewing and end viewing. Wait for processing.

| Record | New rows | Explanation |
| --- | ---: | --- |
| Note | 1 | The retry reuses the initial result |
| ViewSession | 1 | One completed interval |
| QueueItem | 3 | Open, note and completed view |
| Activity | 3 | One per queued event |
| RequestReceipt | 4 | Open, note, view start and view end |

The start request has a receipt but no completed Activity. The note retry creates no receipt. These counts assume no other actions were performed.
