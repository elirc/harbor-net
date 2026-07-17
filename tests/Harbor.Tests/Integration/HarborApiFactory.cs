using Harbor.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Harbor.Tests.Integration;

/// <summary>
/// Hosts the API against a shared in-memory SQLite database. The connection
/// stays open for the factory's lifetime so the schema survives between
/// requests.
/// </summary>
public class HarborApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    /// <summary>Captures webhook attempts in place of real HTTP delivery.</summary>
    public FakeWebhookSender WebhookSender { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<HarborDbContext>));
            services.RemoveAll(typeof(HarborDbContext));

            _connection.Open();
            services.AddDbContext<HarborDbContext>(options => options.UseSqlite(_connection));

            services.RemoveAll(typeof(IWebhookSender));
            services.AddSingleton<IWebhookSender>(WebhookSender);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarborDbContext>();
        db.Database.EnsureCreated();

        return host;
    }

    /// <summary>Runs an action against a fresh DbContext scope (arrange/assert helper).</summary>
    public void WithDb(Action<HarborDbContext> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarborDbContext>();
        action(db);
    }

    public void SeedDemoData() => WithDb(DataSeeder.Seed);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
