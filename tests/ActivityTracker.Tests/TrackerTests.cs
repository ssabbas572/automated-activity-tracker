using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ActivityTracker.Api.Data;
using ActivityTracker.Api.Domain;
using ActivityTracker.Api.Tracking;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ActivityTracker.Tests;

public sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan value) => now += value;
}
public sealed class TestApp(string databasePath) : WebApplicationFactory<Program>
{
    public TestClock Clock { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tracker"] = $"Data Source={databasePath};Foreign Keys=True;Default Timeout=5;Pooling=False",
            ["Demo:Enabled"] = "true"
        }));
        builder.ConfigureServices(services =>
        {
            // Deterministic tests drive the actual processor; no timing-dependent background polling.
            ServiceDescriptor? worker = services.SingleOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(ActivityWorker));
            if (worker is not null) services.Remove(worker);
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
        });
    }
    public HttpClient Client(string identity = "alice")
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "demo-" + identity);
        return client;
    }
    public async Task<TrackerDb> Db() => await Services.GetRequiredService<IDbContextFactory<TrackerDb>>().CreateDbContextAsync();
    public Task<int> Process() => Services.GetRequiredService<QueueProcessor>().ProcessBatchAsync(CancellationToken.None);
    public DemoControls Controls => Services.GetRequiredService<DemoControls>();
}

