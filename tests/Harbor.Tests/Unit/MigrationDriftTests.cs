using Harbor.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Harbor.Tests.Unit;

/// <summary>
/// Guards the gap between how the schema is built in tests and how it is built
/// in production.
///
/// <see cref="Integration.HarborApiFactory"/> calls EnsureCreated(), which
/// materializes the schema straight from the current model — so a model change
/// with no matching migration still gives a green suite. Program.cs calls
/// Migrate(), which replays the migration history instead. A model/migration
/// mismatch therefore fails nowhere until the real app meets an existing
/// harbor.db and a column the entity expects is simply not there. That has
/// already happened once, in Sprint 08.
///
/// These tests close that gap by checking the two definitions of the schema
/// against each other directly.
/// </summary>
public class MigrationDriftTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<HarborDbContext> _options;

    public MigrationDriftTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<HarborDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The model and the migrations must describe the same schema.
    ///
    /// This is the test that turns "I forgot to add a migration" from a
    /// production incident into a red build: the model snapshot the last
    /// migration left behind is diffed against the model the code defines now,
    /// and any difference at all is drift.
    /// </summary>
    [Fact]
    public void Model_HasNoPendingChanges_MissingFromMigrations()
    {
        using var db = new HarborDbContext(_options);

        var differences = ModelDifferences(db);

        Assert.True(
            differences.Count == 0,
            "The EF model has changes that no migration captures. EnsureCreated() hides this in "
            + "tests, but Program.cs runs Migrate(), so the real app would run against a schema "
            + "missing these operations. Add a migration:\n"
            + $"  dotnet ef migrations add <Name> --project src/Harbor.Infrastructure --startup-project src/Harbor.Api\n"
            + $"Pending operations: {Describe(differences)}");
    }

    /// <summary>
    /// Replaying every migration must produce the schema the model expects.
    ///
    /// The check above compares the model to the snapshot; this one compares
    /// the model to a database actually built by running the migrations, which
    /// is what production does. A migration that was hand-edited into
    /// disagreeing with its own snapshot is caught here and nowhere else.
    /// </summary>
    [Fact]
    public void Migrations_BuildTheSchema_TheModelExpects()
    {
        using var db = new HarborDbContext(_options);

        db.Database.Migrate();

        // Every entity has to be queryable against the migrated schema; a
        // missing column surfaces here as a SqliteException, exactly as it
        // would against a real harbor.db.
        Assert.Empty(db.Workspaces);
        Assert.Empty(db.Inboxes);
        Assert.Empty(db.Contacts);
        Assert.Empty(db.Teammates);
        Assert.Empty(db.Teams);
        Assert.Empty(db.TeamMemberships);
        Assert.Empty(db.Conversations);
        Assert.Empty(db.Messages);
        Assert.Empty(db.Tags);
        Assert.Empty(db.ConversationTags);
        Assert.Empty(db.CannedReplies);
        Assert.Empty(db.AssignmentEvents);
        Assert.Empty(db.SlaPolicies);
        Assert.Empty(db.SlaBreachEvents);
        Assert.Empty(db.WebhookSubscriptions);
        Assert.Empty(db.WebhookDeliveries);
        Assert.Empty(db.ArticleCollections);
        Assert.Empty(db.Articles);
        Assert.Empty(db.Segments);
    }

    /// <summary>
    /// The seeder must work against a migrated database, not just an
    /// EnsureCreated one — it is what Program.cs runs immediately after
    /// Migrate(), so a schema gap breaks startup itself.
    /// </summary>
    [Fact]
    public void Seeder_Runs_AgainstAMigratedDatabase()
    {
        using var db = new HarborDbContext(_options);
        db.Database.Migrate();

        DataSeeder.Seed(db);

        Assert.Equal(1, db.Workspaces.Count());
        Assert.Equal(4, db.Conversations.Count());
    }

    /// <summary>
    /// Migration history has to be linear and complete: every migration in the
    /// assembly is applied by Migrate(), leaving nothing pending.
    /// </summary>
    [Fact]
    public void EveryMigration_Applies_AndNothingRemainsPending()
    {
        using var db = new HarborDbContext(_options);
        var all = db.Database.GetMigrations().ToList();

        db.Database.Migrate();

        Assert.NotEmpty(all);
        Assert.Equal(all, db.Database.GetAppliedMigrations());
        Assert.Empty(db.Database.GetPendingMigrations());
    }

    /// <summary>
    /// The operations needed to bring the last migration's snapshot up to the
    /// current model. Empty means model and migrations agree.
    /// </summary>
    private static IReadOnlyList<MigrationOperation> ModelDifferences(HarborDbContext db)
    {
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        // The snapshot's model is design-time and unfinalized; it has to be run
        // through the same initialization the live model gets before the two
        // are comparable.
        var snapshotModel = snapshot.Model;
        if (snapshotModel is IMutableModel mutable)
        {
            snapshotModel = mutable.FinalizeModel();
        }

        snapshotModel = db.GetService<IModelRuntimeInitializer>()
            .Initialize(snapshotModel, designTime: true, validationLogger: null);

        return db.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshotModel.GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());
    }

    /// <summary>Names the pending operations so a failure says what drifted.</summary>
    private static string Describe(IReadOnlyList<MigrationOperation> operations) =>
        string.Join(", ", operations.Select(o => o switch
        {
            AddColumnOperation c => $"AddColumn {c.Table}.{c.Name}",
            DropColumnOperation c => $"DropColumn {c.Table}.{c.Name}",
            AlterColumnOperation c => $"AlterColumn {c.Table}.{c.Name}",
            CreateTableOperation t => $"CreateTable {t.Name}",
            DropTableOperation t => $"DropTable {t.Name}",
            CreateIndexOperation i => $"CreateIndex {i.Table}.{i.Name}",
            DropIndexOperation i => $"DropIndex {i.Table}.{i.Name}",
            _ => o.GetType().Name,
        }));
}
