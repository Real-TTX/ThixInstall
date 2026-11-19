using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ThixInstall.Cloud.Data;

/// <summary>
/// The database (PostgreSQL). Tables are PascalCase and singular, the key is always <c>Id</c> (BIGINT), every record has CreateDate, CreateUserId,
/// UpdateDate and UpdateUserId (filled in <see cref="SaveChangesAsync(bool, CancellationToken)"/> with the person or device of the request), and a
/// record with an <c>UpdateState</c> is deleted softly (0 = deleted, 1 = created, 2 = updated).
/// Only the business layer (<c>ThixInstall.Cloud.Business</c>) uses it.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IHttpContextAccessor? accessor = null) : DbContext(options)
{
    /// <summary>The item of the request that names the acting person (set by the cookie sign-in and by the device token).</summary>
    public const string ActorItem = "ThixInstall.ActorId";

    /// <summary>The one sequence every change of an action takes its revision from.</summary>
    public const string RevisionSequence = "ActionRevision";

    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<Package> Packages => Set<Package>();

    public DbSet<PackageIcon> PackageIcons => Set<PackageIcon>();

    public DbSet<PackageRelease> Releases => Set<PackageRelease>();

    public DbSet<Script> Scripts => Set<Script>();

    public DbSet<Profile> Profiles => Set<Profile>();

    public DbSet<ProfileItem> ProfileItems => Set<ProfileItem>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<DeviceApp> DeviceApps => Set<DeviceApp>();

    public DbSet<DeviceAction> Actions => Set<DeviceAction>();

    public DbSet<Bundle> Bundles => Set<Bundle>();

    public DbSet<BundleItem> BundleItems => Set<BundleItem>();

    public DbSet<CacheFile> CacheFiles => Set<CacheFile>();

    public DbSet<Setting> Settings => Set<Setting>();

    /// <summary>Who changes something outside of a request (tests, background work); a request names its person itself.</summary>
    public long? ActingUserId { get; set; }

    /// <summary>
    /// A record that hangs on a softly deleted one (the icon of a deleted package, the actions of a removed device, the sessions of a deleted account)
    /// is meant to disappear with it: the query filter of the parent hiding it is what we want, so EF's warning about it is off.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasSequence<long>(RevisionSequence).StartsAt(1).IncrementsBy(1);

        b.Entity<User>(e =>
        {
            e.ToTable("User");
            e.Property(x => x.UserName).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.PasswordHash).HasMaxLength(200);
            e.Property(x => x.Theme).HasMaxLength(10);
            e.HasIndex(x => x.UserName).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("UserSession");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.Property(x => x.IpAddress).HasMaxLength(64);
        });

        b.Entity<Package>(e =>
        {
            e.ToTable("Package");
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Publisher).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Category).HasMaxLength(60);
            e.Property(x => x.Homepage).HasMaxLength(500);
            e.Property(x => x.WindowsMethod).HasConversion<int?>();
            e.Property(x => x.MacMethod).HasConversion<int?>();
            e.Property(x => x.WindowsRoute).HasConversion<int?>();
            e.Property(x => x.MacRoute).HasConversion<int?>();
            e.Property(x => x.WingetId).HasMaxLength(200);
            e.Property(x => x.ChocoId).HasMaxLength(200);
            e.Property(x => x.StoreId).HasMaxLength(40);
            e.Property(x => x.StorePackageFamilyName).HasMaxLength(200);
            e.Property(x => x.WindowsUploadSha256).HasMaxLength(64);
            e.Property(x => x.WindowsUploadFileName).HasMaxLength(260);
            e.Property(x => x.DirectUrl).HasMaxLength(1000);
            e.Property(x => x.DirectSilentArguments).HasMaxLength(500);
            e.Property(x => x.DirectSha256).HasMaxLength(64);
            e.Property(x => x.DirectVersion).HasMaxLength(60);
            e.Property(x => x.DetectDisplayName).HasMaxLength(200);
            e.Property(x => x.BrewName).HasMaxLength(200);
            e.Property(x => x.MacDirectUrl).HasMaxLength(1000);
            e.Property(x => x.MacDirectSha256).HasMaxLength(64);
            e.Property(x => x.MacDirectVersion).HasMaxLength(60);
            e.Property(x => x.MacUploadSha256).HasMaxLength(64);
            e.Property(x => x.MacUploadFileName).HasMaxLength(260);
            e.Property(x => x.MacAppBundleName).HasMaxLength(200);
            e.Property(x => x.ThixRepository).HasMaxLength(200);
            e.Property(x => x.ThixManifest).HasMaxLength(200);
            e.Property(x => x.InstallerOptions).HasMaxLength(8000);
            e.HasIndex(x => x.Key).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<PackageIcon>(e =>
        {
            e.ToTable("PackageIcon");
            e.HasIndex(x => x.PackageId).IsUnique();
            e.HasOne(x => x.Package).WithOne(x => x.Icon).HasForeignKey<PackageIcon>(x => x.PackageId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ContentType).HasMaxLength(60);
        });

        b.Entity<PackageRelease>(e =>
        {
            e.ToTable("PackageRelease");
            e.HasIndex(x => new { x.PackageId, x.Os, x.Architecture }).IsUnique();
            e.HasOne(x => x.Package).WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Os).HasConversion<int>();
            e.Property(x => x.Architecture).HasConversion<int>();
            e.Property(x => x.Method).HasConversion<int>();
            e.Property(x => x.InstallerKind).HasConversion<int?>();
            e.Property(x => x.Version).HasMaxLength(100);
            e.Property(x => x.InstallerUrl).HasMaxLength(2000);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.SilentArguments).HasMaxLength(500);
            e.Property(x => x.ProductCode).HasMaxLength(100);
            e.Property(x => x.AppBundleName).HasMaxLength(200);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.Property(x => x.Note).HasMaxLength(500);
        });

        b.Entity<Script>(e =>
        {
            e.ToTable("Script");
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Category).HasMaxLength(60);
            e.Property(x => x.Os).HasConversion<int>();
            e.Property(x => x.Shell).HasConversion<int>();
            e.Property(x => x.Context).HasConversion<int>();
            e.HasIndex(x => x.Key).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<Profile>(e =>
        {
            e.ToTable("Profile");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => x.EnrollmentKey).IsUnique();
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<ProfileItem>(e =>
        {
            e.ToTable("ProfileItem");
            e.HasIndex(x => x.ProfileId);
            e.HasOne<Profile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Package).WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Script).WithMany().HasForeignKey(x => x.ScriptId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("Device");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.HardwareId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Os).HasConversion<int>();
            e.Property(x => x.Architecture).HasConversion<int>();
            e.Property(x => x.OsVersion).HasMaxLength(100);
            e.Property(x => x.AgentVersion).HasMaxLength(60);
            e.Property(x => x.HardwareId).HasMaxLength(100);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<DeviceApp>(e =>
        {
            e.ToTable("DeviceApp");
            e.HasIndex(x => x.DeviceId);
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Version).HasMaxLength(100);
            e.Property(x => x.Publisher).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(20);
        });

        b.Entity<DeviceAction>(e =>
        {
            e.ToTable("DeviceAction");
            e.HasIndex(x => new { x.DeviceId, x.Revision });
            e.HasIndex(x => new { x.DeviceId, x.State });
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Package).WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Script).WithMany().HasForeignKey(x => x.ScriptId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.State).HasConversion<int>();
            e.Property(x => x.Origin).HasConversion<int>();
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.InstalledVersion).HasMaxLength(100);
        });

        b.Entity<Bundle>(e =>
        {
            e.ToTable("Bundle");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.OwnerUserId);
            e.HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Os).HasConversion<int>();
        });

        b.Entity<BundleItem>(e =>
        {
            e.ToTable("BundleItem");
            e.Property(x => x.InstallChoices).HasMaxLength(2000);
            e.HasIndex(x => x.BundleId);
            e.HasOne<Bundle>().WithMany().HasForeignKey(x => x.BundleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Package).WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Script).WithMany().HasForeignKey(x => x.ScriptId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CacheFile>(e =>
        {
            e.ToTable("CacheFile");
            e.HasIndex(x => x.SourceUrl);
            e.HasIndex(x => x.Sha256);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.SourceUrl).HasMaxLength(2000);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(100);
        });

        b.Entity<Setting>(e =>
        {
            e.ToTable("Setting");
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(100);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills the audit columns, and turns the delete of a soft record into UpdateState 0.</summary>
    private void Stamp()
    {
        var now = DateTime.UtcNow;
        var actor = ActingUserId ?? accessor?.HttpContext?.Items[ActorItem] as long?;
        foreach (var entry in ChangeTracker.Entries<Entity>().ToList())
        {
            var entity = entry.Entity;
            if (entry.State == EntityState.Deleted && entity is SoftEntity soft)
            {
                entry.State = EntityState.Modified;
                soft.UpdateState = SoftEntity.Deleted;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entity.CreateDate == default)
                    {
                        entity.CreateDate = now;
                    }

                    entity.CreateUserId ??= actor;
                    entity.UpdateDate = now;
                    entity.UpdateUserId = actor ?? entity.CreateUserId;
                    break;
                case EntityState.Modified:
                    entity.UpdateDate = now;
                    entity.UpdateUserId = actor ?? entity.UpdateUserId;
                    if (entity is SoftEntity { UpdateState: not SoftEntity.Deleted } updated)
                    {
                        updated.UpdateState = SoftEntity.Updated;
                    }

                    break;
            }
        }
    }
}

/// <summary>For <c>dotnet ef migrations add</c>: no database is needed to create a migration.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Port=4957;Database=thixinstall;Username=thixinstall;Password=thixinstall").Options);
}
