using System;
using System.Collections.Concurrent;

namespace VenuePlus.Server;

public static class Store
{
    public static readonly ConcurrentDictionary<string, VipEntry> VipEntries = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ClubVipKeys = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, DjEntry> DjEntries = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, StaffUserInfo> StaffUsers = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string> StaffSessions = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, DateTimeOffset> StaffSessionExpiry = new();
    public static readonly ConcurrentDictionary<string, Rights> JobRights = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string> CreatedClubs = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string> ClubUserJobs = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string> ClubAccessKeysByClub = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string> ClubAccessKeysByKey = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string?> ClubLogos = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string> ClubJoinPasswordHashes = new(StringComparer.Ordinal);
}
