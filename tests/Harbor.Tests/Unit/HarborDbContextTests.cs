using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Tests.Unit;

public class HarborDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<HarborDbContext> _options;

    public HarborDbContextTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<HarborDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new HarborDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void DateTimeOffset_RoundTrips_ThroughSqlite()
    {
        var createdAt = new DateTimeOffset(2026, 7, 16, 9, 30, 0, TimeSpan.FromHours(2));
        var id = Guid.NewGuid();

        using (var db = new HarborDbContext(_options))
        {
            db.Workspaces.Add(new Workspace { Id = id, Name = "W", CreatedAt = createdAt });
            db.SaveChanges();
        }

        using (var db = new HarborDbContext(_options))
        {
            var loaded = db.Workspaces.Single(w => w.Id == id);
            // Same instant, normalized to UTC.
            Assert.Equal(createdAt.ToUniversalTime(), loaded.CreatedAt);
        }
    }

    [Fact]
    public void OrderBy_DateTimeOffset_IsChronological_AcrossOffsets()
    {
        // Wall-clock order (10:00 < 23:00) disagrees with instant order here:
        // 23:00+09:00 == 14:00 UTC, which is AFTER 10:00 UTC but BEFORE 16:00 UTC.
        var first = new Workspace { Name = "first", CreatedAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero) };
        var second = new Workspace { Name = "second", CreatedAt = new DateTimeOffset(2026, 1, 1, 23, 0, 0, TimeSpan.FromHours(9)) };
        var third = new Workspace { Name = "third", CreatedAt = new DateTimeOffset(2026, 1, 1, 16, 0, 0, TimeSpan.Zero) };

        using (var db = new HarborDbContext(_options))
        {
            db.Workspaces.AddRange(third, first, second);
            db.SaveChanges();
        }

        using (var db = new HarborDbContext(_options))
        {
            var names = db.Workspaces.OrderBy(w => w.CreatedAt).Select(w => w.Name).ToList();
            Assert.Equal(["first", "second", "third"], names);
        }
    }

    [Fact]
    public void Where_DateTimeOffsetComparison_WorksInSqlite()
    {
        var cutoff = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        using (var db = new HarborDbContext(_options))
        {
            db.Workspaces.Add(new Workspace { Name = "old", CreatedAt = cutoff.AddDays(-10) });
            db.Workspaces.Add(new Workspace { Name = "new", CreatedAt = cutoff.AddDays(10) });
            db.SaveChanges();
        }

        using (var db = new HarborDbContext(_options))
        {
            var recent = db.Workspaces.Where(w => w.CreatedAt > cutoff).Select(w => w.Name).ToList();
            Assert.Equal(["new"], recent);
        }
    }

    [Fact]
    public void Seeder_PopulatesDemoWorkspace_AndIsIdempotent()
    {
        using var db = new HarborDbContext(_options);

        DataSeeder.Seed(db);
        DataSeeder.Seed(db);

        Assert.Equal(1, db.Workspaces.Count());
        Assert.Equal(2, db.Inboxes.Count());
        Assert.Equal(3, db.Teammates.Count());
        Assert.Equal(2, db.Teams.Count());
        Assert.Equal(3, db.Contacts.Count());
        Assert.Equal(4, db.Conversations.Count());
        Assert.Equal(3, db.Tags.Count());
        Assert.Equal(2, db.CannedReplies.Count());
        Assert.True(db.Messages.Count() >= 8);
    }

    [Fact]
    public void DuplicateTagName_InSameWorkspace_IsRejected()
    {
        using var db = new HarborDbContext(_options);
        var workspace = new Workspace { Name = "W" };
        db.Workspaces.Add(workspace);
        db.Tags.Add(new Tag { WorkspaceId = workspace.Id, Name = "billing" });
        db.SaveChanges();

        db.Tags.Add(new Tag { WorkspaceId = workspace.Id, Name = "billing" });

        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }
}
