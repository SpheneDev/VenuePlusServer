using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VenuePlus.Server;

public static class Persistence
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string DataFilePath => Environment.GetEnvironmentVariable("VENUEPLUS_DATA_FILE") ?? Environment.GetEnvironmentVariable("VENUEPLUS_DATA_FILE") ?? System.IO.Path.Combine(AppContext.BaseDirectory, "venueplus-data.json");

    public static void Load()
    {
        try
        {
            if (!System.IO.File.Exists(DataFilePath)) return;
            var json = System.IO.File.ReadAllText(DataFilePath);
            var state = JsonSerializer.Deserialize<ServerState>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? new ServerState();
            Store.VipEntries.Clear();
            foreach (var e in state.VipEntries) Store.VipEntries[e.Key] = e;
            Store.StaffUsers.Clear();
            foreach (var u in state.StaffUsers) Store.StaffUsers[u.Username] = u;
            Store.JobRights.Clear();
            foreach (var kv in state.JobRights) Store.JobRights[kv.Key] = kv.Value;
            Store.ClubUserJobs.Clear();
            bool loadedJobs = false;
            if (state.ClubUserJobs.Count > 0)
            {
                foreach (var kv in state.ClubUserJobs)
                {
                    var sep = kv.Key.IndexOf('|', StringComparison.Ordinal);
                    if (sep <= 0 || sep >= kv.Key.Length - 1) continue;
                    var clubId = kv.Key.Substring(0, sep);
                    var username = kv.Key.Substring(sep + 1);
                    Store.SetJobsForUser(clubId, username, kv.Value);
                }
                loadedJobs = true;
            }
            if (!loadedJobs)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("clubUserJobs", out var jobsEl) && jobsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in jobsEl.EnumerateObject())
                    {
                        var key = prop.Name;
                        var sep = key.IndexOf('|', StringComparison.Ordinal);
                        if (sep <= 0 || sep >= key.Length - 1) continue;
                        var clubId = key.Substring(0, sep);
                        var username = key.Substring(sep + 1);
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            var list = new System.Collections.Generic.List<string>();
                            foreach (var item in prop.Value.EnumerateArray())
                            {
                                var job = item.GetString() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(job)) list.Add(job);
                            }
                            Store.SetJobsForUser(clubId, username, list);
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var job = prop.Value.GetString() ?? "Unassigned";
                            Store.SetJobsForUser(clubId, username, new[] { job });
                        }
                    }
                }
            }
            Store.ClubAccessKeysByClub.Clear();
            foreach (var kv in state.ClubAccessKeysByClub) { Store.ClubAccessKeysByClub[kv.Key] = kv.Value; Store.ClubAccessKeysByKey[kv.Value] = kv.Key; }
            Store.ClubLogos.Clear();
            foreach (var kv in state.ClubLogos) Store.ClubLogos[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("Persistence.Load failed: " + ex.Message);
        }
    }

    public static async Task SaveAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var clubUserJobs = new System.Collections.Generic.Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var kv in Store.ClubUserJobs)
            {
                if (kv.Value.IsEmpty) { clubUserJobs[kv.Key] = new[] { "Unassigned" }; continue; }
                var arr = new string[kv.Value.Count];
                int i = 0;
                foreach (var name in kv.Value.Keys)
                {
                    arr[i] = name;
                    i++;
                }
                Array.Sort(arr, StringComparer.Ordinal);
                clubUserJobs[kv.Key] = arr;
            }
            var state = new ServerState
            {
                VipEntries = Store.VipEntries.Values.OrderBy(e => e.CharacterName, StringComparer.Ordinal).ToArray(),
                StaffUsers = Store.StaffUsers.Values.OrderBy(u => u.Username, StringComparer.Ordinal).ToArray(),
                JobRights = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value),
                ClubUserJobs = clubUserJobs,
                ClubAccessKeysByClub = Store.ClubAccessKeysByClub.ToDictionary(kv => kv.Key, kv => kv.Value),
                ClubLogos = Store.ClubLogos.ToDictionary(kv => kv.Key, kv => kv.Value)
            };
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false });
            System.IO.File.WriteAllText(DataFilePath, json);
        }
        catch (Exception ex)
        {
            Trace.WriteLine("Persistence.Save failed: " + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }
}
