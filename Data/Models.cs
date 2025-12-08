using System;

namespace VenuePlus.Server;

public sealed class VipEntry
{
    public string CharacterName { get; set; } = string.Empty;
    public string HomeWorld { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public int Duration { get; set; } = 0;
    public string Key => CharacterName + "@" + HomeWorld;
}

public sealed class DjEntry
{
    public string DjName { get; set; } = string.Empty;
    public string TwitchLink { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ShiftEntry
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? AssignedUid { get; set; }
    public string? Job { get; set; }
    public DateTimeOffset StartAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset EndAt { get; set; } = DateTimeOffset.UtcNow.AddHours(2);
}

public sealed class StaffUser
{
    public string Username { get; set; } = string.Empty;
    public DateTimeOffset? CreatedAt { get; set; }
    public string Job { get; set; } = "Unassigned";
    public string Role { get; set; } = "power";
    public string Uid { get; set; } = string.Empty;
}

public sealed class StaffUserInfo
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Job { get; set; } = "Unassigned";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Role { get; set; } = "power";
    public string Uid { get; set; } = string.Empty;
}

public sealed class Rights
{
    public bool AddVip { get; set; }
    public bool RemoveVip { get; set; }
    public bool ManageUsers { get; set; }
    public bool ManageJobs { get; set; }
    public bool EditVipDuration { get; set; }
    public bool AddDj { get; set; }
    public bool RemoveDj { get; set; }
    public bool EditShiftPlan { get; set; }
    public int Rank { get; set; } = 1;
    public string ColorHex { get; set; } = "#FFFFFF";
    public string IconKey { get; set; } = "User";
}

public sealed class UpdatePayload
{
    public string Op { get; set; } = string.Empty;
    public VipEntry? Entry { get; set; }
    public string? Token { get; set; }
}

public sealed class ServerState
{
    public VipEntry[] VipEntries { get; set; } = Array.Empty<VipEntry>();
    public StaffUserInfo[] StaffUsers { get; set; } = Array.Empty<StaffUserInfo>();
    public System.Collections.Generic.Dictionary<string, Rights> JobRights { get; set; } = new System.Collections.Generic.Dictionary<string, Rights>(System.StringComparer.Ordinal);
    public System.Collections.Generic.Dictionary<string, string> ClubUserJobs { get; set; } = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
    public System.Collections.Generic.Dictionary<string, string> ClubAccessKeysByClub { get; set; } = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
    public System.Collections.Generic.Dictionary<string, string?> ClubLogos { get; set; } = new System.Collections.Generic.Dictionary<string, string?>(System.StringComparer.Ordinal);
}
