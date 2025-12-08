using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VenuePlus.Server.Data;

public sealed class VipEntryEntity
{
    [Key]
    public Guid Id { get; set; }
    [Required]
    [MaxLength(64)]
    public string ClubId { get; set; } = string.Empty;
    [Required]
    [MaxLength(256)]
    public string CharacterName { get; set; } = string.Empty;
    [Required]
    [MaxLength(256)]
    public string HomeWorld { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public int Duration { get; set; }
}

public sealed class StaffUserEntity
{
    [Key]
    public Guid Id { get; set; }
    [Required]
    [MaxLength(64)]
    public string ClubId { get; set; } = string.Empty;
    [Required]
    [MaxLength(15)]
    public string UserUid { get; set; } = string.Empty;
    [Required]
    [MaxLength(64)]
    public string Job { get; set; } = "Unassigned";
    [Required]
    [MaxLength(64)]
    public string Role { get; set; } = "power";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class JobRightEntity
{
    [Key]
    public Guid Id { get; set; }
    [Required]
    [MaxLength(64)]
    public string ClubId { get; set; } = string.Empty;
    [Required]
    [MaxLength(64)]
    public string JobName { get; set; } = string.Empty;
    public bool AddVip { get; set; }
    public bool RemoveVip { get; set; }
    public bool ManageUsers { get; set; }
    public bool ManageJobs { get; set; }
    public bool EditVipDuration { get; set; }
    public bool AddDj { get; set; }
    public bool RemoveDj { get; set; }
    public bool EditShiftPlan { get; set; }
    public int Rank { get; set; } = 1;
    [MaxLength(7)]
    public string ColorHex { get; set; } = "#FFFFFF";
    [MaxLength(64)]
    public string IconKey { get; set; } = "User";
}

 

public sealed class ClubEntity
{
    [Key]
    [MaxLength(64)]
    public string ClubId { get; set; } = string.Empty;
    [Required]
    [MaxLength(256)]
    public string CreatedByUsername { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(64)]
    public string? AccessKey { get; set; }
    [Column(TypeName = "text")]
    public string? LogoBase64 { get; set; }
    [MaxLength(256)]
    public string? JoinPasswordHash { get; set; }
}

public sealed class BaseUserEntity
{
    [Key]
    [MaxLength(15)]
    public string Uid { get; set; } = string.Empty;
    [Required]
    [MaxLength(256)]
    public string Username { get; set; } = string.Empty;
    [Required]
    [MaxLength(256)]
    public string PasswordHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DjEntryEntity
{
    [Key]
    public Guid Id { get; set; }
    [Required]
    [MaxLength(64)]
    public string ClubId { get; set; } = string.Empty;
    [Required]
    [MaxLength(128)]
    public string DjName { get; set; } = string.Empty;
    [MaxLength(256)]
    public string TwitchLink { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ShiftEntryEntity
{
    [Key]
    public Guid Id { get; set; }
    [Required]
    [MaxLength(64)]
    public string ClubId { get; set; } = string.Empty;
    [Required]
    [MaxLength(128)]
    public string Title { get; set; } = string.Empty;
    [MaxLength(15)]
    public string? AssignedUid { get; set; }
    [MaxLength(64)]
    public string? Job { get; set; }
    public DateTimeOffset StartAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset EndAt { get; set; } = DateTimeOffset.UtcNow.AddHours(2);
}
