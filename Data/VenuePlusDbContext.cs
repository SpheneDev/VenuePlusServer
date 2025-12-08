using Microsoft.EntityFrameworkCore;

namespace VenuePlus.Server.Data;

public sealed class VenuePlusDbContext : DbContext
{
    public VenuePlusDbContext(DbContextOptions<VenuePlusDbContext> options) : base(options) { }

    public DbSet<VipEntryEntity> VipEntries => Set<VipEntryEntity>();
    public DbSet<StaffUserEntity> StaffUsers => Set<StaffUserEntity>();
    public DbSet<JobRightEntity> JobRights => Set<JobRightEntity>();
    public DbSet<ClubEntity> Clubs => Set<ClubEntity>();
    public DbSet<BaseUserEntity> BaseUsers => Set<BaseUserEntity>();
    public DbSet<DjEntryEntity> DjEntries => Set<DjEntryEntity>();
    public DbSet<ShiftEntryEntity> Shifts => Set<ShiftEntryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VipEntryEntity>().HasIndex(e => new { e.ClubId, e.CharacterName, e.HomeWorld }).IsUnique();
        modelBuilder.Entity<StaffUserEntity>().HasIndex(e => new { e.ClubId, e.UserUid }).IsUnique();
        modelBuilder.Entity<JobRightEntity>().HasIndex(e => new { e.ClubId, e.JobName }).IsUnique();
        modelBuilder.Entity<ClubEntity>().HasIndex(e => e.ClubId).IsUnique();
        modelBuilder.Entity<ClubEntity>().HasIndex(e => e.AccessKey).IsUnique().HasFilter("\"AccessKey\" IS NOT NULL");
        modelBuilder.Entity<BaseUserEntity>().HasIndex(e => e.Username).IsUnique();
        modelBuilder.Entity<DjEntryEntity>().HasIndex(e => new { e.ClubId, e.DjName }).IsUnique();
        modelBuilder.Entity<ShiftEntryEntity>().HasIndex(e => new { e.ClubId, e.Id }).IsUnique();
    }
}