public sealed class TrackerTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "tracker-" + Guid.NewGuid() + ".db");
    private readonly TestApp app;
    public TrackerTests() => app = new TestApp(path);
    public void Dispose()
    {
        app.Dispose();
        foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }
    private static Task<HttpResponseMessage> Send(HttpClient client, string route, string key, object? body = null, HttpMethod? method = null)
    {
        HttpRequestMessage request = new(method ?? HttpMethod.Post, "/api" + route);
        request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }
    private static Task<HttpResponseMessage> Note(HttpClient client, string key, string text = "Reviewed", int caseId = 1)
        => Send(client, $"/cases/{caseId}/notes", key, new { text });

    [Fact]
    public async Task Note_is_queued_then_projected_and_retries_replay_original_response()
    {
        using HttpClient client = app.Client();
        HttpResponseMessage first = await Note(client, "note-1");
        first.EnsureSuccessStatusCode();
        HttpResponseMessage retry = await Note(client, "note-1");
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.Equal("true", retry.Headers.GetValues("Idempotency-Replayed").Single());
        await using TrackerDb db = await app.Db();
        Assert.Equal(1, await db.Notes.CountAsync()); Assert.Equal(1, await db.Queue.CountAsync());
        Assert.Equal(0, await db.Activities.CountAsync());
        await app.Process(); await app.Process();
        Assert.Equal(1, await db.Activities.CountAsync());
        Activity activity = await db.Activities.SingleAsync();
        Assert.Null(activity.DurationMs); Assert.Equal("NoteAdded", activity.Type);
        Assert.Equal("north", activity.TenantId); Assert.Equal(1, activity.CaseId);
    }
    [Fact]
    public async Task Reused_key_with_changed_body_or_route_is_conflict()
    {
        using HttpClient client = app.Client();
        (await Note(client, "shared")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Note(client, "shared", "Changed")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, "/cases/1/documents/101", "shared", method: HttpMethod.Get)).StatusCode);
    }
    [Fact]
    public async Task Concurrent_duplicate_requests_create_one_note_and_event()
    {
        using HttpClient client = app.Client();
        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Note(client, "concurrent")));
        foreach (HttpResponseMessage response in responses) response.EnsureSuccessStatusCode();
        await using TrackerDb db = await app.Db();
        Assert.Equal(1, await db.Notes.CountAsync()); Assert.Equal(1, await db.Queue.CountAsync());
    }
    [Fact]
    public async Task Missing_key_invalid_note_and_unknown_document_do_not_capture()
    {
        using HttpClient client = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/cases/1/notes", new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Note(client, "bad", "  ")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, "/cases/1/documents/999", "doc", method: HttpMethod.Get)).StatusCode);
        await using TrackerDb db = await app.Db();
        Assert.Empty(await db.Queue.ToListAsync()); Assert.Empty(await db.Receipts.ToListAsync());
    }
    [Fact]
    public async Task Tenant_scope_and_authentication_are_enforced()
    {
        using HttpClient alice = app.Client(); using HttpClient bob = app.Client("bob"); using HttpClient anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/cases")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Note(alice, "north", caseId: 2)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/cases/1/activities")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(alice, "/cases/1/documents/201", "wrong-doc", method: HttpMethod.Get)).StatusCode);
        (await Note(alice, "same-key")).EnsureSuccessStatusCode();
        (await Note(bob, "same-key", caseId: 2)).EnsureSuccessStatusCode();
        await app.Process();
        List<Activity>? north = await alice.GetFromJsonAsync<List<Activity>>("/api/cases/1/activities");
        List<Activity>? south = await bob.GetFromJsonAsync<List<Activity>>("/api/cases/2/activities");
        Assert.Equal("north", Assert.Single(north!).TenantId); Assert.Equal("south", Assert.Single(south!).TenantId);
    }
    [Fact]
    public async Task Duration_uses_server_times_and_cannot_be_closed_by_another_user()
    {
        using HttpClient client = app.Client(); using HttpClient alex = app.Client("alex");
        string route = "/cases/1/documents/101/views/" + Guid.NewGuid();
        (await Send(client, route, "start", method: HttpMethod.Put)).EnsureSuccessStatusCode();
        app.Clock.Advance(TimeSpan.FromMinutes(4));
        (await Send(client, route, "start", method: HttpMethod.Put)).EnsureSuccessStatusCode(); // Must not reset time.
        Assert.Equal(HttpStatusCode.NotFound, (await Send(alex, route + "/end", "other-user")).StatusCode);
        (await Send(client, route + "/end", "end")).EnsureSuccessStatusCode();
        (await Send(client, route + "/end", "end")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, route + "/end", "new-key")).StatusCode);
        await app.Process();
        await using TrackerDb db = await app.Db();
        Activity activity = await db.Activities.SingleAsync();
        Assert.Equal(240_000L, activity.DurationMs); Assert.Equal("DocumentViewed", activity.Type);
    }
    [Fact]
    public async Task Incomplete_sessions_are_persisted_but_not_billed()
    {
        using HttpClient client = app.Client();
        (await Send(client, "/cases/1/documents/101/views/" + Guid.NewGuid(), "start", method: HttpMethod.Put)).EnsureSuccessStatusCode();
        await app.Process();
        await using TrackerDb db = await app.Db();
        Assert.Equal(1, await db.ViewSessions.CountAsync()); Assert.Empty(await db.Activities.ToListAsync());
    }
    [Fact]
    public async Task Failure_after_insert_rolls_back_only_that_item_and_retries_without_duplicates()
    {
        using HttpClient client = app.Client();
        (await Note(client, "one")).EnsureSuccessStatusCode();
        (await Note(client, "two")).EnsureSuccessStatusCode();
        app.Controls.FailNext(); await app.Process();
        await using TrackerDb db = await app.Db();
        Assert.Equal(1, await db.Activities.CountAsync());
        QueueItem failed = await db.Queue.AsNoTracking().SingleAsync(x => x.State == "Pending");
        Assert.Equal(1, failed.Attempts);
        await app.Process(); Assert.Equal(1, await db.Activities.CountAsync()); // Backoff not yet due.
        app.Clock.Advance(TimeSpan.FromSeconds(3)); await app.Process();
        Assert.Equal(2, await db.Activities.CountAsync());
        Assert.Equal(2, await db.Queue.CountAsync(x => x.State == "Completed"));
    }
    [Fact]
    public async Task Poison_event_is_visible_in_dead_letter_and_can_be_requeued()
    {
        using HttpClient client = app.Client();
        (await Note(client, "poison")).EnsureSuccessStatusCode();
        Guid id;
        await using (TrackerDb db = await app.Db())
        {
            QueueItem item = await db.Queue.SingleAsync(); id = item.Id; item.Payload = "broken"; await db.SaveChangesAsync();
        }
        for (int i = 0; i < QueueProcessor.MaxAttempts; i++) { await app.Process(); app.Clock.Advance(TimeSpan.FromMinutes(2)); }
        await using TrackerDb check = await app.Db();
        QueueItem dead = await check.Queue.AsNoTracking().SingleAsync();
        Assert.Equal("DeadLetter", dead.State); Assert.Equal(5, dead.Attempts); Assert.Empty(await check.Activities.ToListAsync());
        (await client.PostAsync($"/api/demo/queue/{id}/retry", null)).EnsureSuccessStatusCode();
        Assert.Equal("Pending", (await check.Queue.AsNoTracking().SingleAsync()).State);
    }
    [Fact]
    public async Task Durable_queue_survives_application_restart()
    {
        using (HttpClient client = app.Client()) (await Note(client, "persisted")).EnsureSuccessStatusCode();
        app.Dispose();
        using TestApp restarted = new(path);
        using HttpClient restartedClient = restarted.Client(); // Starts host against the same file.
        await restarted.Process();
        await using TrackerDb db = await restarted.Db();
        Assert.Equal(1, await db.Activities.CountAsync()); Assert.Equal("Completed", (await db.Queue.SingleAsync()).State);
    }
    [Fact]
    public async Task Queue_write_failure_rolls_back_the_business_action_and_receipt()
    {
        using HttpClient client = app.Client();
        await using (TrackerDb db = await app.Db())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectQueue BEFORE INSERT ON Queue BEGIN SELECT RAISE(ABORT, 'simulated queue persistence failure'); END;");
        Assert.Equal(HttpStatusCode.InternalServerError, (await Note(client, "atomic")).StatusCode);
        await using TrackerDb check = await app.Db();
        Assert.Empty(await check.Notes.ToListAsync());
        Assert.Empty(await check.Receipts.ToListAsync());
        Assert.Empty(await check.Queue.ToListAsync());
        await check.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectQueue;");
        (await Note(client, "atomic")).EnsureSuccessStatusCode();
        Assert.Equal(1, await check.Notes.CountAsync());
    }
    [Fact]
    public async Task Reprocessing_an_existing_event_does_not_duplicate_the_activity()
    {
        using HttpClient client = app.Client();
        (await Note(client, "reprocess")).EnsureSuccessStatusCode();
        await app.Process();
        await using (TrackerDb db = await app.Db())
        {
            QueueItem item = await db.Queue.SingleAsync(); item.State = "Pending";
            await db.SaveChangesAsync();
        }
        await app.Process();
        await using TrackerDb check = await app.Db();
        Assert.Equal(1, await check.Activities.CountAsync());
        Assert.Equal("Completed", (await check.Queue.SingleAsync()).State);
    }

}
