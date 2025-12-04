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
            foreach (var kv in state.ClubUserJobs) Store.ClubUserJobs[kv.Key] = kv.Value;
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
            var state = new ServerState
            {
                VipEntries = Store.VipEntries.Values.OrderBy(e => e.CharacterName, StringComparer.Ordinal).ToArray(),
                StaffUsers = Store.StaffUsers.Values.OrderBy(u => u.Username, StringComparer.Ordinal).ToArray(),
                JobRights = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value),
                ClubUserJobs = Store.ClubUserJobs.ToDictionary(kv => kv.Key, kv => kv.Value),
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
