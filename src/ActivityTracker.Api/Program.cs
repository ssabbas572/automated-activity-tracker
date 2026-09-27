using ActivityTracker.Api.Data;
using ActivityTracker.Api.Domain;
using ActivityTracker.Api.Security;
using ActivityTracker.Api.Tracking;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32_768);
builder.Services.AddProblemDetails();
builder.Services.AddDbContextFactory<TrackerDb>(options => options.UseSqlite(
    builder.Configuration.GetConnectionString("Tracker") ?? "Data Source=tracker.db;Foreign Keys=True;Default Timeout=5"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DemoControls>();
builder.Services.AddSingleton<QueueProcessor>();
builder.Services.AddHostedService<ActivityWorker>();
builder.Services.AddAuthentication("Demo").AddScheme<AuthenticationSchemeOptions, DemoIdentity>("Demo", _ => { });
builder.Services.AddAuthorization();
WebApplication app = builder.Build();
if (app.Configuration.GetValue<bool>("Demo:Enabled") && !app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("Demo identity is only supported in Development or Testing. Configure real authentication for production.");
await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<TrackerDb>().InitializeAsync();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
    {
        context.Response.Headers.RetryAfter = "1";
        await Results.Problem(statusCode: 503, detail: "Database is busy. Retry with the same idempotency key.").ExecuteAsync(context);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    // Must run before minimal API body binding so the filter can fingerprint exact request bytes.
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Request.EnableBuffering();
        context.Response.Headers.CacheControl = "no-store";
    }
    await next(context);
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
RouteGroupBuilder api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<CaptureFilter>();
api.MapGet("/cases", async (TrackerDb db, HttpContext http, CancellationToken ct) =>
    Results.Ok(await db.Cases.AsNoTracking().Where(x => x.TenantId == http.TenantId()).ToListAsync(ct)));
api.MapGet("/cases/{caseId:int}/documents/{documentId:int}", async (int caseId, int documentId, TrackerDb db, HttpContext http, CancellationToken ct) =>
{
    Document? document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId && x.CaseId == caseId && x.TenantId == http.TenantId(), ct);
    return document is null ? Results.NotFound() : Results.Ok(document);
}).WithMetadata(new Capture("DocumentOpened"));
api.MapPost("/cases/{caseId:int}/notes", async (int caseId, AddNoteRequest request, TrackerDb db, HttpContext http) =>
{
    if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 4000)
        return Results.Problem(statusCode: 400, detail: "Note text must contain 1–4000 characters.");
    Note note = new() { Id = Guid.NewGuid(), CaseId = caseId, TenantId = http.TenantId(), UserId = http.UserId(), Text = request.Text.Trim() };
    db.Notes.Add(note); // Filter commits the shared unit of work; there is no activity-writing code here.
    return await Task.FromResult(Results.Created($"/api/cases/{caseId}/notes/{note.Id}", note));
}).WithMetadata(new Capture("NoteAdded"));
api.MapGet("/cases/{caseId:int}/notes/{noteId:guid}", async (int caseId, Guid noteId, TrackerDb db, HttpContext http, CancellationToken ct) =>
{
    Note? note = await db.Notes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == noteId && x.CaseId == caseId && x.TenantId == http.TenantId(), ct);
    return note is null ? Results.NotFound() : Results.Ok(note);
});
api.MapPut("/cases/{caseId:int}/documents/{documentId:int}/views/{sessionId:guid}", async (int caseId, int documentId, Guid sessionId, TrackerDb db, HttpContext http, TimeProvider clock, CancellationToken ct) =>
{
    if (!await db.Documents.AnyAsync(x => x.Id == documentId && x.CaseId == caseId && x.TenantId == http.TenantId(), ct)) return Results.NotFound();
    if (await db.ViewSessions.AnyAsync(x => x.Id == sessionId, ct)) return Results.Conflict(new { detail = "Session ID already exists. Retry with the original idempotency key, or create a new session ID." });
    ViewSession session = new() { Id = sessionId, CaseId = caseId, DocumentId = documentId, TenantId = http.TenantId(), UserId = http.UserId(), StartedAtMs = clock.GetUtcNow().ToUnixTimeMilliseconds() };
    db.ViewSessions.Add(session);
    return Results.Ok(session);
}).WithMetadata(new Capture("DocumentViewed", CaptureMode.StartSession));
api.MapPost("/cases/{caseId:int}/documents/{documentId:int}/views/{sessionId:guid}/end", async (int caseId, int documentId, Guid sessionId, TrackerDb db, HttpContext http, TimeProvider clock, CancellationToken ct) =>
{
    ViewSession? session = await db.ViewSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.CaseId == caseId && x.DocumentId == documentId && x.TenantId == http.TenantId() && x.UserId == http.UserId(), ct);
    if (session is null) return Results.NotFound();
    if (session.EndedAtMs is not null) return Results.Conflict(new { detail = "Session already ended. Retry with the original end request key." });
    session.EndedAtMs = Math.Max(session.StartedAtMs, clock.GetUtcNow().ToUnixTimeMilliseconds());
    return Results.Ok(session);
}).WithMetadata(new Capture("DocumentViewed", CaptureMode.CompleteSession));
api.MapGet("/cases/{caseId:int}/activities", async (int caseId, TrackerDb db, HttpContext http, CancellationToken ct) =>
{
    if (!await db.Cases.AnyAsync(x => x.Id == caseId && x.TenantId == http.TenantId(), ct)) return Results.NotFound();
    return Results.Ok(await db.Activities.AsNoTracking().Where(x => x.CaseId == caseId && x.TenantId == http.TenantId()).OrderByDescending(x => x.StartedAtMs).Take(100).ToListAsync(ct));
});
api.MapGet("/cases/{caseId:int}/sessions", async (int caseId, TrackerDb db, HttpContext http, CancellationToken ct) =>
{
    if (!await db.Cases.AnyAsync(x => x.Id == caseId && x.TenantId == http.TenantId(), ct)) return Results.NotFound();
    return Results.Ok(await db.ViewSessions.AsNoTracking().Where(x => x.CaseId == caseId && x.TenantId == http.TenantId() && x.UserId == http.UserId()).OrderByDescending(x => x.StartedAtMs).Take(100).ToListAsync(ct));
});
if (app.Configuration.GetValue<bool>("Demo:Enabled"))
{
    api.MapGet("/demo/queue", async (TrackerDb db, HttpContext http, DemoControls controls, CancellationToken ct) => Results.Ok(new
    {
        paused = controls.Paused,
        items = await db.Queue.AsNoTracking().Where(x => x.TenantId == http.TenantId()).OrderByDescending(x => x.CreatedAtMs)
            .Take(100).Select(x => new { x.Id, x.CaseId, x.State, x.Attempts, x.NextAttemptAtMs, x.LastError }).ToListAsync(ct)
    }));
    api.MapPost("/demo/pause", (DemoControls controls) => { controls.Paused = true; return Results.Ok(); });
    api.MapPost("/demo/resume", (DemoControls controls) => { controls.Paused = false; return Results.Ok(); });
    api.MapPost("/demo/fail-next", (DemoControls controls) => { controls.FailNext(); return Results.Ok(); });
    api.MapPost("/demo/queue/{id:guid}/retry", async (Guid id, TrackerDb db, HttpContext http, CancellationToken ct) =>
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
        QueueItem? item = await db.Queue.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == http.TenantId(), ct);
        if (item is null) return Results.NotFound();
        if (item.State != "DeadLetter") return Results.Conflict();
        item.State = "Pending"; item.Attempts = 0; item.NextAttemptAtMs = 0; item.LastError = null;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Results.Ok();
    });
}
app.Run();
public partial class Program { }
