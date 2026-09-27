namespace ActivityTracker.Api.Domain;

public sealed class Case
{
    public int Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class User
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TenantId { get; set; } = "";
}

public sealed class Document
{
    public int Id { get; set; }
    public int CaseId { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class Note
{
    public Guid Id { get; set; }
    public int CaseId { get; set; }
    public string TenantId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class ViewSession
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public int CaseId { get; set; }
    public int DocumentId { get; set; }
    public string UserId { get; set; } = "";
    public long StartedAtMs { get; set; }
    public long? EndedAtMs { get; set; }
}
// Immutable event data is copied into the durable queue; no HttpContext or DbContext is queued.
public sealed record ActivityEvent(Guid Id, string TenantId, int CaseId, string UserId,
    string Type, long StartedAtMs, long? EndedAtMs, Guid? SessionId);
public sealed class QueueItem
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public int CaseId { get; set; }
    public string Payload { get; set; } = "";
    public long CreatedAtMs { get; set; }
    public string State { get; set; } = "Pending";
    public int Attempts { get; set; }
    public long NextAttemptAtMs { get; set; }
    public string? LastError { get; set; }
}
public sealed class Activity
{
    public Guid Id { get; set; } // Event ID is also the projection's deduplication key.
    public string TenantId { get; set; } = "";
    public int CaseId { get; set; }
    public string UserId { get; set; } = "";
    public string Type { get; set; } = "";
    public long StartedAtMs { get; set; }
    public long? EndedAtMs { get; set; }
    public Guid? SessionId { get; set; }
    public long? DurationMs => EndedAtMs.HasValue ? EndedAtMs.Value - StartedAtMs : null;
}
public sealed class RequestReceipt
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int StatusCode { get; set; }
    public string ResponseJson { get; set; } = "";
}
public sealed record AddNoteRequest(string? Text);
