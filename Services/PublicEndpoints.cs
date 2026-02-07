using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace VenuePlus.Server;

public static class PublicEndpoints
{
    private static string[] EnsureJobs(string[] jobs)
    {
        if (jobs.Length == 0) return new[] { "Unassigned" };
        return jobs;
    }

    private static bool HasOwner(string[] jobs)
    {
        for (int i = 0; i < jobs.Length; i++)
        {
            if (string.Equals(jobs[i], "Owner", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void SplitNameAndHomeWorld(string? input, out string name, out string homeWorld)
    {
        name = string.Empty;
        homeWorld = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return;
        var at = input.LastIndexOf('@');
        if (at <= 0 || at >= input.Length - 1)
        {
            name = input;
            return;
        }
        name = input.Substring(0, at);
        homeWorld = input.Substring(at + 1);
    }

    private static string GetPrimaryJob(Dictionary<string, Rights> rightsMap, string[] jobs)
    {
        if (HasOwner(jobs)) return "Owner";
        string best = "Unassigned";
        int bestRank = 0;
        for (int i = 0; i < jobs.Length; i++)
        {
            if (!rightsMap.TryGetValue(jobs[i], out var r)) continue;
            if (r.Rank > bestRank)
            {
                bestRank = r.Rank;
                best = jobs[i];
            }
        }
        return best;
    }

    public static void Map(WebApplication app, string? conn)
    {
        app.MapGet("/", () => Results.Ok(new { ok = true, time = DateTimeOffset.UtcNow })).RequireCors("PublicJson");
        app.MapGet("/health", async (HttpContext ctx) =>
        {
            try
            {
                bool dbOk = false;
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    using var scopeH = app.Services.CreateScope();
                    var db = scopeH.ServiceProvider.GetRequiredService<VenuePlus.Server.Data.VenuePlusDbContext>();
                    dbOk = await db.Database.CanConnectAsync();
                }
                return Results.Ok(new { ok = true, dbOk, maintenanceMode = Store.MaintenanceMode, time = DateTimeOffset.UtcNow });
            }
            catch (Exception ex)
            {
                app.Logger.LogDebug($"Health db error: {ex.Message}");
                return Results.Ok(new { ok = true, dbOk = false, maintenanceMode = Store.MaintenanceMode, time = DateTimeOffset.UtcNow });
            }
        }).RequireCors("PublicJson");

        app.MapGet("/{accessKey}/viplist.json", async (string accessKey, HttpContext ctx) =>
        {
            app.Logger.LogDebug($"Public VIP GET ip={ctx.Connection.RemoteIpAddress} ak={accessKey}");
            try
            {
                if (string.IsNullOrWhiteSpace(accessKey)) { app.Logger.LogDebug("Public VIP missing accessKey"); return Results.Json(Array.Empty<VipEntry>()); }
                string? clubId = null;
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    using var scope = app.Services.CreateScope();
                    var ef = scope.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                    clubId = await ef.GetClubIdByAccessKeyAsync(accessKey);
                }
                else
                {
                    clubId = Store.ClubAccessKeysByKey.TryGetValue(accessKey, out var c) ? c : null;
                }
                if (string.IsNullOrWhiteSpace(clubId)) { app.Logger.LogDebug("Public VIP no club for accessKey"); return Results.Json(Array.Empty<VipEntry>()); }
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    using var scope2 = app.Services.CreateScope();
                    var ef2 = scope2.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                    var entries = await ef2.LoadVipEntriesAsync(clubId!) ?? Array.Empty<VipEntry>();
                    var res = entries.OrderBy(e => e.CharacterName, StringComparer.Ordinal).ToArray();
                    app.Logger.LogDebug($"Public VIP ok club={clubId} count={res.Length}");
                    return Results.Json(res);
                }
                else
                {
                    string[] keys;
                    if (Store.ClubVipKeys.TryGetValue(clubId!, out var s)) keys = s.Keys.ToArray(); else keys = Array.Empty<string>();
                    var list = keys.Select(k => Store.VipEntries.TryGetValue(k, out var e) ? e : null).Where(e => e != null).Select(e => e!).OrderBy(e => e.CharacterName, StringComparer.Ordinal).ToArray();
                    app.Logger.LogDebug($"Public VIP ok mem club={clubId} count={list.Length}");
                    return Results.Json(list);
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogDebug($"Public VIP error ak={accessKey}: {ex.Message}");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireCors("PublicJson");

        app.MapGet("/{accessKey}/stafflist.json", async (string accessKey, HttpContext ctx) =>
        {
            app.Logger.LogDebug($"Public Staff GET ip={ctx.Connection.RemoteIpAddress} ak={accessKey}");
            try
            {
                if (string.IsNullOrWhiteSpace(accessKey)) { app.Logger.LogDebug("Public Staff missing accessKey"); return Results.Json(Array.Empty<StaffUser>()); }
                string? clubId = null;
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    using var scope = app.Services.CreateScope();
                    var ef = scope.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                    clubId = await ef.GetClubIdByAccessKeyAsync(accessKey);
                }
                else
                {
                    clubId = Store.ClubAccessKeysByKey.TryGetValue(accessKey, out var c) ? c : null;
                }
                if (string.IsNullOrWhiteSpace(clubId)) { app.Logger.LogDebug("Public Staff no club for accessKey"); return Results.Json(Array.Empty<StaffUser>()); }
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    using var scope2 = app.Services.CreateScope();
                    var ef2 = scope2.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                    var list = await ef2.GetStaffUsersAsync(clubId!) ?? Array.Empty<StaffUserInfo>();
                    var rights = await ef2.GetJobRightsAsync(clubId!);
                    var users = list.OrderBy(u => u.Username, StringComparer.Ordinal).Select(u =>
                    {
                        var jobs = EnsureJobs(u.Jobs);
                        SplitNameAndHomeWorld(u.Username, out var n1, out var w1);
                        return new { Username = n1, Homeworld = w1, Jobs = jobs, CreatedAt = u.CreatedAt };
                    }).ToArray();
                    app.Logger.LogDebug($"Public Staff ok club={clubId} count={users.Length}");
                    return Results.Json(users);
                }
                else
                {
                    var usersClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubId + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubId!.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                    var rights = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                    var users = usersClub.Select(u =>
                    {
                        var jobs = EnsureJobs(Store.GetJobsForUser(clubId!, u));
                        SplitNameAndHomeWorld(u, out var n1, out var w1);
                        return new
                        {
                            Username = n1,
                            Homeworld = w1,
                            Jobs = jobs,
                            CreatedAt = (Store.StaffUsers.TryGetValue(u, out var info2) ? info2.CreatedAt : DateTimeOffset.UtcNow)
                        };
                    }).ToArray();
                    app.Logger.LogDebug($"Public Staff ok mem club={clubId} count={users.Length}");
                    return Results.Json(users);
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogDebug($"Public Staff error ak={accessKey}: {ex.Message}");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireCors("PublicJson");

        app.MapGet("/{accessKey}/djlist.json", async (string accessKey, HttpContext ctx) =>
        {
            app.Logger.LogDebug($"Public DJ GET ip={ctx.Connection.RemoteIpAddress} ak={accessKey}");
            try
            {
                if (string.IsNullOrWhiteSpace(accessKey)) { app.Logger.LogDebug("Public DJ missing accessKey"); return Results.Json(Array.Empty<DjEntry>()); }
                string? clubId = null;
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    using var scope = app.Services.CreateScope();
                    var ef = scope.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                    clubId = await ef.GetClubIdByAccessKeyAsync(accessKey);
                }
                else
                {
                    clubId = Store.ClubAccessKeysByKey.TryGetValue(accessKey, out var c) ? c : null;
                }
                if (string.IsNullOrWhiteSpace(clubId)) { app.Logger.LogDebug("Public DJ no club for accessKey"); return Results.Json(Array.Empty<DjEntry>()); }
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    using var scope2 = app.Services.CreateScope();
                    var ef2 = scope2.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                    var list = await ef2.LoadDjEntriesAsync(clubId!) ?? Array.Empty<DjEntry>();
                    var res = list.OrderBy(e => e.DjName, StringComparer.Ordinal).ToArray();
                    app.Logger.LogDebug($"Public DJ ok club={clubId} count={res.Length}");
                    return Results.Json(res);
                }
                else
                {
                    var keys = Store.DjEntries.Keys.Where(k => k.StartsWith(clubId + "|", StringComparison.Ordinal)).ToArray();
                    var list = keys.Select(k => Store.DjEntries.TryGetValue(k, out var e) ? e : null).Where(e => e != null).Select(e => e!).OrderBy(e => e.DjName, StringComparer.Ordinal).ToArray();
                    app.Logger.LogDebug($"Public DJ ok mem club={clubId} count={list.Length}");
                    return Results.Json(list);
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogDebug($"Public DJ error ak={accessKey}: {ex.Message}");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireCors("PublicJson");

        app.MapGet("/{accessKey}/staffshifts.json", async (string accessKey, HttpContext ctx) =>
        {
            app.Logger.LogDebug($"Public Staff Shifts GET ip={ctx.Connection.RemoteIpAddress} ak={accessKey}");
            try
            {
                if (string.IsNullOrWhiteSpace(accessKey)) { app.Logger.LogDebug("Public Staff Shifts missing accessKey"); return Results.Json(Array.Empty<ShiftEntry>()); }
                string? clubId = null;
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    using var scope = app.Services.CreateScope();
                    var ef = scope.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                    clubId = await ef.GetClubIdByAccessKeyAsync(accessKey);
                }
                else
                {
                    clubId = Store.ClubAccessKeysByKey.TryGetValue(accessKey, out var c) ? c : null;
                }
                if (string.IsNullOrWhiteSpace(clubId)) { app.Logger.LogDebug("Public Staff Shifts no club for accessKey"); return Results.Json(Array.Empty<ShiftEntry>()); }
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    using var scope2 = app.Services.CreateScope();
                    var ef2 = scope2.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                    var list = await ef2.LoadShiftEntriesAsync(clubId!) ?? Array.Empty<ShiftEntry>();
                    var staff = await ef2.GetStaffUsersAsync(clubId!) ?? Array.Empty<StaffUserInfo>();
                    var nameByUid = staff.ToDictionary(s => s.Uid, s => s.Username, StringComparer.Ordinal);
                    var res = list.Where(e => !string.IsNullOrWhiteSpace(e.AssignedUid)).OrderBy(e => e.StartAt).Select(e =>
                    {
                        var uname = string.Empty;
                        if (!string.IsNullOrWhiteSpace(e.AssignedUid) && nameByUid.TryGetValue(e.AssignedUid, out var n)) uname = n;
                        SplitNameAndHomeWorld(uname, out var n1, out var w1);
                        return new { Title = e.Title, StaffName = n1, StaffHomeWorld = w1, Job = e.Job, StartAt = e.StartAt, EndAt = e.EndAt };
                    }).ToArray();
                    app.Logger.LogDebug($"Public Staff Shifts ok club={clubId} count={res.Length}");
                    return Results.Json(res);
                }
                else
                {
                    var keys = Store.ShiftEntries.Keys.Where(k => k.StartsWith(clubId + "|", StringComparison.Ordinal)).ToArray();
                    var list = keys.Select(k => Store.ShiftEntries.TryGetValue(k, out var e) ? e : null).Where(e => e != null && !string.IsNullOrWhiteSpace(e!.AssignedUid)).Select(e => e!).OrderBy(e => e.StartAt).Select(e =>
                    {
                        var uname = string.Empty;
                        if (!string.IsNullOrWhiteSpace(e.AssignedUid))
                        {
                            foreach (var kv in Store.StaffUsers)
                            {
                                if (kv.Value != null && string.Equals(kv.Value.Uid, e.AssignedUid, StringComparison.Ordinal))
                                {
                                    uname = kv.Key;
                                    break;
                                }
                            }
                        }
                        SplitNameAndHomeWorld(uname, out var n1, out var w1);
                        return new { Title = e.Title, StaffName = n1, StaffHomeWorld = w1, Job = e.Job, StartAt = e.StartAt, EndAt = e.EndAt };
                    }).ToArray();
                    app.Logger.LogDebug($"Public Staff Shifts ok mem club={clubId} count={list.Length}");
                    return Results.Json(list);
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogDebug($"Public Staff Shifts error ak={accessKey}: {ex.Message}");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireCors("PublicJson");

        app.MapGet("/{accessKey}/djshifts.json", async (string accessKey, HttpContext ctx) =>
        {
            app.Logger.LogDebug($"Public DJ Shifts GET ip={ctx.Connection.RemoteIpAddress} ak={accessKey}");
            try
            {
                if (string.IsNullOrWhiteSpace(accessKey)) { app.Logger.LogDebug("Public DJ Shifts missing accessKey"); return Results.Json(Array.Empty<ShiftEntry>()); }
                string? clubId = null;
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    using var scope = app.Services.CreateScope();
                    var ef = scope.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                    clubId = await ef.GetClubIdByAccessKeyAsync(accessKey);
                }
                else
                {
                    clubId = Store.ClubAccessKeysByKey.TryGetValue(accessKey, out var c) ? c : null;
                }
                if (string.IsNullOrWhiteSpace(clubId)) { app.Logger.LogDebug("Public DJ Shifts no club for accessKey"); return Results.Json(Array.Empty<ShiftEntry>()); }
                if (!string.IsNullOrWhiteSpace(conn))
                {
                    using var scope2 = app.Services.CreateScope();
                    var ef2 = scope2.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                    var list = await ef2.LoadShiftEntriesAsync(clubId!) ?? Array.Empty<ShiftEntry>();
                    var djs = await ef2.LoadDjEntriesAsync(clubId!) ?? Array.Empty<DjEntry>();
                    var twitchByDj = djs.ToDictionary(d => d.DjName, d => d.TwitchLink, StringComparer.Ordinal);
                    var res = list.Where(e => !string.IsNullOrWhiteSpace(e.DjName)).OrderBy(e => e.StartAt).Select(e =>
                    {
                        var djName = e.DjName ?? string.Empty;
                        var link = twitchByDj.TryGetValue(djName, out var l) ? l : string.Empty;
                        return new { Title = e.Title, DjName = djName, TwitchLink = link, StartAt = e.StartAt, EndAt = e.EndAt };
                    }).ToArray();
                    app.Logger.LogDebug($"Public DJ Shifts ok club={clubId} count={res.Length}");
                    return Results.Json(res);
                }
                else
                {
                    var keys = Store.ShiftEntries.Keys.Where(k => k.StartsWith(clubId + "|", StringComparison.Ordinal)).ToArray();
                    var djKeys = Store.DjEntries.Keys.Where(k => k.StartsWith(clubId + "|", StringComparison.Ordinal)).ToArray();
                    var twitchByDj = djKeys.Select(k => Store.DjEntries.TryGetValue(k, out var e) ? e : null).Where(e => e != null).Select(e => e!).ToDictionary(e => e.DjName, e => e.TwitchLink, StringComparer.Ordinal);
                    var list = keys.Select(k => Store.ShiftEntries.TryGetValue(k, out var e) ? e : null).Where(e => e != null && !string.IsNullOrWhiteSpace(e!.DjName)).Select(e => e!).OrderBy(e => e.StartAt).Select(e =>
                    {
                        var djName = e.DjName ?? string.Empty;
                        var link = twitchByDj.TryGetValue(djName, out var l) ? l : string.Empty;
                        return new { Title = e.Title, DjName = djName, TwitchLink = link, StartAt = e.StartAt, EndAt = e.EndAt };
                    }).ToArray();
                    app.Logger.LogDebug($"Public DJ Shifts ok mem club={clubId} count={list.Length}");
                    return Results.Json(list);
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogDebug($"Public DJ Shifts error ak={accessKey}: {ex.Message}");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireCors("PublicJson");
    }
}
