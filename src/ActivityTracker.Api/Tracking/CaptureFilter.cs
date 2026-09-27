using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ActivityTracker.Api.Data;
using ActivityTracker.Api.Domain;
using ActivityTracker.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace ActivityTracker.Api.Tracking;

public enum CaptureMode { Discrete, StartSession, CompleteSession }
public sealed record Capture(string Type, CaptureMode Mode = CaptureMode.Discrete);

// A single filter wraps all opted-in actions. Endpoint bodies contain business logic only.
public sealed class CaptureFilter(TrackerDb db, TimeProvider clock) : IEndpointFilter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        HttpContext http = invocation.HttpContext;
        Capture? policy = http.GetEndpoint()?.Metadata.GetMetadata<Capture>();
        if (policy is null) return await next(invocation);
        CancellationToken ct = http.RequestAborted;
        string key = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            return Results.Problem(statusCode: 400, detail: "Supply an Idempotency-Key of 1–128 characters; reuse it only when retrying this exact action.");
        int caseId = Convert.ToInt32(http.Request.RouteValues["caseId"]);
        string tenant = http.TenantId();
        string user = http.UserId();
        // Authorization precedes replay: a receipt never bypasses current case scope.
        if (!await db.Cases.AnyAsync(x => x.Id == caseId && x.TenantId == tenant, ct)) return Results.NotFound();
        http.Request.Body.Position = 0;
        using StreamReader reader = new(http.Request.Body, Encoding.UTF8, leaveOpen: true);
        string body = await reader.ReadToEndAsync(ct);
        http.Request.Body.Position = 0;
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{http.Request.Method}\n{http.Request.Path}\n{http.Request.QueryString}\n{body}")));

        // SQLite's immediate write transaction serializes competing writers, including duplicate requests.
        // Business mutations, receipt and outbox row either all commit or all roll back.
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
        RequestReceipt? receipt = await db.Receipts.SingleOrDefaultAsync(x => x.TenantId == tenant && x.UserId == user && x.Key == key, ct);
        if (receipt is not null)
        {
            if (receipt.Fingerprint != fingerprint) return Results.Problem(statusCode: 409, detail: "This idempotency key was already used for a different request.");
            http.Response.Headers["Idempotency-Replayed"] = "true";
            return Results.Content(receipt.ResponseJson, "application/json", statusCode: receipt.StatusCode);
        }
        object? result = await next(invocation);
        int status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        if (status is < 200 or >= 300) return result; // Dispose rolls back; failures do not reserve the key.
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (policy.Mode != CaptureMode.StartSession)
        {
            long start = now;
            long? end = null;
            Guid? sessionId = null;
            if (policy.Mode == CaptureMode.CompleteSession)
            {
                sessionId = Guid.Parse(http.Request.RouteValues["sessionId"]!.ToString()!);
                ViewSession session = await db.ViewSessions.SingleAsync(x => x.Id == sessionId && x.TenantId == tenant && x.CaseId == caseId && x.UserId == user, ct);
                start = session.StartedAtMs;
                end = session.EndedAtMs ?? throw new InvalidOperationException("Completion endpoint did not close the session.");
            }
            ActivityEvent activityEvent = new(Guid.NewGuid(), tenant, caseId, user, policy.Type, start, end, sessionId);
            db.Queue.Add(new QueueItem { Id = activityEvent.Id, TenantId = tenant, CaseId = caseId, Payload = JsonSerializer.Serialize(activityEvent), CreatedAtMs = now });
        }
        string response = JsonSerializer.Serialize((result as IValueHttpResult)?.Value, Json);
        db.Receipts.Add(new RequestReceipt { TenantId = tenant, UserId = user, Key = key, Fingerprint = fingerprint, StatusCode = status, ResponseJson = response });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Content(response, "application/json", statusCode: status);
    }
}
