using INRFS.Financer.Domain;
using INRFS.Financer.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace INRFS.Financer.IntegrationTests;

public sealed class DatabaseInitializationTests
{
    [Fact]
    public async Task Clean_sqlite_database_builds_from_model_and_seeds_access_control()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FinancerDbContext>().UseSqlite(connection).Options;
        await using var db = new FinancerDbContext(options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Initialize"] = "true",
        }).Build();
        var initializer = new DatabaseInitializer(db, new PasswordHasher<UserAccount>(), configuration);

        await initializer.InitializeAsync();

        Assert.True(await db.Database.CanConnectAsync());
        Assert.Contains(await db.Roles.Select(role => role.Name).ToListAsync(), name => name == "SuperAdmin");
        Assert.Contains(await db.Permissions.Select(permission => permission.Name).ToListAsync(), name => name == "loans.disburse");
    }

    [Fact]
    public async Task Existing_database_restores_platform_permissions_for_admin_roles()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FinancerDbContext>().UseSqlite(connection).Options;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Initialize"] = "true",
        }).Build();

        await using (var seedDb = new FinancerDbContext(options))
        {
            var initializer = new DatabaseInitializer(seedDb, new PasswordHasher<UserAccount>(), configuration);
            await initializer.InitializeAsync();
            var affected = await seedDb.RolePermissions
                .Where(grant => grant.Role.Name == "Admin" &&
                    (grant.Permission.Name == "dashboard.read" || grant.Permission.Name == "financers.read" ||
                     grant.Permission.Name == "reports.read" || grant.Permission.Name == "settings.manage"))
                .ExecuteDeleteAsync();
            Assert.Equal(4, affected);
        }

        await using (var upgradeDb = new FinancerDbContext(options))
        {
            var initializer = new DatabaseInitializer(upgradeDb, new PasswordHasher<UserAccount>(), configuration);
            await initializer.InitializeAsync();
        }

        await using var verifyDb = new FinancerDbContext(options);
        var restored = await verifyDb.RolePermissions
            .Where(grant => grant.Role.Name == "Admin" &&
                (grant.Permission.Name == "dashboard.read" || grant.Permission.Name == "financers.read" ||
                 grant.Permission.Name == "reports.read" || grant.Permission.Name == "settings.manage"))
            .Select(grant => grant.Permission.Name)
            .ToListAsync();
        Assert.Equal(["dashboard.read", "financers.read", "reports.read", "settings.manage"], restored.OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task Existing_sqlite_database_adds_missing_profile_image_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE \"Users\" (\"Id\" TEXT NOT NULL PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<FinancerDbContext>().UseSqlite(connection).Options;
        await using var db = new FinancerDbContext(options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Initialize"] = "true",
        }).Build();
        var initializer = new DatabaseInitializer(db, new PasswordHasher<UserAccount>(), configuration);

        // Stop after the compatibility upgrade: the intentionally minimal legacy
        // schema cannot run the normal seed queries.
        await Assert.ThrowsAnyAsync<SqliteException>(() => initializer.InitializeAsync());

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name = 'ProfileImageDataUrl';";
        Assert.Equal(1L, (long)(await verify.ExecuteScalarAsync())!);
    }
}
