using ActivityTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ActivityTracker.Api.Data;

public sealed class TrackerDb(DbContextOptions<TrackerDb> options) : DbContext(options)
{
    public DbSet<Case> Cases => Set<Case>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<ViewSession> ViewSessions => Set<ViewSession>();
    public DbSet<QueueItem> Queue => Set<QueueItem>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<RequestReceipt> Receipts => Set<RequestReceipt>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Case>().HasAlternateKey(x => new { x.Id, x.TenantId });
        model.Entity<User>().HasAlternateKey(x => new { x.Id, x.TenantId });
        model.Entity<Document>().HasAlternateKey(x => new { x.Id, x.CaseId, x.TenantId });
        model.Entity<Document>().HasOne<Case>().WithMany().HasForeignKey(x => new { x.CaseId, x.TenantId }).HasPrincipalKey(x => new { x.Id, x.TenantId });
        model.Entity<Note>().HasOne<Case>().WithMany().HasForeignKey(x => new { x.CaseId, x.TenantId }).HasPrincipalKey(x => new { x.Id, x.TenantId });
        model.Entity<Note>().HasOne<User>().WithMany().HasForeignKey(x => new { x.UserId, x.TenantId }).HasPrincipalKey(x => new { x.Id, x.TenantId });
        model.Entity<ViewSession>().HasOne<Document>().WithMany().HasForeignKey(x => new { x.DocumentId, x.CaseId, x.TenantId }).HasPrincipalKey(x => new { x.Id, x.CaseId, x.TenantId });
        model.Entity<ViewSession>().HasOne<User>().WithMany().HasForeignKey(x => new { x.UserId, x.TenantId }).HasPrincipalKey(x => new { x.Id, x.TenantId });
        model.Entity<Activity>().HasOne<Case>().WithMany().HasForeignKey(x => new { x.CaseId, x.TenantId }).HasPrincipalKey(x => new { x.Id, x.TenantId });
        model.Entity<Activity>().HasOne<User>().WithMany().HasForeignKey(x => new { x.UserId, x.TenantId }).HasPrincipalKey(x => new { x.Id, x.TenantId });
        model.Entity<Activity>().HasIndex(x => x.SessionId).IsUnique();
        model.Entity<Activity>().Ignore(x => x.DurationMs);
        model.Entity<QueueItem>().HasOne<Case>().WithMany().HasForeignKey(x => new { x.CaseId, x.TenantId }).HasPrincipalKey(x => new { x.Id, x.TenantId });
        model.Entity<QueueItem>().HasIndex(x => new { x.State, x.NextAttemptAtMs });
        model.Entity<RequestReceipt>().HasIndex(x => new { x.TenantId, x.UserId, x.Key }).IsUnique();
    }

    public async Task InitializeAsync()
    {
        await Database.EnsureCreatedAsync();
        await Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        if (await Cases.AnyAsync()) return;
        Cases.AddRange(new Case { Id = 1, TenantId = "north", Name = "Riverside Estate" }, new Case { Id = 2, TenantId = "south", Name = "Cedar Contract" });
        Users.AddRange(new User { Id = "alice", Name = "Alice Morgan", TenantId = "north" }, new User { Id = "alex", Name = "Alex Chen", TenantId = "north" }, new User { Id = "bob", Name = "Bob Ahmed", TenantId = "south" });
        Documents.AddRange(new Document { Id = 101, CaseId = 1, TenantId = "north", Name = "Estate summary", Content = "Riverside Estate — sample document. Review the ownership summary and executor instructions. This is demonstration data." }, new Document { Id = 201, CaseId = 2, TenantId = "south", Name = "Contract summary", Content = "Cedar Contract — sample document for the second tenant." });
        await SaveChangesAsync();
    }
}
