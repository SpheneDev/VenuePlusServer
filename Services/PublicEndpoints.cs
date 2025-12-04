using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace VenuePlus.Server;

public static class PublicEndpoints
{
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
                return Results.Ok(new { ok = true, dbOk, time = DateTimeOffset.UtcNow });
            }
            catch (Exception ex)
            {
                app.Logger.LogDebug($"Health db error: {ex.Message}");
                return Results.Ok(new { ok = true, dbOk = false, time = DateTimeOffset.UtcNow });
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
                    var users = list.OrderBy(u => u.Username, StringComparer.Ordinal).Select(u => new { Username = u.Username, Job = u.Job, Role = u.Role, CreatedAt = u.CreatedAt }).ToArray();
                    app.Logger.LogDebug($"Public Staff ok club={clubId} count={users.Length}");
                    return Results.Json(users);
                }
                else
                {
                    var usersClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubId + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubId!.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                    var users = usersClub.Select(u => new
                    {
                        Username = u,
                        Job = (Store.ClubUserJobs.TryGetValue(clubId! + "|" + u, out var j) ? j : "Unassigned"),
                        Role = (Store.StaffUsers.TryGetValue(u, out var info) ? info.Role : "power"),
                        CreatedAt = (Store.StaffUsers.TryGetValue(u, out var info2) ? info2.CreatedAt : DateTimeOffset.UtcNow)
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
    }
}
