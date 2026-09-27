using System.Text.Json;
using ActivityTracker.Api.Data;
using ActivityTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ActivityTracker.Api.Tracking;

public sealed class DemoControls
{
    private int paused;
    private int failNext;
    public bool Paused { get => Volatile.Read(ref paused) == 1; set => Interlocked.Exchange(ref paused, value ? 1 : 0); }
    public void FailNext() => Interlocked.Exchange(ref failNext, 1);
    public void ThrowIfArmed()
    {
        if (Interlocked.Exchange(ref failNext, 0) == 1)
            throw new InvalidOperationException("Demonstration failure after projection write, before commit.");
    }
}

public sealed class QueueProcessor(IDbContextFactory<TrackerDb> factory, TimeProvider clock,
    DemoControls controls, ILogger<QueueProcessor> logger)
{
    public const int MaxAttempts = 5;
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        if (controls.Paused) return 0;
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        await using TrackerDb reader = await factory.CreateDbContextAsync(ct);
        List<Guid> ids = await reader.Queue.AsNoTracking()
            .Where(x => x.State == "Pending" && x.NextAttemptAtMs <= now)
            .OrderBy(x => x.CreatedAtMs).ThenBy(x => x.Id).Select(x => x.Id).Take(20).ToListAsync(ct);
        foreach (Guid id in ids)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessOneAsync(id, ct);
        }
        return ids.Count;
    }

    private async Task ProcessOneAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await using TrackerDb db = await factory.CreateDbContextAsync(ct);
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
            QueueItem item = await db.Queue.SingleAsync(x => x.Id == id, ct);
            if (item.State != "Pending" || item.NextAttemptAtMs > clock.GetUtcNow().ToUnixTimeMilliseconds()) return;
            ActivityEvent data = JsonSerializer.Deserialize<ActivityEvent>(item.Payload)
                ?? throw new InvalidOperationException("Invalid event payload.");
            if (data.Id != item.Id || data.TenantId != item.TenantId || data.CaseId != item.CaseId ||
                data.EndedAtMs < data.StartedAtMs ||
                !await db.Cases.AnyAsync(x => x.Id == data.CaseId && x.TenantId == data.TenantId, ct) ||
                !await db.Users.AnyAsync(x => x.Id == data.UserId && x.TenantId == data.TenantId, ct))
                throw new InvalidOperationException("Invalid event scope or duration.");
            if (!await db.Activities.AnyAsync(x => x.Id == data.Id, ct))
                db.Activities.Add(new Activity { Id = data.Id, TenantId = data.TenantId, CaseId = data.CaseId,
                    UserId = data.UserId, Type = data.Type, StartedAtMs = data.StartedAtMs,
                    EndedAtMs = data.EndedAtMs, SessionId = data.SessionId });
            // Save inside transaction first so fault injection proves rollback of an actual DB write.
            await db.SaveChangesAsync(ct);
            controls.ThrowIfArmed();
            item.State = "Completed";
            item.LastError = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning(error, "Event {EventId} failed; retaining it for retry", id);
            // The failed context and transaction are disposed before recording a retry.
            await using TrackerDb retryDb = await factory.CreateDbContextAsync(ct);
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction retryTx = await retryDb.Database.BeginTransactionAsync(ct);
            QueueItem item = await retryDb.Queue.SingleAsync(x => x.Id == id, ct);
            if (item.State != "Pending") return;
            item.Attempts++;
            item.LastError = error.GetType().Name; // Details stay in server logs, never note/document content.
            item.NextAttemptAtMs = clock.GetUtcNow().AddSeconds(Math.Pow(2, item.Attempts)).ToUnixTimeMilliseconds();
            if (item.Attempts >= MaxAttempts) item.State = "DeadLetter";
            await retryDb.SaveChangesAsync(ct);
            await retryTx.CommitAsync(ct);
        }
    }
}

public sealed class ActivityWorker(QueueProcessor processor, ILogger<ActivityWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.ProcessBatchAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Queue polling failed; durable work remains available");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
