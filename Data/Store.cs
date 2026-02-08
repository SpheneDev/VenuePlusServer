using System;
using System.Collections.Concurrent;

namespace VenuePlus.Server;

public static class Store
{
    public static readonly ConcurrentDictionary<string, VipEntry> VipEntries = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ClubVipKeys = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, DjEntry> DjEntries = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, ShiftEntry> ShiftEntries = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, EventEntry> EventEntries = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, StaffUserInfo> StaffUsers = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string> StaffSessions = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, DateTimeOffset> StaffSessionExpiry = new();
    public static readonly ConcurrentDictionary<string, Rights> JobRights = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string> CreatedClubs = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ClubUserJobs = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string> ClubAccessKeysByClub = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string> ClubAccessKeysByKey = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string?> ClubLogos = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, string> ClubJoinPasswordHashes = new(StringComparer.Ordinal);
    public static bool MaintenanceMode;
    public static bool MaintenanceModePendingEnable;

    public static string[] GetJobsForUser(string clubId, string username)
    {
        var key = clubId + "|" + username;
        if (!ClubUserJobs.TryGetValue(key, out var jobs) || jobs.IsEmpty) return new[] { "Unassigned" };
        var list = new string[jobs.Count];
        int i = 0;
        foreach (var name in jobs.Keys)
        {
            list[i] = name;
            i++;
        }
        Array.Sort(list, StringComparer.Ordinal);
        return list;
    }

    public static void SetJobsForUser(string clubId, string username, System.Collections.Generic.IEnumerable<string> jobs)
    {
        var key = clubId + "|" + username;
        var set = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job)) continue;
            set[job] = 1;
        }
        if (set.IsEmpty) set["Unassigned"] = 1;
        ClubUserJobs[key] = set;
    }
}
