using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TaRegistry.Web.Data;

public sealed class AdminUser : IdentityUser;

public sealed class RegistryDb(DbContextOptions<RegistryDb> options) : IdentityDbContext<AdminUser>(options)
{
    public DbSet<PackageEntity> Packages => Set<PackageEntity>();
    public DbSet<DownloadEntity> Downloads => Set<DownloadEntity>();
    public DbSet<IssuedKey> IssuedKeys => Set<IssuedKey>();
    public DbSet<ChallengeEntity> Challenges => Set<ChallengeEntity>();
    public DbSet<CatalogueState> CatalogueStates => Set<CatalogueState>();
    public DbSet<RegistrySettings> Settings => Set<RegistrySettings>();
    public DbSet<SigningKeyEntity> SigningKeys => Set<SigningKeyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PackageEntity>(entity =>
        {
            entity.HasIndex(p => new { p.Kind, p.PackageId }).IsUnique();
        });

        modelBuilder.Entity<DownloadEntity>(entity =>
        {
            entity.HasKey(d => d.DownloadId);
            entity.HasIndex(d => d.Token).IsUnique();
            entity.HasIndex(d => new { d.InstallId, d.Kind, d.PackageId, d.Release, d.Sha256 });
        });

        modelBuilder.Entity<IssuedKey>(entity =>
        {
            entity.HasIndex(k => new { k.InstallId, k.IssuedAt });
        });

        modelBuilder.Entity<ChallengeEntity>(entity =>
        {
            entity.HasKey(c => c.Code);
            entity.HasIndex(c => new { c.InstallId, c.State, c.CreatedAt });
        });

        modelBuilder.Entity<SigningKeyEntity>(entity =>
        {
            entity.HasKey(k => k.KeyId);
        });
    }
}

public sealed class PackageEntity
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Release { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Version { get; set; }
    public int? Revision { get; set; }
    public string? Summary { get; set; }
    public string? Author { get; set; }
    public string? Publisher { get; set; }
    public string? Homepage { get; set; }
    public string? Notes { get; set; }
    public string? TagsJson { get; set; }
    public string? PublishedDay { get; set; }
    public string? BadgeSha { get; set; }
    public string? DetailsJson { get; set; }
    public bool InCatalogue { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class DownloadEntity
{
    public string DownloadId { get; set; } = "";
    public string InstallId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string PackageId { get; set; } = "";
    public int Release { get; set; }
    public string Sha256 { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public int KeyCount { get; set; }
    public string? Result { get; set; }
    public long? ResultBytes { get; set; }
    public int? ResultSeconds { get; set; }
    public DateTime? ResultAt { get; set; }
}

public sealed class IssuedKey
{
    public long Id { get; set; }
    public string InstallId { get; set; } = "";
    public string DownloadId { get; set; } = "";
    public DateTime IssuedAt { get; set; }
}

public sealed class ChallengeEntity
{
    public string Code { get; set; } = "";
    public string InstallId { get; set; } = "";
    public string State { get; set; } = "pending";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class CatalogueState
{
    public int Id { get; set; } = 1;
    public long Sequence { get; set; }
    public bool Dirty { get; set; }
    public string? Generated { get; set; }
    public string? Expires { get; set; }
    public byte[]? Json { get; set; }
    public string? Signature { get; set; }
    public string? SignedByKeyId { get; set; }
}

public sealed class RegistrySettings
{
    public int Id { get; set; } = 1;
    public string RegistryId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? Homepage { get; set; }
    public string BaseUrl { get; set; } = "";
    public bool InstallIdRequired { get; set; } = true;
    public int CatalogueExpiryDays { get; set; } = 30;
    public int DownloadUrlTtlSeconds { get; set; } = 600;
    public int KeysPerHour { get; set; } = 30;
    public int ChallengeAfter { get; set; } = 10;
}

public sealed class SigningKeyEntity
{
    public string KeyId { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public bool IncludeInCatalogue { get; set; } = true;
    public bool IsSigner { get; set; }
    public DateTime CreatedAt { get; set; }
}
