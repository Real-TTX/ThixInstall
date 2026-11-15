using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Tests;

/// <summary>A test that needs PostgreSQL: it runs when <c>THIXINSTALL_TEST_DB</c> names a server (a connection string with the right to create databases), else it is skipped.</summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (TestDatabase.Admin is null)
        {
            Skip = "THIXINSTALL_TEST_DB is not set (a PostgreSQL connection string with the right to create databases).";
        }
    }
}

/// <summary>A database of its own for one test, migrated like the real one (so the migrations are tested too), dropped afterwards.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _name = "thixinstall_t_" + Guid.NewGuid().ToString("N")[..12];

    static TestDatabase()
    {
        // the tests never ask GitHub or Homebrew: the background resolver stays off
        Environment.SetEnvironmentVariable("THIXINSTALL_RESOLVE", "off");
    }

    private TestDatabase()
    {
    }

    public static string? Admin => Environment.GetEnvironmentVariable("THIXINSTALL_TEST_DB");

    public string ConnectionString { get; private set; } = string.Empty;

    public AppDbContext Db { get; private set; } = null!;

    public static async Task<TestDatabase> CreateAsync()
    {
        var test = new TestDatabase();
        await using (var admin = new NpgsqlConnection(Admin))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{test._name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        test.ConnectionString = new NpgsqlConnectionStringBuilder(Admin) { Database = test._name, Timeout = 30 }.ConnectionString;
        test.Db = test.NewContext();
        SettingService.Forget();
        DeviceService.Invalidate();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await test.Db.Database.MigrateAsync();
                return test;
            }
            catch (NpgsqlException) when (attempt < 3)
            {
                // a server that has just created the database may refuse the first connection for a moment
                NpgsqlConnection.ClearAllPools();
                await Task.Delay(1000);
            }
        }
    }

    /// <summary>A second context on the same database (another request, another device).</summary>
    public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task<User> AddUserAsync(string name, bool admin = false, string password = "secret-password")
    {
        var user = new User { UserName = name.ToLowerInvariant(), DisplayName = name, IsAdmin = admin };
        user.PasswordHash = Passwords.Hash(user, password);
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    /// <summary>A package with sources for both systems (no network: Chocolatey and a Homebrew formula resolve without a request).</summary>
    public async Task<Package> AddPackageAsync(string key, bool isPublic = true, InstallMethod? windows = InstallMethod.Chocolatey, InstallMethod? mac = null)
    {
        var package = new Package
        {
            Key = key, Name = key.ToUpperInvariant(), Category = "Werkzeuge", IsPublic = isPublic, WindowsMethod = windows, ChocoId = key,
            MacMethod = mac, BrewName = mac is null ? null : key,
        };
        Db.Packages.Add(package);
        await Db.SaveChangesAsync();
        return package;
    }

    public async Task<Script> AddScriptAsync(string key, TargetOs os, bool isPublic = true)
    {
        var script = new Script
        {
            Key = key, Name = key, Category = "System", Os = os, Shell = os == TargetOs.Windows ? ScriptShell.PowerShell : ScriptShell.Zsh,
            Content = "echo hallo", IsPublic = isPublic,
        };
        Db.Scripts.Add(script);
        await Db.SaveChangesAsync();
        return script;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        SettingService.Forget();
        await using var admin = new NpgsqlConnection(Admin);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>Devices and answers of the world for the tests.</summary>
public static class Samples
{
    public static DeviceFacts Windows(string name = "PC-1", string? hardwareId = "HW-1") => new(name, TargetOs.Windows, "10.0.26200", CpuArchitecture.X64, "0.1.5-20261115", hardwareId);

    public static DeviceFacts Mac(string name = "Mac", string? hardwareId = "HW-MAC") => new(name, TargetOs.MacOS, "15.1", CpuArchitecture.Arm64, "0.1.5-20261115", hardwareId);
}

/// <summary>An HTTP handler that answers from a table instead of the internet, and counts the requests.</summary>
public sealed class FakeWorld(Func<HttpRequestMessage, HttpResponseMessage?> answer) : HttpMessageHandler
{
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(answer(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
