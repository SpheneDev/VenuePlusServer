using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;

namespace VenuePlus.Server;

public static class WebSocketMiddleware
{
    private static readonly object _adminStateLock = new();
    private static System.Threading.CancellationTokenSource? _shutdownCts;
    private static System.Threading.Tasks.Task? _shutdownTask;

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

    private static Rights MergeRights(System.Collections.Generic.Dictionary<string, Rights> rightsMap, string[] jobs)
    {
        var res = new Rights();
        int bestRank = 0;
        for (int i = 0; i < jobs.Length; i++)
        {
            if (!rightsMap.TryGetValue(jobs[i], out var r)) continue;
            if (r.AddVip) res.AddVip = true;
            if (r.RemoveVip) res.RemoveVip = true;
            if (r.EditVipHomeWorld) res.EditVipHomeWorld = true;
            if (r.ManageUsers) res.ManageUsers = true;
            if (r.DeleteStaffMember) res.DeleteStaffMember = true;
            if (r.ManageJobs) res.ManageJobs = true;
            if (r.ManageVenueSettings) res.ManageVenueSettings = true;
            if (r.EditVipDuration) res.EditVipDuration = true;
            if (r.AddDj) res.AddDj = true;
            if (r.RemoveDj) res.RemoveDj = true;
            if (r.EditShiftPlan) res.EditShiftPlan = true;
            if (r.Rank > bestRank) bestRank = r.Rank;
        }
        if (HasOwner(jobs))
        {
            res.AddVip = true;
            res.RemoveVip = true;
            res.EditVipHomeWorld = true;
            res.ManageUsers = true;
            res.DeleteStaffMember = true;
            res.ManageJobs = true;
            res.ManageVenueSettings = true;
            res.EditVipDuration = true;
            res.AddDj = true;
            res.RemoveDj = true;
            res.EditShiftPlan = true;
            if (bestRank < 10) bestRank = 10;
        }
        res.Rank = bestRank;
        return res;
    }

    private static string GetPrimaryJob(System.Collections.Generic.Dictionary<string, Rights> rightsMap, string[] jobs)
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

    private static async System.Threading.Tasks.Task<string[]> GetJobsFromDbAsync(VenuePlus.Server.Services.EfStore ef, string clubId, string username)
    {
        var info = await ef.GetStaffUserAsync(clubId, username);
        return info == null ? new[] { "Unassigned" } : EnsureJobs(info.Jobs);
    }

    private static string[] GetJobsFromStore(string clubId, string username)
    {
        return Store.GetJobsForUser(clubId, username);
    }

    private static bool TryGetJobsFromPayload(JsonElement root, out string[] jobs)
    {
        jobs = Array.Empty<string>();
        if (root.TryGetProperty("jobs", out var jobsEl) && jobsEl.ValueKind == JsonValueKind.Array)
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var el in jobsEl.EnumerateArray())
            {
                var val = el.GetString();
                if (!string.IsNullOrWhiteSpace(val)) list.Add(val);
            }
            jobs = EnsureJobs(list.ToArray());
            return true;
        }
        if (root.TryGetProperty("job", out var jobEl))
        {
            var job = jobEl.GetString() ?? string.Empty;
            jobs = EnsureJobs(string.IsNullOrWhiteSpace(job) ? Array.Empty<string>() : new[] { job });
            return true;
        }
        return false;
    }

    private static object BuildStaffUserPayload(StaffUserInfo info, System.Collections.Generic.Dictionary<string, Rights> rightsMap, string? viewerUsername)
    {
        var jobs = EnsureJobs(info.Jobs);
        var uid = string.Equals(info.Username, viewerUsername, StringComparison.Ordinal) ? info.Uid : string.Empty;
        return new { Username = info.Username, Jobs = jobs, Job = GetPrimaryJob(rightsMap, jobs), Role = info.Role, CreatedAt = info.CreatedAt, Uid = uid, IsManual = info.IsManual, IsOnline = IsUserOnline(info.Username), Birthday = info.Birthday };
    }

    private static object BuildStaffUserPayloadFromStore(string clubId, string username, System.Collections.Generic.Dictionary<string, Rights> rightsMap, string? viewerUsername)
    {
        var jobs = EnsureJobs(Store.GetJobsForUser(clubId, username));
        Store.StaffUsers.TryGetValue(username, out var info);
        var role = info?.Role ?? "power";
        var createdAt = info?.CreatedAt ?? DateTimeOffset.UtcNow;
        var uid = string.Equals(username, viewerUsername, StringComparison.Ordinal) ? (!string.IsNullOrWhiteSpace(info?.Uid) ? info!.Uid : username) : string.Empty;
        var isManual = info?.IsManual ?? false;
        var birthday = info?.Birthday;
        return new { Username = username, Jobs = jobs, Job = GetPrimaryJob(rightsMap, jobs), Role = role, CreatedAt = createdAt, Uid = uid, IsManual = isManual, IsOnline = IsUserOnline(username), Birthday = birthday };
    }

    private static bool IsUserOnline(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in Store.StaffSessions)
        {
            if (!string.Equals(kv.Value, username, StringComparison.Ordinal)) continue;
            if (Store.StaffSessionExpiry.TryGetValue(kv.Key, out var exp) && exp > now) return true;
        }
        return false;
    }

    private static string ResolveViewerUsername(Guid socketId)
    {
        var tokens = WebSocketStore.GetSessions(socketId);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (Store.StaffSessions.TryGetValue(tokens[i], out var user) && !string.IsNullOrWhiteSpace(user)) return user;
        }
        return string.Empty;
    }

    private static string[] GetUserClubsFromStore(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return Array.Empty<string>();
        var set = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var k in Store.ClubUserJobs.Keys)
        {
            var p = k.IndexOf('|');
            if (p <= 0) continue;
            var user = k.Substring(p + 1);
            if (!string.Equals(user, username, StringComparison.Ordinal)) continue;
            var club = k.Substring(0, p);
            if (!string.IsNullOrWhiteSpace(club)) set.Add(club);
        }
        var arr = new string[set.Count];
        int i = 0;
        foreach (var c in set)
        {
            arr[i] = c;
            i++;
        }
        Array.Sort(arr, StringComparer.Ordinal);
        return arr;
    }

    private static async System.Threading.Tasks.Task<bool> IsServerAdminAsync(WebApplication app, string? conn, string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        if (!string.IsNullOrWhiteSpace(conn))
        {
            using var scopeEf = app.Services.CreateScope();
            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
            var info = await efSvc.GetStaffUserByUsernameAsync(username);
            return info?.IsServerAdmin ?? false;
        }
        Store.StaffUsers.TryGetValue(username, out var infoMem);
        return infoMem != null && infoMem.IsServerAdmin;
    }

    private static string BuildAnnouncementMessage(string baseMessage, int minutesLeft, bool restart)
    {
        if (minutesLeft <= 0)
        {
            if (string.IsNullOrWhiteSpace(baseMessage)) return restart ? "Server is restarting now." : "Server is shutting down now.";
            return baseMessage;
        }
        var action = restart ? "restart" : "shutdown";
        var suffix = $"Server will {action} in {minutesLeft} minutes.";
        if (string.IsNullOrWhiteSpace(baseMessage)) return suffix;
        return baseMessage + " - " + suffix;
    }

    private static async System.Threading.Tasks.Task BroadcastUsersDetailsForClubDbAsync(string clubId, VenuePlus.Server.Services.EfStore efSvc)
    {
        var list = await efSvc.GetStaffUsersAsync(clubId) ?? Array.Empty<StaffUserInfo>();
        var rightsDb = await efSvc.GetJobRightsAsync(clubId);
        await BroadcastUsersDetailsForClubDbAsync(clubId, list, rightsDb);
    }

    private static async System.Threading.Tasks.Task BroadcastUsersDetailsForClubDbAsync(string clubId, StaffUserInfo[] list, System.Collections.Generic.Dictionary<string, Rights> rightsDb)
    {
        var ordered = list.OrderBy(u => u.Username, StringComparer.Ordinal).ToArray();
        var sockets = WebSocketStore.GetSocketsForClub(clubId);
        for (int i = 0; i < sockets.Length; i++)
        {
            var viewer = ResolveViewerUsername(sockets[i].Id);
            var users = ordered.Select(u => BuildStaffUserPayload(u, rightsDb, viewer)).ToArray();
            await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
        }
    }

    private static async System.Threading.Tasks.Task BroadcastUsersDetailsForClubMemAsync(string clubId)
    {
        var usersClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubId + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubId.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
        var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
        var sockets = WebSocketStore.GetSocketsForClub(clubId);
        for (int i = 0; i < sockets.Length; i++)
        {
            var viewer = ResolveViewerUsername(sockets[i].Id);
            var users = usersClub.Select(u => BuildStaffUserPayloadFromStore(clubId, u, rightsMap, viewer)).ToArray();
            await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
        }
    }

    private static async System.Threading.Tasks.Task SendUsersDetailsToSocketDbAsync(Guid socketId, WebSocket ws, StaffUserInfo[] list, System.Collections.Generic.Dictionary<string, Rights> rightsDb)
    {
        var ordered = list.OrderBy(u => u.Username, StringComparer.Ordinal).ToArray();
        var viewer = ResolveViewerUsername(socketId);
        var users = ordered.Select(u => BuildStaffUserPayload(u, rightsDb, viewer)).ToArray();
        await WebSocketStore.SendAsync(ws, new { type = "users.details", users });
    }

    private static async System.Threading.Tasks.Task SendUsersDetailsToSocketMemAsync(Guid socketId, WebSocket ws, string clubId, string[] usersClub, System.Collections.Generic.Dictionary<string, Rights> rightsMap)
    {
        var ordered = usersClub.OrderBy(u => u, StringComparer.Ordinal).ToArray();
        var viewer = ResolveViewerUsername(socketId);
        var users = ordered.Select(u => BuildStaffUserPayloadFromStore(clubId, u, rightsMap, viewer)).ToArray();
        await WebSocketStore.SendAsync(ws, new { type = "users.details", users });
    }

    public static void Use(WebApplication app, string? conn)
    {
        app.UseWebSockets();
        app.Use(async (ctx, next) =>
        {
            var isWsPath = string.Equals(ctx.Request.Path, "/ws", StringComparison.Ordinal) || string.Equals(ctx.Request.Path, "/ws/", StringComparison.Ordinal);
            if (!isWsPath)
            {
                await next();
                return;
            }
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var id = Guid.NewGuid();
            string clubIdWs = (ctx.Request.Query.TryGetValue("clubId", out var q) && q.Count > 0 ? q[0] : "default") ?? "default";
            WebSocketStore.Add(id, ws, clubIdWs);
            app.Logger.LogDebug($"WS connect id={id} club={clubIdWs}");
            if (!string.IsNullOrWhiteSpace(conn))
            {
                using var scopeEfWs = app.Services.CreateScope();
                var efSvcWs = scopeEfWs.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                var entriesDb = await efSvcWs.LoadVipEntriesAsync(clubIdWs) ?? Array.Empty<VipEntry>();
                await WebSocketStore.SendAsync(ws, new { type = "vip.snapshot", entries = entriesDb.OrderBy(e => e.CharacterName, StringComparer.Ordinal).ToArray() });
                var djsDb = await efSvcWs.LoadDjEntriesAsync(clubIdWs) ?? Array.Empty<DjEntry>();
                await WebSocketStore.SendAsync(ws, new { type = "dj.snapshot", entries = djsDb.OrderBy(e => e.DjName, StringComparer.Ordinal).ToArray() });
                var shiftsDb = await efSvcWs.LoadShiftEntriesAsync(clubIdWs) ?? Array.Empty<ShiftEntry>();
                await WebSocketStore.SendAsync(ws, new { type = "shift.snapshot", entries = shiftsDb.OrderBy(e => e.StartAt).ToArray() });
                var usersDbInit = await efSvcWs.GetStaffUsersAsync(clubIdWs) ?? Array.Empty<StaffUserInfo>();
                await WebSocketStore.SendAsync(ws, new { type = "users.list", users = usersDbInit.Select(u => u.Username).OrderBy(u => u, StringComparer.Ordinal).ToArray() });
                var jobsDbInit = await efSvcWs.GetJobsAsync(clubIdWs) ?? Array.Empty<string>();
                await WebSocketStore.SendAsync(ws, new { type = "jobs.list", jobs = jobsDbInit });
                var rightsDbInit = await efSvcWs.GetJobRightsAsync(clubIdWs);
                await SendUsersDetailsToSocketDbAsync(id, ws, usersDbInit, rightsDbInit);
                await WebSocketStore.SendAsync(ws, new { type = "jobs.rights", rights = rightsDbInit });
                var logoInit = await efSvcWs.GetClubLogoAsync(clubIdWs);
                await WebSocketStore.SendAsync(ws, new { type = "club.logo", clubId = clubIdWs, logoBase64 = logoInit ?? string.Empty });
            }
            else
            {
                await WebSocketStore.SendAsync(ws, new { type = "vip.snapshot", entries = Array.Empty<VipEntry>() });
                var djKeys = Store.DjEntries.Keys.Where(k => k.StartsWith(clubIdWs + "|", StringComparison.Ordinal)).ToArray();
                var djList = djKeys.Select(k => Store.DjEntries.TryGetValue(k, out var e) ? e : null).Where(e => e != null).Select(e => e!).OrderBy(e => e.DjName, StringComparer.Ordinal).ToArray();
                await WebSocketStore.SendAsync(ws, new { type = "dj.snapshot", entries = djList });
                var shiftKeys = Store.ShiftEntries.Keys.Where(k => k.StartsWith(clubIdWs + "|", StringComparison.Ordinal)).ToArray();
                var shiftList = shiftKeys.Select(k => Store.ShiftEntries.TryGetValue(k, out var e) ? e : null).Where(e => e != null).Select(e => e!).OrderBy(e => e.StartAt).ToArray();
                await WebSocketStore.SendAsync(ws, new { type = "shift.snapshot", entries = shiftList });
                var usersForClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubIdWs + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubIdWs.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                await WebSocketStore.SendAsync(ws, new { type = "users.list", users = usersForClub });
                var rightsMapInit = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                await SendUsersDetailsToSocketMemAsync(id, ws, clubIdWs, usersForClub, rightsMapInit);
                await WebSocketStore.SendAsync(ws, new { type = "jobs.list", jobs = Store.JobRights.Keys.OrderBy(j => j, StringComparer.Ordinal).ToArray() });
                await WebSocketStore.SendAsync(ws, new { type = "jobs.rights", rights = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value) });
            }
            var buffer = new byte[128 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    using var recvMs = new MemoryStream();
                    bool closedMsg = false;
                    while (true)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), app.Lifetime.ApplicationStopping);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", app.Lifetime.ApplicationStopping); } catch (Exception ex) { app.Logger.LogDebug($"WS close output failed: {ex.Message}"); }
                            closedMsg = true;
                            break;
                        }
                        if (result.Count > 0) recvMs.Write(buffer, 0, result.Count);
                        if (result.EndOfMessage) break;
                    }
                    if (closedMsg) break;
                    var msg = Encoding.UTF8.GetString(recvMs.ToArray());
                    using var doc = JsonDocument.Parse(msg);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                    if (type == "switch.club")
                    {
                        var newClub = root.TryGetProperty("clubId", out var c) ? (c.GetString() ?? "default") : "default";
                        var had = WebSocketStore.TryGetClub(id, out var curClub) && !string.IsNullOrWhiteSpace(curClub);
                        if (had && string.Equals(curClub!, newClub, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        app.Logger.LogDebug($"WS switch.club id={id} from={(had ? curClub! : "none")} to={newClub}");
                        WebSocketStore.SetClub(id, newClub);
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfWs2 = app.Services.CreateScope();
                            var efSvcWs2 = scopeEfWs2.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var entriesDbSw = await efSvcWs2.LoadVipEntriesAsync(newClub);
                            await WebSocketStore.SendAsync(ws, new { type = "vip.snapshot", entries = entriesDbSw.OrderBy(e => e.CharacterName, StringComparer.Ordinal).ToArray() });
                            var djsDbSw = await efSvcWs2.LoadDjEntriesAsync(newClub) ?? Array.Empty<DjEntry>();
                            await WebSocketStore.SendAsync(ws, new { type = "dj.snapshot", entries = djsDbSw.OrderBy(e => e.DjName, StringComparer.Ordinal).ToArray() });
                        var usersDb = await efSvcWs2.GetStaffUsersAsync(newClub);
                        await WebSocketStore.SendAsync(ws, new { type = "users.list", users = usersDb.Select(u => u.Username).OrderBy(u => u, StringComparer.Ordinal).ToArray() });
                        var jobsDb = await efSvcWs2.GetJobsAsync(newClub);
                        await WebSocketStore.SendAsync(ws, new { type = "jobs.list", jobs = jobsDb });
                        var rightsDb = await efSvcWs2.GetJobRightsAsync(newClub);
                        await SendUsersDetailsToSocketDbAsync(id, ws, usersDb, rightsDb);
                        await WebSocketStore.SendAsync(ws, new { type = "jobs.rights", rights = rightsDb });
                        var shiftsDbSw = await efSvcWs2.LoadShiftEntriesAsync(newClub) ?? Array.Empty<ShiftEntry>();
                        await WebSocketStore.SendAsync(ws, new { type = "shift.snapshot", entries = shiftsDbSw.OrderBy(e => e.StartAt).ToArray() });
                        var logoSw = await efSvcWs2.GetClubLogoAsync(newClub);
                        await WebSocketStore.SendAsync(ws, new { type = "club.logo", clubId = newClub, logoBase64 = logoSw ?? string.Empty });
                        }
                        else
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "vip.snapshot", entries = Array.Empty<VipEntry>() });
                            var djKeysSw = Store.DjEntries.Keys.Where(k => k.StartsWith(newClub + "|", StringComparison.Ordinal)).ToArray();
                        var djListSw = djKeysSw.Select(k => Store.DjEntries.TryGetValue(k, out var e) ? e : null).Where(e => e != null).Select(e => e!).OrderBy(e => e.DjName, StringComparer.Ordinal).ToArray();
                        await WebSocketStore.SendAsync(ws, new { type = "dj.snapshot", entries = djListSw });
                        var shiftKeysSw = Store.ShiftEntries.Keys.Where(k => k.StartsWith(newClub + "|", StringComparison.Ordinal)).ToArray();
                        var shiftListSw = shiftKeysSw.Select(k => Store.ShiftEntries.TryGetValue(k, out var e) ? e : null).Where(e => e != null).Select(e => e!).OrderBy(e => e.StartAt).ToArray();
                        await WebSocketStore.SendAsync(ws, new { type = "shift.snapshot", entries = shiftListSw });
                        var usersClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(newClub + "|", StringComparison.Ordinal)).Select(k => k.Substring(newClub.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                        await WebSocketStore.SendAsync(ws, new { type = "users.list", users = usersClub });
                            var rightsMapSw = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            await SendUsersDetailsToSocketMemAsync(id, ws, newClub, usersClub, rightsMapSw);
                            await WebSocketStore.SendAsync(ws, new { type = "jobs.list", jobs = Store.JobRights.Keys.OrderBy(j => j, StringComparer.Ordinal).ToArray() });
                            await WebSocketStore.SendAsync(ws, new { type = "jobs.rights", rights = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value) });
                        }
                        continue;
                    }
                    if (type == "login.request")
                    {
                        var username = root.TryGetProperty("username", out var u) ? (u.GetString() ?? string.Empty) : string.Empty;
                        var password = root.TryGetProperty("password", out var p) ? (p.GetString() ?? string.Empty) : string.Empty;
                        var clubIdLogin = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) { await WebSocketStore.SendAsync(ws, new { type = "login.fail" }); continue; }
                        if (!LoginRateLimiter.Allow(username)) { await WebSocketStore.SendAsync(ws, new { type = "login.fail" }); app.Logger.LogDebug($"WS login.fail user={username} club={clubIdLogin} reason=rate"); continue; }
                        StaffUserInfo? info = null;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            info = await efSvc.GetStaffUserAsync(clubIdLogin, username);
                            if (info == null) info = await efSvc.GetStaffUserByUsernameAsync(username);
                        }
                        else
                        {
                            if (Store.ClubUserJobs.ContainsKey(clubIdLogin + "|" + username)) Store.StaffUsers.TryGetValue(username, out info);
                            if (info == null) Store.StaffUsers.TryGetValue(username, out info);
                        }
                        if (info == null) { await WebSocketStore.SendAsync(ws, new { type = "login.fail" }); app.Logger.LogDebug($"WS login.fail user={username} club={clubIdLogin} reason=notfound"); continue; }
                        var ok = Util.VerifyPassword(username, password, info.PasswordHash);
                        if (!ok) { await WebSocketStore.SendAsync(ws, new { type = "login.fail" }); app.Logger.LogDebug($"WS login.fail user={username} club={clubIdLogin} reason=password"); continue; }
                        if (Store.MaintenanceMode)
                        {
                            var isAdmin = await IsServerAdminAsync(app, conn, username);
                            if (!isAdmin)
                            {
                                await WebSocketStore.SendAsync(ws, new { type = "login.fail", message = "Maintenance mode active" });
                                app.Logger.LogDebug($"WS login.fail user={username} club={clubIdLogin} reason=maintenance");
                                continue;
                            }
                        }
                        Store.StaffUsers[username] = info;
                        var token = Util.NewToken();
                        Store.StaffSessions[token] = username;
                        Store.StaffSessionExpiry[token] = DateTimeOffset.UtcNow.AddHours(8);
                        WebSocketStore.AddSession(id, token);
                        await WebSocketStore.SendAsync(ws, new { type = "login.ok", token });
                        app.Logger.LogDebug($"WS login.ok user={username} club={clubIdLogin}");
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfClubs = app.Services.CreateScope();
                            var efClubs = scopeEfClubs.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var clubs = await efClubs.GetUserClubsAsync(username) ?? Array.Empty<string>();
                            await WebSocketStore.SendAsync(ws, new { type = "user.clubs", clubs });
                            var created = await efClubs.GetCreatedClubsAsync(username) ?? Array.Empty<string>();
                            await WebSocketStore.SendAsync(ws, new { type = "user.clubs.created", clubs = created });
                            for (int i = 0; i < clubs.Length; i++)
                            {
                                await BroadcastUsersDetailsForClubDbAsync(clubs[i], efClubs);
                            }
                        }
                        else
                        {
                            var clubsMem = GetUserClubsFromStore(username);
                            await WebSocketStore.SendAsync(ws, new { type = "user.clubs", clubs = clubsMem });
                            var clubsCreated = Store.CreatedClubs
                                .Where(kv => string.Equals(kv.Value, username, StringComparison.Ordinal))
                                .Select(kv => kv.Key)
                                .OrderBy(c => c, StringComparer.Ordinal)
                                .ToArray();
                            await WebSocketStore.SendAsync(ws, new { type = "user.clubs.created", clubs = clubsCreated });
                            for (int i = 0; i < clubsMem.Length; i++)
                            {
                                await BroadcastUsersDetailsForClubMemAsync(clubsMem[i]);
                            }
                        }
                        continue;
                    }
                    if (type == "register.request")
                    {
                        var characterName = root.TryGetProperty("characterName", out var cn) ? (cn.GetString() ?? string.Empty) : string.Empty;
                        var homeWorld = root.TryGetProperty("homeWorld", out var hw) ? (hw.GetString() ?? string.Empty) : string.Empty;
                        var password = root.TryGetProperty("password", out var pw) ? (pw.GetString() ?? string.Empty) : string.Empty;
                        var usernameNew = (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(homeWorld)) ? string.Empty : (characterName + "@" + homeWorld);
                        if (string.IsNullOrWhiteSpace(usernameNew) || string.IsNullOrWhiteSpace(password)) { await WebSocketStore.SendAsync(ws, new { type = "register.fail", code = 400 }); continue; }
                        if (Store.MaintenanceMode)
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "register.fail", code = 403, message = "Maintenance mode active" });
                            continue;
                        }
                        if (!LoginRateLimiter.Allow("reg|" + usernameNew)) { await WebSocketStore.SendAsync(ws, new { type = "register.fail", code = 429 }); continue; }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var existsDb = await efChk.GetStaffUserByUsernameAsync(usernameNew);
                            if (existsDb != null) { await WebSocketStore.SendAsync(ws, new { type = "register.fail", code = 409 }); continue; }
                        }
                        else
                        {
                            if (Store.StaffUsers.ContainsKey(usernameNew)) { await WebSocketStore.SendAsync(ws, new { type = "register.fail", code = 409 }); continue; }
                        }
                        var recoveryCode = Util.NewUid(24);
                        var recoveryHash = Util.Sha256(recoveryCode);
                        var info = new StaffUserInfo { Username = usernameNew, PasswordHash = Util.HashPassword(usernameNew, password), RecoveryCodeHash = recoveryHash, Jobs = Array.Empty<string>(), Role = "power", CreatedAt = DateTimeOffset.UtcNow, Uid = usernameNew };
                        Store.StaffUsers[usernameNew] = info;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf2 = app.Services.CreateScope();
                            var efSvc2 = scopeEf2.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            await efSvc2.CreateBaseUserAsync(usernameNew, info.PasswordHash);
                            await efSvc2.SetRecoveryCodeHashAsync(usernameNew, recoveryHash);
                        }
                        else
                        {
                            await Persistence.SaveAsync();
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "register.ok", recoveryCode });
                        continue;
                    }
                    if (type == "session.logout")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        if (string.IsNullOrWhiteSpace(token)) { await WebSocketStore.SendAsync(ws, new { type = "session.logout.fail", code = 400 }); continue; }
                        Store.StaffSessions.TryGetValue(token, out var logoutUser);
                        Store.StaffSessions.TryRemove(token, out _);
                        Store.StaffSessionExpiry.TryRemove(token, out _);
                        WebSocketStore.RemoveSession(id, token);
                        await WebSocketStore.SendAsync(ws, new { type = "session.logout.ok" });
                        if (!string.IsNullOrWhiteSpace(logoutUser))
                        {
                            if (!string.IsNullOrWhiteSpace(conn))
                            {
                                using var scopeEfClubs = app.Services.CreateScope();
                                var efClubs = scopeEfClubs.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                var clubs = await efClubs.GetUserClubsAsync(logoutUser) ?? Array.Empty<string>();
                                for (int i = 0; i < clubs.Length; i++)
                                {
                                    await BroadcastUsersDetailsForClubDbAsync(clubs[i], efClubs);
                                }
                            }
                            else
                            {
                                var clubsMem = GetUserClubsFromStore(logoutUser);
                                for (int i = 0; i < clubsMem.Length; i++)
                                {
                                    await BroadcastUsersDetailsForClubMemAsync(clubsMem[i]);
                                }
                            }
                        }
                        continue;
                    }
                    if (type == "users.list.request")
                    {
                        var clubReq = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfU = app.Services.CreateScope();
                            var efU = scopeEfU.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var usersDb = await efU.GetStaffUsersAsync(clubReq) ?? Array.Empty<StaffUserInfo>();
                            await WebSocketStore.SendAsync(ws, new { type = "users.list", users = usersDb.Select(u => u.Username).OrderBy(u => u, StringComparer.Ordinal).ToArray() });
                        }
                        else
                        {
                            var usersForClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubReq + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubReq.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                            await WebSocketStore.SendAsync(ws, new { type = "users.list", users = usersForClub });
                        }
                        continue;
                    }
                    if (type == "users.details.request")
                    {
                        var clubReq = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfU = app.Services.CreateScope();
                            var efU = scopeEfU.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var usersDb = await efU.GetStaffUsersAsync(clubReq) ?? Array.Empty<StaffUserInfo>();
                            await WebSocketStore.SendAsync(ws, new { type = "users.details", users = usersDb.OrderBy(u => u.Username, StringComparer.Ordinal).Select(u => new StaffUser { Username = u.Username, Jobs = u.Jobs, Role = u.Role, CreatedAt = u.CreatedAt, Uid = u.Uid, IsManual = u.IsManual, IsOnline = IsUserOnline(u.Username), Birthday = u.Birthday }).ToArray() });
                        }
                        else
                        {
                            var usersForClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubReq + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubReq.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                            var detMem2 = usersForClub.OrderBy(u => u, StringComparer.Ordinal).Select(u => new StaffUser { Username = u, Jobs = Store.GetJobsForUser(clubReq, u), Role = (Store.StaffUsers.TryGetValue(u, out var info) ? info.Role : "power"), CreatedAt = (Store.StaffUsers.TryGetValue(u, out var info2) ? info2.CreatedAt : DateTimeOffset.UtcNow), Uid = (Store.StaffUsers.TryGetValue(u, out var info3) ? info3.Uid : u), IsManual = (Store.StaffUsers.TryGetValue(u, out var info4) && info4 != null && info4.IsManual), IsOnline = IsUserOnline(u), Birthday = (Store.StaffUsers.TryGetValue(u, out var info5) ? info5.Birthday : null) }).ToArray();
                            await WebSocketStore.SendAsync(ws, new { type = "users.details", users = detMem2 });
                        }
                        continue;
                    }
                    if (type == "jobs.list.request")
                    {
                        var clubReq = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfJ = app.Services.CreateScope();
                            var efJ = scopeEfJ.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var jobsDb = await efJ.GetJobsAsync(clubReq) ?? Array.Empty<string>();
                            await WebSocketStore.SendAsync(ws, new { type = "jobs.list", jobs = jobsDb });
                        }
                        else
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "jobs.list", jobs = Store.JobRights.Keys.OrderBy(j => j, StringComparer.Ordinal).ToArray() });
                        }
                        continue;
                    }
                    if (type == "jobs.rights.request")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var clubReq = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "jobs.rights.fail" }); continue; }
                        bool canManageJobs;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var rightsDb = await efSvc.GetJobRightsAsync(clubReq);
                            var jobs = await GetJobsFromDbAsync(efSvc, clubReq, username);
                            var rights = MergeRights(rightsDb, jobs);
                            canManageJobs = rights.ManageJobs;
                            if (!canManageJobs) { await WebSocketStore.SendAsync(ws, new { type = "jobs.rights.fail" }); continue; }
                            await WebSocketStore.SendAsync(ws, new { type = "jobs.rights", rights = rightsDb });
                        }
                        else
                        {
                            var jobs = GetJobsFromStore(clubReq, username);
                            var rights = MergeRights(Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value), jobs);
                            canManageJobs = rights.ManageJobs;
                            if (!canManageJobs) { await WebSocketStore.SendAsync(ws, new { type = "jobs.rights.fail" }); continue; }
                            await WebSocketStore.SendAsync(ws, new { type = "jobs.rights", rights = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value) });
                        }
                        continue;
                    }
                    if (type == "jobs.rights.update")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
                        var rightsEl = root.TryGetProperty("rights", out var rEl) ? rEl : default;
                        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(name) || rightsEl.ValueKind != JsonValueKind.Object)
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "jobs.rights.fail" });
                            continue;
                        }
                        var rights = JsonSerializer.Deserialize<Rights>(rightsEl.GetRawText(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? new Rights();
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc2) && !string.IsNullOrWhiteSpace(cc2)) ? cc2! : "default";
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "jobs.rights.fail" }); continue; }
                        bool canManageJobs;
                        string[] actorJobs;
                        Rights actorRights;
                        bool isOwner;
                        int actorRank;
                        System.Collections.Generic.Dictionary<string, Rights> rightsMapChk;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var rightsDbChk = await efChk.GetJobRightsAsync(clubIdCur);
                            actorJobs = await GetJobsFromDbAsync(efChk, clubIdCur, username);
                            actorRights = MergeRights(rightsDbChk, actorJobs);
                            isOwner = HasOwner(actorJobs);
                            actorRank = actorRights.Rank;
                            canManageJobs = actorRights.ManageJobs;
                            rightsMapChk = rightsDbChk;
                            if (!canManageJobs) { await WebSocketStore.SendAsync(ws, new { type = "jobs.rights.fail" }); continue; }
                        }
                        else
                        {
                            rightsMapChk = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            actorJobs = GetJobsFromStore(clubIdCur, username);
                            actorRights = MergeRights(rightsMapChk, actorJobs);
                            isOwner = HasOwner(actorJobs);
                            actorRank = actorRights.Rank;
                            canManageJobs = actorRights.ManageJobs;
                            if (!canManageJobs) { await WebSocketStore.SendAsync(ws, new { type = "jobs.rights.fail" }); continue; }
                        }
                        if (!isOwner)
                        {
                            Rights existingRole;
                            if (!rightsMapChk.TryGetValue(name, out existingRole!)) existingRole = new Rights();
                            var existingRank = existingRole.Rank;
                            if (string.Equals(name, "Owner", StringComparison.Ordinal)) existingRank = 10;
                            else if (string.Equals(name, "Unassigned", StringComparison.Ordinal)) existingRank = 0;
                            else existingRank = existingRank <= 0 ? 1 : (existingRank > 9 ? 9 : existingRank);

                            var desiredRank = rights.Rank;
                            if (string.Equals(name, "Owner", StringComparison.Ordinal)) desiredRank = 10;
                            else if (string.Equals(name, "Unassigned", StringComparison.Ordinal)) desiredRank = 0;
                            else desiredRank = desiredRank <= 0 ? 1 : (desiredRank > 9 ? 9 : desiredRank);

                            bool isActorRole = false;
                            for (int i = 0; i < actorJobs.Length; i++)
                            {
                                if (string.Equals(actorJobs[i], name, StringComparison.Ordinal)) { isActorRole = true; break; }
                            }
                            if (rights.ManageJobs && !existingRole.ManageJobs) { await WebSocketStore.SendAsync(ws, new { type = "jobs.rights.fail" }); continue; }
                            if (rights.ManageVenueSettings && !existingRole.ManageVenueSettings) { await WebSocketStore.SendAsync(ws, new { type = "jobs.rights.fail" }); continue; }
                            if (desiredRank > actorRank) { await WebSocketStore.SendAsync(ws, new { type = "jobs.rights.fail" }); continue; }
                            if (!isActorRole && existingRank > actorRank) { await WebSocketStore.SendAsync(ws, new { type = "jobs.rights.fail" }); continue; }
                        }
                        if (string.Equals(name, "Owner", StringComparison.Ordinal))
                        {
                            var existing = Store.JobRights.TryGetValue(name, out var ex) ? ex : new Rights();
                                existing.AddVip = true; existing.RemoveVip = true; existing.EditVipHomeWorld = true; existing.ManageUsers = true; existing.DeleteStaffMember = true; existing.ManageJobs = true; existing.ManageVenueSettings = true; existing.EditVipDuration = true; existing.AddDj = true; existing.RemoveDj = true; existing.EditShiftPlan = true; existing.Rank = 10;
                            existing.ColorHex = rights.ColorHex ?? existing.ColorHex ?? "#FFFFFF";
                            existing.IconKey = rights.IconKey ?? existing.IconKey ?? "User";
                            Store.JobRights[name] = existing;
                        }
                        else
                        {
                            var r = rights ?? new Rights();
                            if (string.Equals(name, "Unassigned", StringComparison.Ordinal)) r.Rank = 0;
                            else r.Rank = r.Rank <= 0 ? 1 : (r.Rank > 9 ? 9 : r.Rank);
                            Store.JobRights[name] = r;
                        }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            if (string.Equals(name, "Owner", StringComparison.Ordinal))
                            {
                                var rightsDict = await efSvc.GetJobRightsAsync(clubIdCur);
                                var ex = rightsDict.TryGetValue(name, out var exDb) ? exDb : new Rights();
                                var merged = new Rights { AddVip = ex.AddVip, RemoveVip = ex.RemoveVip, EditVipHomeWorld = ex.EditVipHomeWorld, ManageUsers = ex.ManageUsers, DeleteStaffMember = ex.DeleteStaffMember, ManageJobs = ex.ManageJobs, ManageVenueSettings = ex.ManageVenueSettings, EditVipDuration = ex.EditVipDuration, AddDj = true, RemoveDj = true, EditShiftPlan = true, Rank = 10, ColorHex = rights.ColorHex ?? ex.ColorHex ?? "#FFFFFF", IconKey = rights.IconKey ?? ex.IconKey ?? "User" };
                                await efSvc.UpdateJobRightsAsync(clubIdCur, name, merged);
                            }
                            else
                            {
                                var r = rights ?? new Rights();
                                if (string.Equals(name, "Unassigned", StringComparison.Ordinal)) r.Rank = 0;
                                else r.Rank = r.Rank <= 0 ? 1 : (r.Rank > 9 ? 9 : r.Rank);
                                await efSvc.UpdateJobRightsAsync(clubIdCur, name, r);
                            }
                        }
                        else
                        {
                            await Persistence.SaveAsync();
                        }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfWs2 = app.Services.CreateScope();
                            var efSvcWs2 = scopeEfWs2.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var rightsDb = await efSvcWs2.GetJobRightsAsync(clubIdCur);
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "jobs.rights", rights = rightsDb });
                        }
                        else
                        {
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "jobs.rights", rights = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value) });
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "jobs.rights.ok" });
                        continue;
                    }
                    if (type == "user.delete")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var targetUsername = root.TryGetProperty("username", out var tu) ? (tu.GetString() ?? string.Empty) : string.Empty;
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var ccU) && !string.IsNullOrWhiteSpace(ccU)) ? ccU! : "default";
                        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(targetUsername)) { await WebSocketStore.SendAsync(ws, new { type = "user.delete.fail", message = "Missing token or username" }); continue; }
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "user.delete.fail", message = "Invalid session" }); app.Logger.LogDebug($"WS user.delete.fail club={clubIdCur} reason=session target={targetUsername}"); continue; }
                        var isSelf = string.Equals(targetUsername, username, StringComparison.Ordinal);
                        var userClubsAll = Array.Empty<string>();
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var rightsDbChk = await efChk.GetJobRightsAsync(clubIdCur);
                            var targetJobs = await GetJobsFromDbAsync(efChk, clubIdCur, targetUsername);
                            if (!isSelf)
                            {
                                var actorJobs = await GetJobsFromDbAsync(efChk, clubIdCur, username);
                                var actorRights = MergeRights(rightsDbChk, actorJobs);
                                var isOwner = HasOwner(actorJobs);
                                var canDelete = actorRights.DeleteStaffMember;
                                if (!(isOwner || canDelete)) { await WebSocketStore.SendAsync(ws, new { type = "user.delete.fail", message = "No rights" }); app.Logger.LogDebug($"WS user.delete.fail club={clubIdCur} reason=rights target={targetUsername}"); continue; }
                                var targetRights = MergeRights(rightsDbChk, targetJobs);
                                var actorRankDel = actorRights.Rank;
                                var targetRankDel = targetRights.Rank;
                                if (!isOwner && actorRankDel <= targetRankDel) { await WebSocketStore.SendAsync(ws, new { type = "user.delete.fail", message = "Cannot modify equal or higher rank" }); app.Logger.LogDebug($"WS user.delete.fail club={clubIdCur} reason=rank target={targetUsername}"); continue; }
                                if (HasOwner(targetJobs) && !isOwner) { await WebSocketStore.SendAsync(ws, new { type = "user.delete.fail", message = "Only owner can modify owner" }); app.Logger.LogDebug($"WS user.delete.fail club={clubIdCur} reason=owner target={targetUsername}"); continue; }
                                var listOwners = await efChk.GetStaffUsersAsync(clubIdCur) ?? Array.Empty<StaffUserInfo>();
                                var ownersCount = listOwners.Count(u => HasOwner(EnsureJobs(u.Jobs)));
                                if (HasOwner(targetJobs) && ownersCount <= 1) { await WebSocketStore.SendAsync(ws, new { type = "user.delete.fail", message = "Cannot remove last owner" }); app.Logger.LogDebug($"WS user.delete.fail club={clubIdCur} reason=lastowner target={targetUsername}"); continue; }
                                await efChk.DeleteStaffUserAsync(clubIdCur, targetUsername);
                                using var scopeEfWs = app.Services.CreateScope();
                                var efWs = scopeEfWs.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                var list = await efWs.GetStaffUsersAsync(clubIdCur) ?? Array.Empty<StaffUserInfo>();
                                await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "users.list", users = list.Select(u => u.Username).OrderBy(u => u, StringComparer.Ordinal).ToArray() });
                                var rightsDbUpd = await efWs.GetJobRightsAsync(clubIdCur);
                                await BroadcastUsersDetailsForClubDbAsync(clubIdCur, list, rightsDbUpd);
                                var ownersOnlyUpd = list.Where(u => HasOwner(EnsureJobs(u.Jobs))).Select(u => u.Username).ToArray();
                                if (ownersOnlyUpd.Length == 1)
                                {
                                    var loneOwnerUpd = ownersOnlyUpd[0];
                                    try { await efWs.SetClubCreatorAsync(clubIdCur, loneOwnerUpd); } catch { }
                                    Store.CreatedClubs[clubIdCur] = loneOwnerUpd;
                                    await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "access.owner.changed", clubId = clubIdCur, owner = loneOwnerUpd });
                                }
                                var ownersOnly = list.Where(u => HasOwner(EnsureJobs(u.Jobs))).Select(u => u.Username).ToArray();
                                if (ownersOnly.Length == 1)
                                {
                                    var loneOwner = ownersOnly[0];
                                    try { await efWs.SetClubCreatorAsync(clubIdCur, loneOwner); } catch { }
                                    Store.CreatedClubs[clubIdCur] = loneOwner;
                                    await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "access.owner.changed", clubId = clubIdCur, owner = loneOwner });
                                }
                            }
                            else
                            {
                                userClubsAll = await efChk.GetUserClubsAsync(targetUsername) ?? Array.Empty<string>();
                                await efChk.DeleteBaseUserAsync(targetUsername);
                                for (int i = 0; i < userClubsAll.Length; i++)
                                {
                                    var clubId = userClubsAll[i];
                                    var list = await efChk.GetStaffUsersAsync(clubId) ?? Array.Empty<StaffUserInfo>();
                                    await WebSocketStore.BroadcastToClubAsync(clubId, new { type = "users.list", users = list.Select(u => u.Username).OrderBy(u => u, StringComparer.Ordinal).ToArray() });
                                    var rightsDbUpd = await efChk.GetJobRightsAsync(clubId);
                                    await BroadcastUsersDetailsForClubDbAsync(clubId, list, rightsDbUpd);
                                    var ownersOnly = list.Where(u => HasOwner(EnsureJobs(u.Jobs))).Select(u => u.Username).ToArray();
                                    if (ownersOnly.Length == 0)
                                    {
                                        await efChk.DeleteClubAsync(clubId);
                                        Store.CreatedClubs.TryRemove(clubId, out _);
                                        await WebSocketStore.BroadcastToClubAsync(clubId, new { type = "club.deleted" });
                                        await WebSocketStore.BroadcastAsync(new { type = "membership.removed", username = string.Empty, clubId });
                                        continue;
                                    }
                                    if (ownersOnly.Length == 1)
                                    {
                                        var loneOwner = ownersOnly[0];
                                        try { await efChk.SetClubCreatorAsync(clubId, loneOwner); } catch { }
                                        Store.CreatedClubs[clubId] = loneOwner;
                                        await WebSocketStore.BroadcastToClubAsync(clubId, new { type = "access.owner.changed", clubId, owner = loneOwner });
                                    }
                                }
                            }
                        }
                        else
                        {
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            var targetJobs = GetJobsFromStore(clubIdCur, targetUsername);
                            if (!isSelf)
                            {
                                var actorJobs = GetJobsFromStore(clubIdCur, username);
                                var actorRights = MergeRights(rightsMap, actorJobs);
                                var isOwnerMem = HasOwner(actorJobs);
                                var canDeleteMem = actorRights.DeleteStaffMember;
                                if (!(isOwnerMem || canDeleteMem)) { await WebSocketStore.SendAsync(ws, new { type = "user.delete.fail", message = "No rights" }); app.Logger.LogDebug($"WS user.delete.fail club={clubIdCur} reason=rights mem target={targetUsername}"); continue; }
                                var targetRightsMem = MergeRights(rightsMap, targetJobs);
                                var actorRankDelMem = actorRights.Rank;
                                var targetRankDelMem = targetRightsMem.Rank;
                                if (!isOwnerMem && actorRankDelMem <= targetRankDelMem) { await WebSocketStore.SendAsync(ws, new { type = "user.delete.fail", message = "Cannot modify equal or higher rank" }); app.Logger.LogDebug($"WS user.delete.fail club={clubIdCur} reason=rank mem target={targetUsername}"); continue; }
                                if (HasOwner(targetJobs) && !isOwnerMem) { await WebSocketStore.SendAsync(ws, new { type = "user.delete.fail", message = "Only owner can modify owner" }); app.Logger.LogDebug($"WS user.delete.fail club={clubIdCur} reason=owner mem target={targetUsername}"); continue; }
                                var ownersCountMem = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubIdCur + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubIdCur.Length + 1)).Count(u => HasOwner(Store.GetJobsForUser(clubIdCur, u)));
                                if (HasOwner(targetJobs) && ownersCountMem <= 1) { await WebSocketStore.SendAsync(ws, new { type = "user.delete.fail", message = "Cannot remove last owner" }); app.Logger.LogDebug($"WS user.delete.fail club={clubIdCur} reason=lastowner mem target={targetUsername}"); continue; }
                                var jobKey = clubIdCur + "|" + targetUsername;
                                Store.ClubUserJobs.TryRemove(jobKey, out _);
                                await Persistence.SaveAsync();
                                var usersClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubIdCur + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubIdCur.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                                var users = usersClub.Select(u => new StaffUser { Username = u, Jobs = Store.GetJobsForUser(clubIdCur, u), Role = (Store.StaffUsers.TryGetValue(u, out var info2) ? info2.Role : "power"), CreatedAt = (Store.StaffUsers.TryGetValue(u, out var info3) ? info3.CreatedAt : DateTimeOffset.UtcNow), Uid = (Store.StaffUsers.TryGetValue(u, out var info4) ? info4.Uid : u), IsManual = (Store.StaffUsers.TryGetValue(u, out var info5) && info5 != null && info5.IsManual), IsOnline = IsUserOnline(u) }).ToArray();
                                await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "users.list", users = users.Select(x => x.Username).ToArray() });
                                await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "users.details", users });
                                var ownersOnlyUpdMem = users.Where(x => HasOwner(EnsureJobs(x.Jobs))).Select(x => x.Username).ToArray();
                                if (ownersOnlyUpdMem.Length == 1)
                                {
                                    var loneOwnerUpdMem = ownersOnlyUpdMem[0];
                                    Store.CreatedClubs[clubIdCur] = loneOwnerUpdMem;
                                    await Persistence.SaveAsync();
                                    await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "access.owner.changed", clubId = clubIdCur, owner = loneOwnerUpdMem });
                                }
                                var ownersOnlyMem = users.Where(x => HasOwner(EnsureJobs(x.Jobs))).Select(x => x.Username).ToArray();
                                if (ownersOnlyMem.Length == 1)
                                {
                                    var loneOwnerMem = ownersOnlyMem[0];
                                    Store.CreatedClubs[clubIdCur] = loneOwnerMem;
                                    await Persistence.SaveAsync();
                                    await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "access.owner.changed", clubId = clubIdCur, owner = loneOwnerMem });
                                }
                            }
                            else
                            {
                                userClubsAll = GetUserClubsFromStore(targetUsername);
                                for (int i = 0; i < userClubsAll.Length; i++)
                                {
                                    var clubId = userClubsAll[i];
                                    var jobKey = clubId + "|" + targetUsername;
                                    Store.ClubUserJobs.TryRemove(jobKey, out _);
                                }
                                Store.StaffUsers.TryRemove(targetUsername, out _);
                                var createdKeys = Store.CreatedClubs.Where(kv => string.Equals(kv.Value, targetUsername, StringComparison.Ordinal)).Select(kv => kv.Key).ToArray();
                                for (int i = 0; i < createdKeys.Length; i++) Store.CreatedClubs.TryRemove(createdKeys[i], out _);
                                await Persistence.SaveAsync();
                                for (int i = 0; i < userClubsAll.Length; i++)
                                {
                                    var clubId = userClubsAll[i];
                                    var usersClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubId + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubId.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                                    var users = usersClub.Select(u => new StaffUser { Username = u, Jobs = Store.GetJobsForUser(clubId, u), Role = (Store.StaffUsers.TryGetValue(u, out var info2) ? info2.Role : "power"), CreatedAt = (Store.StaffUsers.TryGetValue(u, out var info3) ? info3.CreatedAt : DateTimeOffset.UtcNow), Uid = (Store.StaffUsers.TryGetValue(u, out var info4) ? info4.Uid : u), IsManual = (Store.StaffUsers.TryGetValue(u, out var info5) && info5 != null && info5.IsManual), IsOnline = IsUserOnline(u) }).ToArray();
                                    await WebSocketStore.BroadcastToClubAsync(clubId, new { type = "users.list", users = users.Select(x => x.Username).ToArray() });
                                    await WebSocketStore.BroadcastToClubAsync(clubId, new { type = "users.details", users });
                                    var ownersOnlyMem = users.Where(x => HasOwner(EnsureJobs(x.Jobs))).Select(x => x.Username).ToArray();
                                    if (ownersOnlyMem.Length == 0)
                                    {
                                        var keys = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubId + "|", StringComparison.Ordinal)).ToArray();
                                        foreach (var k in keys) Store.ClubUserJobs.TryRemove(k, out _);
                                        Store.CreatedClubs.TryRemove(clubId, out _);
                                        Store.JobRights.Clear();
                                        Store.VipEntries.Clear();
                                        await Persistence.SaveAsync();
                                        await WebSocketStore.BroadcastToClubAsync(clubId, new { type = "club.deleted" });
                                        await WebSocketStore.BroadcastAsync(new { type = "membership.removed", username = string.Empty, clubId });
                                        continue;
                                    }
                                    if (ownersOnlyMem.Length == 1)
                                    {
                                        var loneOwnerMem = ownersOnlyMem[0];
                                        Store.CreatedClubs[clubId] = loneOwnerMem;
                                        await Persistence.SaveAsync();
                                        await WebSocketStore.BroadcastToClubAsync(clubId, new { type = "access.owner.changed", clubId, owner = loneOwnerMem });
                                    }
                                }
                            }
                        }
                        if (isSelf)
                        {
                            var tokensToRemove = Store.StaffSessions.Where(kv => string.Equals(kv.Value, targetUsername, StringComparison.Ordinal)).Select(kv => kv.Key).ToArray();
                            for (int i = 0; i < tokensToRemove.Length; i++)
                            {
                                var tokenToRemove = tokensToRemove[i];
                                Store.StaffSessions.TryRemove(tokenToRemove, out _);
                                Store.StaffSessionExpiry.TryRemove(tokenToRemove, out _);
                            }
                            var clubSet = new System.Collections.Generic.HashSet<string>(userClubsAll, StringComparer.Ordinal);
                            if (!string.IsNullOrWhiteSpace(clubIdCur)) clubSet.Add(clubIdCur);
                            foreach (var clubId in clubSet)
                            {
                                var sockets = WebSocketStore.GetSocketsForClub(clubId);
                                for (int i = 0; i < sockets.Length; i++)
                                {
                                    var sessions = WebSocketStore.GetSessions(sockets[i].Id);
                                    for (int j = 0; j < sessions.Length; j++)
                                    {
                                        if (!Store.StaffSessions.ContainsKey(sessions[j]))
                                        {
                                            WebSocketStore.RemoveSession(sockets[i].Id, sessions[j]);
                                        }
                                    }
                                }
                            }
                        }
                        if (isSelf && userClubsAll.Length > 0)
                        {
                            for (int i = 0; i < userClubsAll.Length; i++)
                            {
                                await WebSocketStore.BroadcastAsync(new { type = "membership.removed", username = targetUsername, clubId = userClubsAll[i] });
                            }
                        }
                        else
                        {
                            await WebSocketStore.BroadcastAsync(new { type = "membership.removed", username = targetUsername, clubId = clubIdCur });
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "user.delete.ok" });
                        app.Logger.LogDebug($"WS user.delete.ok club={clubIdCur} target={targetUsername}");
                        continue;
                    }
                    if (type == "user.manual.add")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var displayName = root.TryGetProperty("displayName", out var dn) ? (dn.GetString() ?? string.Empty) : string.Empty;
                        DateTimeOffset? birthday = null;
                        if (root.TryGetProperty("birthday", out var b) && b.ValueKind != JsonValueKind.Null)
                        {
                            if (b.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(b.GetString(), out var dt)) birthday = dt.ToUniversalTime();
                        }
                        var hasJobs = TryGetJobsFromPayload(root, out var jobsRaw);
                        var jobs = EnsureJobs(hasJobs ? jobsRaw : Array.Empty<string>());
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var ccU) && !string.IsNullOrWhiteSpace(ccU)) ? ccU! : "default";
                        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(displayName)) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.add.fail", message = "Missing token or display name" }); continue; }
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.add.fail", message = "Invalid session" }); app.Logger.LogDebug($"WS user.manual.add.fail club={clubIdCur} reason=session"); continue; }
                        bool isOwner;
                        bool canManage;
                        var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var actorJobs = await GetJobsFromDbAsync(efChk, clubIdCur, username);
                            var rightsDbChk = await efChk.GetJobRightsAsync(clubIdCur);
                            var actorRights = MergeRights(rightsDbChk, actorJobs);
                            isOwner = HasOwner(actorJobs);
                            canManage = actorRights.ManageUsers;
                            if (!(isOwner || canManage)) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.add.fail", message = "No rights" }); app.Logger.LogDebug($"WS user.manual.add.fail club={clubIdCur} reason=rights"); continue; }
                            if (HasOwner(jobs) && !isOwner) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.add.fail", message = "Only owner can assign owner" }); app.Logger.LogDebug($"WS user.manual.add.fail club={clubIdCur} reason=assignowner"); continue; }
                            var created = await efChk.CreateManualStaffEntryAsync(clubIdCur, displayName, jobs, birthday);
                            if (created == null) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.add.fail", message = "Already exists or invalid" }); app.Logger.LogDebug($"WS user.manual.add.fail club={clubIdCur} reason=exists"); continue; }
                            using var scopeEfWs = app.Services.CreateScope();
                            var efWs = scopeEfWs.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var list = await efWs.GetStaffUsersAsync(clubIdCur) ?? Array.Empty<StaffUserInfo>();
                            var rightsDb = await efWs.GetJobRightsAsync(clubIdCur);
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "users.list", users = list.Select(u => u.Username).OrderBy(u => u, StringComparer.Ordinal).ToArray() });
                            var sockets = WebSocketStore.GetSocketsForClub(clubIdCur);
                            var ordered = list.OrderBy(u => u.Username, StringComparer.Ordinal).ToArray();
                            for (int i = 0; i < sockets.Length; i++)
                            {
                                var viewer = ResolveViewerUsername(sockets[i].Id);
                                var users = ordered.Select(u => BuildStaffUserPayload(u, rightsDb, viewer)).ToArray();
                                await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
                            }
                        }
                        else
                        {
                            var actorJobs = GetJobsFromStore(clubIdCur, username);
                            var actorRights = MergeRights(rightsMap, actorJobs);
                            isOwner = HasOwner(actorJobs);
                            canManage = actorRights.ManageUsers;
                            if (!(isOwner || canManage)) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.add.fail", message = "No rights" }); app.Logger.LogDebug($"WS user.manual.add.fail club={clubIdCur} reason=rights mem"); continue; }
                            if (HasOwner(jobs) && !isOwner) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.add.fail", message = "Only owner can assign owner" }); app.Logger.LogDebug($"WS user.manual.add.fail club={clubIdCur} reason=assignowner mem"); continue; }
                            if (Store.StaffUsers.ContainsKey(displayName)) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.add.fail", message = "Already exists" }); app.Logger.LogDebug($"WS user.manual.add.fail club={clubIdCur} reason=exists mem"); continue; }
                            string uid;
                            do
                            {
                                uid = Util.NewUid();
                            } while (Store.StaffUsers.Values.Any(u => string.Equals(u.Uid, uid, StringComparison.Ordinal)));
                            var info = new StaffUserInfo { Username = displayName, PasswordHash = string.Empty, Jobs = EnsureJobs(jobs), Role = "power", CreatedAt = DateTimeOffset.UtcNow, Uid = uid, IsManual = true, Birthday = birthday };
                            Store.StaffUsers[displayName] = info;
                            Store.SetJobsForUser(clubIdCur, displayName, info.Jobs);
                            await Persistence.SaveAsync();
                            var usersClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubIdCur + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubIdCur.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "users.list", users = usersClub });
                            var sockets = WebSocketStore.GetSocketsForClub(clubIdCur);
                            for (int i = 0; i < sockets.Length; i++)
                            {
                                var viewer = ResolveViewerUsername(sockets[i].Id);
                                var users = usersClub.Select(u => BuildStaffUserPayloadFromStore(clubIdCur, u, rightsMap, viewer)).ToArray();
                                await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
                            }
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "user.manual.add.ok" });
                        app.Logger.LogDebug($"WS user.manual.add.ok club={clubIdCur} name={displayName}");
                        continue;
                    }
                    if (type == "user.manual.link")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var manualUid = root.TryGetProperty("manualUid", out var mu) ? (mu.GetString() ?? string.Empty) : string.Empty;
                        var targetUid = root.TryGetProperty("targetUid", out var tu) ? (tu.GetString() ?? string.Empty) : string.Empty;
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var ccU) && !string.IsNullOrWhiteSpace(ccU)) ? ccU! : "default";
                        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(manualUid) || string.IsNullOrWhiteSpace(targetUid)) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Missing token or uid" }); continue; }
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Invalid session" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=session"); continue; }
                        bool isOwner;
                        bool canManage;
                        var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var actorJobs = await GetJobsFromDbAsync(efChk, clubIdCur, username);
                            var rightsDbChk = await efChk.GetJobRightsAsync(clubIdCur);
                            var actorRights = MergeRights(rightsDbChk, actorJobs);
                            isOwner = HasOwner(actorJobs);
                            canManage = actorRights.ManageUsers;
                            if (!(isOwner || canManage)) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "No rights" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=rights"); continue; }
                            var manualInfo = await efChk.GetStaffUserByUidAsync(clubIdCur, manualUid);
                            if (manualInfo == null || !manualInfo.IsManual) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Manual entry not found" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=manualmissing"); continue; }
                            var manualJobs = EnsureJobs(manualInfo.Jobs);
                            if (HasOwner(manualJobs) && !isOwner) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Only owner can assign owner" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=assignowner"); continue; }
                            var targetInfo = await efChk.GetStaffUserByUidAsync(clubIdCur, targetUid);
                            var targetJobs = targetInfo != null ? EnsureJobs(targetInfo.Jobs) : Array.Empty<string>();
                            if (targetInfo != null && HasOwner(targetJobs) && !isOwner) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Only owner can modify owner" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=owner"); continue; }
                            if (targetInfo != null && HasOwner(targetJobs) && !HasOwner(manualJobs))
                            {
                                var listOwners = await efChk.GetStaffUsersAsync(clubIdCur) ?? Array.Empty<StaffUserInfo>();
                                var ownersCount = listOwners.Count(u => HasOwner(EnsureJobs(u.Jobs)));
                                if (ownersCount <= 1) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Cannot demote last owner" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=lastowner"); continue; }
                            }
                            if (!isOwner)
                            {
                                var actorRank = actorRights.Rank;
                                var newRank = MergeRights(rightsDbChk, manualJobs).Rank;
                                if (actorRank <= newRank) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Cannot assign role with equal or higher rank" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=newrank"); continue; }
                                var targetRank = MergeRights(rightsDbChk, EnsureJobs(targetJobs)).Rank;
                                if (actorRank <= targetRank) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Cannot modify equal or higher rank" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=targetrank"); continue; }
                            }
                            var linked = await efChk.LinkManualStaffEntryAsync(clubIdCur, manualUid, targetUid);
                            if (linked == null) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Link failed" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=linkfail"); continue; }
                            using var scopeEfWs = app.Services.CreateScope();
                            var efWs = scopeEfWs.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var list = await efWs.GetStaffUsersAsync(clubIdCur) ?? Array.Empty<StaffUserInfo>();
                            var rightsDb = await efWs.GetJobRightsAsync(clubIdCur);
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "users.list", users = list.Select(u => u.Username).OrderBy(u => u, StringComparer.Ordinal).ToArray() });
                            var sockets = WebSocketStore.GetSocketsForClub(clubIdCur);
                            var ordered = list.OrderBy(u => u.Username, StringComparer.Ordinal).ToArray();
                            for (int i = 0; i < sockets.Length; i++)
                            {
                                var viewer = ResolveViewerUsername(sockets[i].Id);
                                var users = ordered.Select(u => BuildStaffUserPayload(u, rightsDb, viewer)).ToArray();
                                await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
                            }
                        }
                        else
                        {
                            var actorJobs = GetJobsFromStore(clubIdCur, username);
                            var actorRights = MergeRights(rightsMap, actorJobs);
                            isOwner = HasOwner(actorJobs);
                            canManage = actorRights.ManageUsers;
                            if (!(isOwner || canManage)) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "No rights" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=rights mem"); continue; }
                            StaffUserInfo? manualInfo = null;
                            string manualName = string.Empty;
                            foreach (var kv in Store.StaffUsers)
                            {
                                if (kv.Value != null && kv.Value.IsManual && string.Equals(kv.Value.Uid, manualUid, StringComparison.Ordinal))
                                {
                                    manualInfo = kv.Value;
                                    manualName = kv.Key;
                                    break;
                                }
                            }
                            if (manualInfo == null) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Manual entry not found" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=manualmissing mem"); continue; }
                            var manualJobs = EnsureJobs(Store.GetJobsForUser(clubIdCur, manualName));
                            if (HasOwner(manualJobs) && !isOwner) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Only owner can assign owner" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=assignowner mem"); continue; }
                            StaffUserInfo? targetInfo = null;
                            string targetName = string.Empty;
                            foreach (var kv in Store.StaffUsers)
                            {
                                if (kv.Value != null && string.Equals(kv.Value.Uid, targetUid, StringComparison.Ordinal))
                                {
                                    targetInfo = kv.Value;
                                    targetName = kv.Key;
                                    break;
                                }
                            }
                            var targetJobs = string.IsNullOrWhiteSpace(targetName) ? Array.Empty<string>() : EnsureJobs(Store.GetJobsForUser(clubIdCur, targetName));
                            if (!string.IsNullOrWhiteSpace(targetName) && HasOwner(targetJobs) && !isOwner) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Only owner can modify owner" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=owner mem"); continue; }
                            if (!string.IsNullOrWhiteSpace(targetName) && HasOwner(targetJobs) && !HasOwner(manualJobs))
                            {
                                var ownersCountMem = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubIdCur + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubIdCur.Length + 1)).Count(u => HasOwner(Store.GetJobsForUser(clubIdCur, u)));
                                if (ownersCountMem <= 1) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Cannot demote last owner" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=lastowner mem"); continue; }
                            }
                            if (!isOwner)
                            {
                                var actorRank = actorRights.Rank;
                                var newRank = MergeRights(rightsMap, manualJobs).Rank;
                                if (actorRank <= newRank) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Cannot assign role with equal or higher rank" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=newrank mem"); continue; }
                                var targetRank = MergeRights(rightsMap, EnsureJobs(targetJobs)).Rank;
                                if (actorRank <= targetRank) { await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.fail", message = "Cannot modify equal or higher rank" }); app.Logger.LogDebug($"WS user.manual.link.fail club={clubIdCur} reason=targetrank mem"); continue; }
                            }
                            var finalTargetName = string.IsNullOrWhiteSpace(targetName) ? targetUid : targetName;
                            var info = new StaffUserInfo { Username = finalTargetName, PasswordHash = string.Empty, Jobs = EnsureJobs(manualJobs), Role = manualInfo.Role, CreatedAt = manualInfo.CreatedAt, Uid = targetUid, IsManual = false };
                            Store.StaffUsers[finalTargetName] = info;
                            Store.SetJobsForUser(clubIdCur, finalTargetName, info.Jobs);
                            if (!string.IsNullOrWhiteSpace(manualName))
                            {
                                Store.StaffUsers.TryRemove(manualName, out _);
                                Store.ClubUserJobs.TryRemove(clubIdCur + "|" + manualName, out _);
                            }
                            await Persistence.SaveAsync();
                            var usersClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubIdCur + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubIdCur.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "users.list", users = usersClub });
                            var sockets = WebSocketStore.GetSocketsForClub(clubIdCur);
                            for (int i = 0; i < sockets.Length; i++)
                            {
                                var viewer = ResolveViewerUsername(sockets[i].Id);
                                var users = usersClub.Select(u => BuildStaffUserPayloadFromStore(clubIdCur, u, rightsMap, viewer)).ToArray();
                                await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
                            }
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "user.manual.link.ok" });
                        app.Logger.LogDebug($"WS user.manual.link.ok club={clubIdCur} manualUid={manualUid} targetUid={targetUid}");
                        continue;
                    }
                    if (type == "user.update.request")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var targetUsername = root.TryGetProperty("username", out var tu) ? (tu.GetString() ?? string.Empty) : string.Empty;
                        var hasJobs = TryGetJobsFromPayload(root, out var newJobs);
                        var newJobLabel = hasJobs ? string.Join(",", newJobs) : string.Empty;
                        var newRole = root.TryGetProperty("role", out var r) ? (r.GetString() ?? string.Empty) : string.Empty;
                        var hasBirthday = root.TryGetProperty("birthday", out var b);
                        DateTimeOffset? newBirthday = null;
                        if (hasBirthday && b.ValueKind != JsonValueKind.Null)
                        {
                            if (b.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(b.GetString(), out var dt)) newBirthday = dt.ToUniversalTime();
                        }
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var ccU) && !string.IsNullOrWhiteSpace(ccU)) ? ccU! : "default";
                        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(targetUsername)) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Missing token or username" }); continue; }
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Invalid session" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=session target={targetUsername}"); continue; }
                        bool isOwner;
                        bool canManage;
                        var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var actorJobs = await GetJobsFromDbAsync(efChk, clubIdCur, username);
                            var rightsDbChk = await efChk.GetJobRightsAsync(clubIdCur);
                            var actorRights = MergeRights(rightsDbChk, actorJobs);
                            isOwner = HasOwner(actorJobs);
                            canManage = actorRights.ManageUsers;
                            var actorRank = actorRights.Rank;
                            if (!(isOwner || canManage)) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "No rights" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=rights target={targetUsername}"); continue; }
                            if (string.Equals(targetUsername, username, StringComparison.Ordinal) && hasJobs && !isOwner)
                            {
                                var newRightsSelf = MergeRights(rightsDbChk, newJobs);
                                var newRankSelf = newRightsSelf.Rank;
                                if (actorRank <= newRankSelf) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Cannot assign yourself a role of equal or higher rank" }); continue; }
                                if (!newRightsSelf.ManageJobs) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Cannot assign yourself a role that removes ManageJobs" }); continue; }
                            }
                            var targetJobs = await GetJobsFromDbAsync(efChk, clubIdCur, targetUsername);
                            var targetRights = MergeRights(rightsDbChk, targetJobs);
                            var targetRank = targetRights.Rank;
                            if (!isOwner && actorRank <= targetRank) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Cannot modify equal or higher rank" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=rank target={targetUsername}"); continue; }
                            if (HasOwner(targetJobs) && !isOwner) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Only owner can modify owner" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=owner target={targetUsername}"); continue; }
                            if (hasJobs && HasOwner(newJobs) && !isOwner) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Only owner can assign owner" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=assignowner target={targetUsername}"); continue; }
                            if (hasJobs && HasOwner(targetJobs) && !HasOwner(newJobs))
                            {
                                var listOwners = await efChk.GetStaffUsersAsync(clubIdCur) ?? Array.Empty<StaffUserInfo>();
                                var ownersCount = listOwners.Count(u => HasOwner(EnsureJobs(u.Jobs)));
                                if (ownersCount <= 1) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Cannot demote last owner" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=lastowner target={targetUsername}"); continue; }
                            }
                            if (hasJobs && !isOwner)
                            {
                                var newRank = MergeRights(rightsDbChk, newJobs).Rank;
                                if (actorRank <= newRank) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Cannot assign role with equal or higher rank" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=newrank target={targetUsername} job={newJobLabel}"); continue; }
                            }
                            if (hasBirthday)
                            {
                                var manualInfo = await efChk.GetManualStaffUserAsync(clubIdCur, targetUsername);
                                if (manualInfo == null || !manualInfo.IsManual) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Birthday update only for manual entries" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=birthdaymanual target={targetUsername}"); continue; }
                                await efChk.UpdateManualStaffBirthdayAsync(clubIdCur, targetUsername, newBirthday);
                            }
                            if (hasJobs) await efChk.UpdateStaffUserJobsAsync(clubIdCur, targetUsername, newJobs);
                            if (!string.IsNullOrWhiteSpace(newRole)) await efChk.UpdateStaffUserRoleAsync(clubIdCur, targetUsername, newRole);
                            using var scopeEfWs = app.Services.CreateScope();
                            var efWs = scopeEfWs.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var list = await efWs.GetStaffUsersAsync(clubIdCur) ?? Array.Empty<StaffUserInfo>();
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "users.list", users = list.Select(u => u.Username).OrderBy(u => u, StringComparer.Ordinal).ToArray() });
                            var sockets = WebSocketStore.GetSocketsForClub(clubIdCur);
                            var ordered = list.OrderBy(u => u.Username, StringComparer.Ordinal).ToArray();
                            for (int i = 0; i < sockets.Length; i++)
                            {
                                var viewer = ResolveViewerUsername(sockets[i].Id);
                                var users = ordered.Select(u => BuildStaffUserPayload(u, rightsDbChk, viewer)).ToArray();
                                await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
                            }
                            var ownersOnlyUpdReq = list.Where(u => HasOwner(EnsureJobs(u.Jobs))).Select(u => u.Username).ToArray();
                            if (ownersOnlyUpdReq.Length == 1)
                            {
                                var loneOwnerUpdReq = ownersOnlyUpdReq[0];
                                try { await efWs.SetClubCreatorAsync(clubIdCur, loneOwnerUpdReq); } catch { }
                                Store.CreatedClubs[clubIdCur] = loneOwnerUpdReq;
                                await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "access.owner.changed", clubId = clubIdCur, owner = loneOwnerUpdReq });
                            }
                            rightsMap = rightsDbChk.ToDictionary(kv => kv.Key, kv => kv.Value);
                        }
                        else
                        {
                            var existingJobs = GetJobsFromStore(clubIdCur, targetUsername);
                            if (existingJobs.Length == 0) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "User not in club" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=notmember target={targetUsername}"); continue; }
                            var actorJobs = GetJobsFromStore(clubIdCur, username);
                            var actorRightsMem = MergeRights(rightsMap, actorJobs);
                            var canManageMem = actorRightsMem.ManageUsers;
                            var actorRankMem = actorRightsMem.Rank;
                            var isOwnerMem = HasOwner(actorJobs);
                            if (!(isOwnerMem || canManageMem)) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "No rights" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=rights mem target={targetUsername}"); continue; }
                            var isSelfMem = string.Equals(targetUsername, username, StringComparison.Ordinal);
                            if (isSelfMem && hasJobs && !isOwnerMem)
                            {
                                var newRightsSelfMem = MergeRights(rightsMap, newJobs);
                                var newRankSelfMem = newRightsSelfMem.Rank;
                                if (actorRankMem <= newRankSelfMem) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Cannot assign yourself a role of equal or higher rank" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=selfrank target={targetUsername} job={newJobLabel}"); continue; }
                                if (!newRightsSelfMem.ManageJobs) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Cannot assign yourself a role that removes ManageJobs" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=selfmanage target={targetUsername} job={newJobLabel}"); continue; }
                            }
                            var targetRightsMem = MergeRights(rightsMap, existingJobs);
                            var targetRankMem = targetRightsMem.Rank;
                            if (!isOwnerMem && actorRankMem <= targetRankMem) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Cannot modify equal or higher rank" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=rank mem target={targetUsername}"); continue; }
                            if (HasOwner(existingJobs) && !isOwnerMem) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Only owner can modify owner" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=owner mem target={targetUsername}"); continue; }
                            if (hasJobs && HasOwner(newJobs) && !isOwnerMem) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Only owner can assign owner" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=assignowner mem target={targetUsername}"); continue; }
                            if (hasJobs && HasOwner(existingJobs) && !HasOwner(newJobs))
                            {
                                var ownersCountMem = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubIdCur + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubIdCur.Length + 1)).Count(u => HasOwner(Store.GetJobsForUser(clubIdCur, u)));
                                if (ownersCountMem <= 1) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Cannot demote last owner" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=lastowner mem target={targetUsername}"); continue; }
                            }
                            if (hasJobs && !isOwnerMem)
                            {
                                var newRankMem = MergeRights(rightsMap, newJobs).Rank;
                                if (actorRankMem <= newRankMem) { await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Cannot assign role with equal or higher rank" }); app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=newrank mem target={targetUsername} job={newJobLabel}"); continue; }
                            }
                            if (hasBirthday)
                            {
                                if (!Store.StaffUsers.TryGetValue(targetUsername, out var infoMem) || infoMem == null || !infoMem.IsManual)
                                {
                                    await WebSocketStore.SendAsync(ws, new { type = "user.update.fail", message = "Birthday update only for manual entries" });
                                    app.Logger.LogDebug($"WS user.update.fail club={clubIdCur} reason=birthdaymanual mem target={targetUsername}");
                                    continue;
                                }
                                infoMem.Birthday = newBirthday;
                                Store.StaffUsers[targetUsername] = infoMem;
                            }
                            if (hasJobs) Store.SetJobsForUser(clubIdCur, targetUsername, newJobs);
                            if (Store.StaffUsers.TryGetValue(targetUsername, out var info))
                            {
                                if (!string.IsNullOrWhiteSpace(newRole)) info.Role = newRole;
                                if (hasJobs) info.Jobs = EnsureJobs(newJobs);
                                Store.StaffUsers[targetUsername] = info;
                            }
                            await Persistence.SaveAsync();
                            var usersClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubIdCur + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubIdCur.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "users.list", users = usersClub });
                            var sockets = WebSocketStore.GetSocketsForClub(clubIdCur);
                            for (int i = 0; i < sockets.Length; i++)
                            {
                                var viewer = ResolveViewerUsername(sockets[i].Id);
                                var users = usersClub.Select(u => BuildStaffUserPayloadFromStore(clubIdCur, u, rightsMap, viewer)).ToArray();
                                await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
                            }
                        }
                        var broadcastJobs = hasJobs ? EnsureJobs(newJobs) : GetJobsFromStore(clubIdCur, targetUsername);
                        var primary = GetPrimaryJob(rightsMap, EnsureJobs(broadcastJobs));
                        await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "user.update", username = targetUsername, jobs = broadcastJobs, job = primary });
                        await WebSocketStore.SendAsync(ws, new { type = "user.update.ok" });
                        app.Logger.LogDebug($"WS user.update.ok club={clubIdCur} target={targetUsername} job={primary} role={newRole}");
                        continue;
                    }
                    if (type == "job.add" || type == "job.delete")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(name)) { await WebSocketStore.SendAsync(ws, new { type = type == "job.add" ? "job.add.fail" : "job.delete.fail" }); continue; }
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = type == "job.add" ? "job.add.fail" : "job.delete.fail" }); continue; }
                        if (string.Equals(name, "Owner", StringComparison.Ordinal)) { await WebSocketStore.SendAsync(ws, new { type = type == "job.add" ? "job.add.fail" : "job.delete.fail" }); continue; }
                        bool isOwner;
                        bool canManageJobs;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var rightsDbChk = await efChk.GetJobRightsAsync(clubIdCur);
                            var jobsChk = await GetJobsFromDbAsync(efChk, clubIdCur, username);
                            var rightsChk = MergeRights(rightsDbChk, jobsChk);
                            isOwner = HasOwner(jobsChk);
                            canManageJobs = rightsChk.ManageJobs;
                            if (!(isOwner || canManageJobs)) { await WebSocketStore.SendAsync(ws, new { type = type == "job.add" ? "job.add.fail" : "job.delete.fail" }); continue; }
                        }
                        else
                        {
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            var jobsChk = GetJobsFromStore(clubIdCur, username);
                            var rightsChk = MergeRights(rightsMap, jobsChk);
                            isOwner = HasOwner(jobsChk);
                            canManageJobs = rightsChk.ManageJobs;
                            if (!(isOwner || canManageJobs)) { await WebSocketStore.SendAsync(ws, new { type = type == "job.add" ? "job.add.fail" : "job.delete.fail" }); continue; }
                        }
                        if (type == "job.add")
                        {
                            Store.JobRights[name] = Store.JobRights.TryGetValue(name, out var existing) ? existing : new Rights();
                            if (!string.IsNullOrWhiteSpace(conn))
                            {
                                using var scopeEf = app.Services.CreateScope();
                                var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                await efSvc.AddJobAsync(clubIdCur, name);
                            }
                            else
                            {
                                await Persistence.SaveAsync();
                            }
                        }
                        else
                        {
                            Store.JobRights.TryRemove(name, out _);
                            if (!string.IsNullOrWhiteSpace(conn))
                            {
                                using var scopeEf = app.Services.CreateScope();
                                var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                await efSvc.DeleteJobAsync(clubIdCur, name);
                            }
                            else
                            {
                                await Persistence.SaveAsync();
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfWs = app.Services.CreateScope();
                            var efSvcWs = scopeEfWs.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var jobsDb = await efSvcWs.GetJobsAsync(clubIdCur) ?? Array.Empty<string>();
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "jobs.list", jobs = jobsDb });
                        }
                        else
                        {
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "jobs.list", jobs = Store.JobRights.Keys.OrderBy(j => j, StringComparer.Ordinal).ToArray() });
                        }
                        await WebSocketStore.SendAsync(ws, new { type = type == "job.add" ? "job.add.ok" : "job.delete.ok" });
                        continue;
                    }
                    if (type == "vip.homeworld.update")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var characterName = root.TryGetProperty("characterName", out var cn) ? (cn.GetString() ?? string.Empty) : string.Empty;
                        var oldHomeWorld = root.TryGetProperty("oldHomeWorld", out var ohw) ? (ohw.GetString() ?? string.Empty) : string.Empty;
                        var newHomeWorld = root.TryGetProperty("newHomeWorld", out var nhw) ? (nhw.GetString() ?? string.Empty) : string.Empty;
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(oldHomeWorld) || string.IsNullOrWhiteSpace(newHomeWorld))
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "vip.update.fail" });
                            app.Logger.LogDebug($"WS vip.update.fail club={clubIdCur} reason=invalid op=homeworld");
                            continue;
                        }
                        if (!Util.ValidateSession(token, out var username))
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "vip.update.fail" });
                            app.Logger.LogDebug($"WS vip.update.fail club={clubIdCur} reason=session op=homeworld");
                            continue;
                        }
                        if (string.Equals(oldHomeWorld, newHomeWorld, StringComparison.Ordinal))
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "vip.update.ok" });
                            continue;
                        }
                        string[] jobs;
                        Rights rights;
                        bool isOwner;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfRights = app.Services.CreateScope();
                            var efSvcRights = scopeEfRights.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var rightsDb = await efSvcRights.GetJobRightsAsync(clubIdCur);
                            jobs = await GetJobsFromDbAsync(efSvcRights, clubIdCur, username);
                            rights = MergeRights(rightsDb, jobs);
                            isOwner = HasOwner(jobs);
                        }
                        else
                        {
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            jobs = GetJobsFromStore(clubIdCur, username);
                            rights = MergeRights(rightsMap, jobs);
                            isOwner = HasOwner(jobs);
                        }
                        if (!(rights.EditVipHomeWorld || isOwner))
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "vip.update.fail" });
                            app.Logger.LogDebug($"WS vip.update.fail club={clubIdCur} reason=rights op=homeworld name={characterName}@{oldHomeWorld}");
                            continue;
                        }
                        VipEntry? existing = null;
                        bool newExists = false;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var list = await efSvc.LoadVipEntriesAsync(clubIdCur) ?? Array.Empty<VipEntry>();
                            for (int i = 0; i < list.Length; i++)
                            {
                                var entry = list[i];
                                if (string.Equals(entry.CharacterName, characterName, StringComparison.Ordinal) && string.Equals(entry.HomeWorld, oldHomeWorld, StringComparison.Ordinal))
                                {
                                    existing = entry;
                                    break;
                                }
                            }
                            newExists = await efSvc.ExistsVipAsync(clubIdCur, characterName, newHomeWorld);
                        }
                        else
                        {
                            var oldKey = characterName + "@" + oldHomeWorld;
                            if (Store.VipEntries.TryGetValue(oldKey, out var entry)) existing = entry;
                            var newKey = characterName + "@" + newHomeWorld;
                            newExists = Store.VipEntries.ContainsKey(newKey);
                        }
                        if (existing == null)
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "vip.update.fail" });
                            app.Logger.LogDebug($"WS vip.update.fail club={clubIdCur} reason=missing op=homeworld name={characterName}@{oldHomeWorld}");
                            continue;
                        }
                        if (newExists)
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "vip.update.fail" });
                            app.Logger.LogDebug($"WS vip.update.fail club={clubIdCur} reason=exists op=homeworld name={characterName}@{newHomeWorld}");
                            continue;
                        }
                        var updated = new VipEntry
                        {
                            CharacterName = existing.CharacterName,
                            HomeWorld = newHomeWorld,
                            CreatedAt = existing.CreatedAt,
                            ExpiresAt = existing.ExpiresAt,
                            Duration = existing.Duration
                        };
                        Store.VipEntries.TryRemove(existing.Key, out _);
                        Store.VipEntries[updated.Key] = updated;
                        if (Store.ClubVipKeys.TryGetValue(clubIdCur, out var setVipClub))
                        {
                            setVipClub.TryRemove(existing.Key, out _);
                            setVipClub[updated.Key] = 1;
                        }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            await efSvc.PersistRemoveVipAsync(clubIdCur, existing.CharacterName, existing.HomeWorld);
                            await efSvc.PersistAddVipAsync(clubIdCur, updated);
                        }
                        if (string.IsNullOrWhiteSpace(conn)) await Persistence.SaveAsync();
                        await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { op = "remove", entry = existing });
                        await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { op = "add", entry = updated });
                        await WebSocketStore.SendAsync(ws, new { type = "vip.update.ok" });
                        app.Logger.LogDebug($"WS vip.update.ok club={clubIdCur} op=homeworld name={characterName}@{oldHomeWorld}->{newHomeWorld}");
                        continue;
                    }
                    if (type == "vip.add" || type == "vip.remove")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var entryEl = root.TryGetProperty("entry", out var e) ? e : default;
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (entryEl.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(token)) { await WebSocketStore.SendAsync(ws, new { type = "vip.update.fail" }); app.Logger.LogDebug($"WS vip.update.fail club={clubIdCur} reason=invalid"); continue; }
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "vip.update.fail" }); app.Logger.LogDebug($"WS vip.update.fail club={clubIdCur} reason=session"); continue; }
                        var entry = JsonSerializer.Deserialize<VipEntry>(entryEl.GetRawText(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                        if (entry == null) { await WebSocketStore.SendAsync(ws, new { type = "vip.update.fail" }); app.Logger.LogDebug($"WS vip.update.fail club={clubIdCur} reason=entrynull"); continue; }
                        string[] jobs;
                        Rights rights;
                        bool isOwner;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfRights = app.Services.CreateScope();
                            var efSvcRights = scopeEfRights.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var rightsDb = await efSvcRights.GetJobRightsAsync(clubIdCur);
                            jobs = await GetJobsFromDbAsync(efSvcRights, clubIdCur, username);
                            rights = MergeRights(rightsDb, jobs);
                            isOwner = HasOwner(jobs);
                        }
                        else
                        {
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            jobs = GetJobsFromStore(clubIdCur, username);
                            rights = MergeRights(rightsMap, jobs);
                            isOwner = HasOwner(jobs);
                        }
                        if (type == "vip.add")
                        {
                            bool existsCur;
                            if (!string.IsNullOrWhiteSpace(conn))
                            {
                                using var scopeEfExist = app.Services.CreateScope();
                                var efExist = scopeEfExist.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                existsCur = await efExist.ExistsVipAsync(clubIdCur, entry.CharacterName, entry.HomeWorld);
                            }
                            else
                            {
                                existsCur = Store.VipEntries.ContainsKey(entry.Key);
                            }
                            if (existsCur)
                            {
                                if (!(rights.EditVipDuration || isOwner)) { await WebSocketStore.SendAsync(ws, new { type = "vip.update.fail" }); app.Logger.LogDebug($"WS vip.update.fail club={clubIdCur} reason=rights op=edit name={entry.CharacterName}@{entry.HomeWorld}"); continue; }
                            }
                            else
                            {
                                if (!(rights.AddVip || isOwner)) { await WebSocketStore.SendAsync(ws, new { type = "vip.update.fail" }); app.Logger.LogDebug($"WS vip.update.fail club={clubIdCur} reason=rights op=add name={entry.CharacterName}@{entry.HomeWorld}"); continue; }
                            }
                        }
                        if (type == "vip.remove" && !(rights.RemoveVip || isOwner)) { await WebSocketStore.SendAsync(ws, new { type = "vip.update.fail" }); app.Logger.LogDebug($"WS vip.update.fail club={clubIdCur} reason=rights op=remove name={entry.CharacterName}@{entry.HomeWorld}"); continue; }
                        if (type == "vip.add")
                        {
                            Store.VipEntries[entry.Key] = entry;
                            var setVipClub = Store.ClubVipKeys.GetOrAdd(clubIdCur, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                            setVipClub[entry.Key] = 1;
                            if (!string.IsNullOrWhiteSpace(conn))
                            {
                                using var scopeEf = app.Services.CreateScope();
                                var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                await efSvc.PersistAddVipAsync(clubIdCur, entry);
                            }
                        }
                        else
                        {
                            Store.VipEntries.TryRemove(entry.Key, out _);
                            if (Store.ClubVipKeys.TryGetValue(clubIdCur, out var setVipClubRem)) { setVipClubRem.TryRemove(entry.Key, out _); }
                            if (!string.IsNullOrWhiteSpace(conn))
                            {
                                using var scopeEf = app.Services.CreateScope();
                                var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                await efSvc.PersistRemoveVipAsync(clubIdCur, entry.CharacterName, entry.HomeWorld);
                            }
                        }
                        if (string.IsNullOrWhiteSpace(conn)) await Persistence.SaveAsync();
                        await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { op = type == "vip.add" ? "add" : "remove", entry });
                        await WebSocketStore.SendAsync(ws, new { type = "vip.update.ok" });
                        app.Logger.LogDebug($"WS vip.update.ok club={clubIdCur} op={(type == "vip.add" ? "add" : "remove")} name={entry.CharacterName}@{entry.HomeWorld}");
                        continue;
                    }
                    if (type == "vip.purge.expired")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (string.IsNullOrWhiteSpace(token)) { await WebSocketStore.SendAsync(ws, new { type = "vip.purge.expired.fail", code = 400, message = "Missing session" }); continue; }
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "vip.purge.expired.fail", code = 401, message = "Invalid session" }); continue; }
                        string[] jobs;
                        Rights rights;
                        bool isOwner;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfRights = app.Services.CreateScope();
                            var efSvcRights = scopeEfRights.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var rightsDb = await efSvcRights.GetJobRightsAsync(clubIdCur);
                            jobs = await GetJobsFromDbAsync(efSvcRights, clubIdCur, username);
                            rights = MergeRights(rightsDb, jobs);
                            isOwner = HasOwner(jobs);
                        }
                        else
                        {
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            jobs = GetJobsFromStore(clubIdCur, username);
                            rights = MergeRights(rightsMap, jobs);
                            isOwner = HasOwner(jobs);
                        }
                        if (!(rights.RemoveVip || isOwner)) { await WebSocketStore.SendAsync(ws, new { type = "vip.purge.expired.fail", code = 403, message = "No rights" }); continue; }
                        var now = DateTimeOffset.UtcNow;
                        var expired = new List<VipEntry>();
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var entries = await efSvc.LoadVipEntriesAsync(clubIdCur) ?? Array.Empty<VipEntry>();
                            for (int i = 0; i < entries.Length; i++)
                            {
                                var entry = entries[i];
                                if (entry.Duration == 2) continue;
                                if (!entry.ExpiresAt.HasValue) continue;
                                if (entry.ExpiresAt.Value <= now) expired.Add(entry);
                            }
                            for (int i = 0; i < expired.Count; i++)
                            {
                                var entry = expired[i];
                                await efSvc.PersistRemoveVipAsync(clubIdCur, entry.CharacterName, entry.HomeWorld);
                            }
                        }
                        else
                        {
                            if (Store.ClubVipKeys.TryGetValue(clubIdCur, out var keys))
                            {
                                foreach (var key in keys.Keys)
                                {
                                    if (Store.VipEntries.TryGetValue(key, out var entry))
                                    {
                                        if (entry.Duration == 2) continue;
                                        if (!entry.ExpiresAt.HasValue) continue;
                                        if (entry.ExpiresAt.Value <= now) expired.Add(entry);
                                    }
                                }
                            }
                            for (int i = 0; i < expired.Count; i++)
                            {
                                var entry = expired[i];
                                Store.VipEntries.TryRemove(entry.Key, out _);
                                if (Store.ClubVipKeys.TryGetValue(clubIdCur, out var setVipClubRem)) { setVipClubRem.TryRemove(entry.Key, out _); }
                            }
                            await Persistence.SaveAsync();
                        }
                        for (int i = 0; i < expired.Count; i++)
                        {
                            var entry = expired[i];
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { op = "remove", entry });
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "vip.purge.expired.ok", count = expired.Count });
                        app.Logger.LogDebug($"WS vip.purge.expired.ok club={clubIdCur} removed={expired.Count}");
                        continue;
                    }
                    if (type == "dj.add" || type == "dj.remove")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var entryEl = root.TryGetProperty("entry", out var e) ? e : default;
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (entryEl.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(token)) { await WebSocketStore.SendAsync(ws, new { type = "dj.update.fail" }); continue; }
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "dj.update.fail" }); continue; }
                        var entry = JsonSerializer.Deserialize<DjEntry>(entryEl.GetRawText(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                        if (entry == null || string.IsNullOrWhiteSpace(entry.DjName)) { await WebSocketStore.SendAsync(ws, new { type = "dj.update.fail" }); continue; }
                        string[] jobs;
                        Rights rights;
                        bool isOwner;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfRights = app.Services.CreateScope();
                            var efSvcRights = scopeEfRights.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var rightsDb = await efSvcRights.GetJobRightsAsync(clubIdCur);
                            jobs = await GetJobsFromDbAsync(efSvcRights, clubIdCur, username);
                            rights = MergeRights(rightsDb, jobs);
                            isOwner = HasOwner(jobs);
                        }
                        else
                        {
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            jobs = GetJobsFromStore(clubIdCur, username);
                            rights = MergeRights(rightsMap, jobs);
                            isOwner = HasOwner(jobs);
                        }
                        var canAdd = rights.AddDj || isOwner;
                        var canRemove = rights.RemoveDj || isOwner;
                        var allowed = type == "dj.add" ? canAdd : canRemove;
                        if (!allowed) { await WebSocketStore.SendAsync(ws, new { type = "dj.update.fail" }); app.Logger.LogDebug($"WS dj.update.fail club={clubIdCur} reason=rights op={(type == "dj.add" ? "add" : "remove")} dj={entry.DjName}"); continue; }
                        if (type == "dj.add")
                        {
                            var twLink = entry.TwitchLink ?? string.Empty;
                            var sLink = twLink.Trim();
                            if (!string.IsNullOrWhiteSpace(sLink))
                            {
                                var lower = sLink.ToLowerInvariant();
                                if (lower.StartsWith("http://")) sLink = "https://" + sLink.Substring(7);
                                lower = sLink.ToLowerInvariant();
                                if (lower.StartsWith("https://www.twitch.tv/")) sLink = "https://twitch.tv/" + sLink.Substring("https://www.twitch.tv/".Length);
                                else if (lower.StartsWith("www.twitch.tv/")) sLink = "https://twitch.tv/" + sLink.Substring("www.twitch.tv/".Length);
                                else if (lower.StartsWith("twitch.tv/")) sLink = "https://" + sLink;
                                else if (!lower.Contains("twitch.tv"))
                                {
                                    var channel = sLink.TrimStart('@').Trim('/').ToLowerInvariant();
                                    if (!string.IsNullOrWhiteSpace(channel)) sLink = "https://twitch.tv/" + channel;
                                }
                                if (sLink.EndsWith("/")) sLink = sLink.Substring(0, sLink.Length - 1);
                                entry.TwitchLink = sLink;
                            }
                        }
                        if (type == "dj.add")
                        {
                            Store.DjEntries[clubIdCur + "|" + entry.DjName] = entry;
                            if (!string.IsNullOrWhiteSpace(conn))
                            {
                                using var scopeEf = app.Services.CreateScope();
                                var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                await efSvc.PersistAddOrUpdateDjAsync(clubIdCur, entry);
                            }
                        }
                        else
                        {
                            Store.DjEntries.TryRemove(clubIdCur + "|" + entry.DjName, out _);
                            if (!string.IsNullOrWhiteSpace(conn))
                            {
                                using var scopeEf = app.Services.CreateScope();
                                var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                await efSvc.PersistRemoveDjAsync(clubIdCur, entry.DjName);
                            }
                        }
                        await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "dj.update", op = (type == "dj.add" ? "add" : "remove"), entry });
                        await WebSocketStore.SendAsync(ws, new { type = "dj.update.ok" });
                        app.Logger.LogDebug($"WS dj.update.ok club={clubIdCur} op={(type == "dj.add" ? "add" : "remove")} dj={entry.DjName}");
                        continue;
                    }
                    if (type == "shift.add" || type == "shift.update" || type == "shift.remove")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var entryEl = root.TryGetProperty("entry", out var e) ? e : default;
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if ((type == "shift.remove" && !root.TryGetProperty("id", out var idEl)) && entryEl.ValueKind != JsonValueKind.Object) { await WebSocketStore.SendAsync(ws, new { type = "shift.update.fail" }); continue; }
                        if (string.IsNullOrWhiteSpace(token)) { await WebSocketStore.SendAsync(ws, new { type = "shift.update.fail" }); continue; }
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "shift.update.fail" }); continue; }
                        ShiftEntry? entry = null;
                        if (entryEl.ValueKind == JsonValueKind.Object)
                        {
                            entry = JsonSerializer.Deserialize<ShiftEntry>(entryEl.GetRawText(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                        }
                        Guid idRem = Guid.Empty;
                        if (type == "shift.remove" && root.TryGetProperty("id", out var idEl2))
                        {
                            var idStr = idEl2.GetString() ?? string.Empty;
                            Guid.TryParse(idStr, out idRem);
                        }
                        string[] jobs;
                        Rights rights;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfRights = app.Services.CreateScope();
                            var efSvcRights = scopeEfRights.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var rightsDb = await efSvcRights.GetJobRightsAsync(clubIdCur);
                            jobs = await GetJobsFromDbAsync(efSvcRights, clubIdCur, username);
                            rights = MergeRights(rightsDb, jobs);
                        }
                        else
                        {
                            jobs = GetJobsFromStore(clubIdCur, username);
                            rights = MergeRights(Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value), jobs);
                        }
                        bool isOwner = HasOwner(jobs);
                        var allowed = rights.EditShiftPlan || isOwner;
                        if (!allowed) { await WebSocketStore.SendAsync(ws, new { type = "shift.update.fail" }); app.Logger.LogDebug($"WS shift.update.fail club={clubIdCur} reason=rights op={type} id={(entry?.Id.ToString() ?? idRem.ToString())}"); continue; }
                        if (type == "shift.add" || type == "shift.update")
                        {
                            if (entry == null) { await WebSocketStore.SendAsync(ws, new { type = "shift.update.fail" }); continue; }
                            if (!string.IsNullOrWhiteSpace(conn))
                            {
                                using var scopeEf = app.Services.CreateScope();
                                var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                entry = await efSvc.PersistAddOrUpdateShiftAsync(clubIdCur, entry);
                            }
                            else
                            {
                                var key = clubIdCur + "|" + (entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id);
                                if (entry.Id == Guid.Empty) entry.Id = Guid.Parse(key.Substring(clubIdCur.Length + 1));
                                Store.ShiftEntries[key] = entry;
                                await Persistence.SaveAsync();
                            }
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "shift.update", op = (type == "shift.add" ? "add" : "update"), entry });
                            await WebSocketStore.SendAsync(ws, new { type = "shift.update.ok" });
                            app.Logger.LogDebug($"WS shift.update.ok club={clubIdCur} op={(type == "shift.add" ? "add" : "update")} id={entry.Id}");
                            continue;
                        }
                        else if (type == "shift.remove")
                        {
                            if (!string.IsNullOrWhiteSpace(conn))
                            {
                                using var scopeEf = app.Services.CreateScope();
                                var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                await efSvc.PersistRemoveShiftAsync(clubIdCur, idRem);
                            }
                            else
                            {
                                var key = clubIdCur + "|" + idRem.ToString();
                                Store.ShiftEntries.TryRemove(key, out _);
                                await Persistence.SaveAsync();
                            }
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "shift.update", op = "remove", id = idRem });
                            await WebSocketStore.SendAsync(ws, new { type = "shift.update.ok" });
                            app.Logger.LogDebug($"WS shift.update.ok club={clubIdCur} op=remove id={idRem}");
                            continue;
                        }
                    }
                    if (type == "club.logo.request")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var clubIdReq = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "club.logo", clubId = clubIdReq, logoBase64 = string.Empty }); continue; }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfWs = app.Services.CreateScope();
                            var efSvcWs = scopeEfWs.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var infoDb = await efSvcWs.GetStaffUserAsync(clubIdReq, username);
                            var isMember = infoDb != null;
                            if (!isMember) { await WebSocketStore.SendAsync(ws, new { type = "club.logo", clubId = clubIdReq, logoBase64 = string.Empty }); continue; }
                            var logo = await efSvcWs.GetClubLogoAsync(clubIdReq);
                            await WebSocketStore.SendAsync(ws, new { type = "club.logo", clubId = clubIdReq, logoBase64 = logo ?? string.Empty });
                        }
                        else
                        {
                            var isMember = Store.ClubUserJobs.ContainsKey(clubIdReq + "|" + username);
                            if (!isMember) { await WebSocketStore.SendAsync(ws, new { type = "club.logo", clubId = clubIdReq, logoBase64 = string.Empty }); continue; }
                            var logo = Store.ClubLogos.TryGetValue(clubIdReq, out var l) ? l : null;
                            await WebSocketStore.SendAsync(ws, new { type = "club.logo", clubId = clubIdReq, logoBase64 = logo ?? string.Empty });
                        }
                        continue;
                    }
                    if (type == "club.logo.for.request")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var clubIdReq = root.TryGetProperty("clubId", out var c) ? (c.GetString() ?? "default") : "default";
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "club.logo", clubId = clubIdReq, logoBase64 = string.Empty }); continue; }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfWs = app.Services.CreateScope();
                            var efSvcWs = scopeEfWs.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var infoDb = await efSvcWs.GetStaffUserAsync(clubIdReq, username);
                            var isMember = infoDb != null;
                            if (!isMember) { await WebSocketStore.SendAsync(ws, new { type = "club.logo", clubId = clubIdReq, logoBase64 = string.Empty }); continue; }
                            var logo = await efSvcWs.GetClubLogoAsync(clubIdReq);
                            await WebSocketStore.SendAsync(ws, new { type = "club.logo", clubId = clubIdReq, logoBase64 = logo ?? string.Empty });
                        }
                        else
                        {
                            var isMember = Store.ClubUserJobs.ContainsKey(clubIdReq + "|" + username);
                            if (!isMember) { await WebSocketStore.SendAsync(ws, new { type = "club.logo", clubId = clubIdReq, logoBase64 = string.Empty }); continue; }
                            var logo = Store.ClubLogos.TryGetValue(clubIdReq, out var l) ? l : null;
                            await WebSocketStore.SendAsync(ws, new { type = "club.logo", clubId = clubIdReq, logoBase64 = logo ?? string.Empty });
                        }
                        continue;
                    }
                    if (type == "club.logo.update")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var raw = root.TryGetProperty("logoBase64", out var l) ? (l.GetString() ?? string.Empty) : string.Empty;
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "club.logo.update.fail" }); continue; }
                        bool isOwner;
                        Rights rights;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var jobsChk = await GetJobsFromDbAsync(efChk, clubIdCur, username);
                            var rightsDb = await efChk.GetJobRightsAsync(clubIdCur);
                            rights = MergeRights(rightsDb, jobsChk);
                            isOwner = HasOwner(jobsChk);
                        }
                        else
                        {
                            var jobsMem = GetJobsFromStore(clubIdCur, username);
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            rights = MergeRights(rightsMap, jobsMem);
                            isOwner = HasOwner(jobsMem);
                        }
                        if (!(isOwner || rights.ManageVenueSettings)) { await WebSocketStore.SendAsync(ws, new { type = "club.logo.update.fail" }); continue; }
                        string? processed = null;
                        try
                        {
                            var bytes = Convert.FromBase64String(raw);
                            if (bytes.Length > 1024 * 1024) { await WebSocketStore.SendAsync(ws, new { type = "club.logo.update.fail" }); continue; }
                            using var img = Image.Load(bytes);
                            var max = 256;
                            var w = img.Width; var h = img.Height;
                            if (w > max || h > max)
                            {
                                var scale = Math.Min((float)max / w, (float)max / h);
                                var nw = (int)MathF.Round(w * scale);
                                var nh = (int)MathF.Round(h * scale);
                                img.Mutate(x => x.Resize(new Size(nw, nh)));
                            }
                            using var ms = new MemoryStream();
                            img.Save(ms, new PngEncoder());
                            processed = Convert.ToBase64String(ms.ToArray());
                        }
                        catch (Exception ex) { app.Logger.LogDebug($"WS club.logo.update.fail: {ex.Message}"); await WebSocketStore.SendAsync(ws, new { type = "club.logo.update.fail" }); continue; }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            await efSvc.SetClubLogoAsync(clubIdCur, processed);
                        }
                        else
                        {
                            Store.ClubLogos[clubIdCur] = processed;
                            await Persistence.SaveAsync();
                        }
                        await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "club.logo", clubId = clubIdCur, logoBase64 = processed ?? string.Empty });
                        await WebSocketStore.SendAsync(ws, new { type = "club.logo.update.ok" });
                        continue;
                    }
                    if (type == "club.logo.delete")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "club.logo.delete.fail", code = 401 }); continue; }
                        bool isOwner;
                        Rights rights;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var jobsChk = await GetJobsFromDbAsync(efChk, clubIdCur, username);
                            var rightsDb = await efChk.GetJobRightsAsync(clubIdCur);
                            rights = MergeRights(rightsDb, jobsChk);
                            isOwner = HasOwner(jobsChk);
                        }
                        else
                        {
                            var jobsMem = GetJobsFromStore(clubIdCur, username);
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            rights = MergeRights(rightsMap, jobsMem);
                            isOwner = HasOwner(jobsMem);
                        }
                        if (!(isOwner || rights.ManageVenueSettings)) { await WebSocketStore.SendAsync(ws, new { type = "club.logo.delete.fail", code = 403 }); continue; }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            await efSvc.SetClubLogoAsync(clubIdCur, null);
                        }
                        else
                        {
                            Store.ClubLogos.TryRemove(clubIdCur, out _);
                            await Persistence.SaveAsync();
                        }
                        await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "club.logo", clubId = clubIdCur, logoBase64 = string.Empty });
                        await WebSocketStore.SendAsync(ws, new { type = "club.logo.delete.ok" });
                        continue;
                    }
                    if (type == "club.accesskey.request")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var clubIdReq = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "club.accesskey.fail", code = 401 }); continue; }
                        bool isOwner;
                        Rights rights;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var jobsChk = await GetJobsFromDbAsync(efChk, clubIdReq, username);
                            var rightsDb = await efChk.GetJobRightsAsync(clubIdReq);
                            rights = MergeRights(rightsDb, jobsChk);
                            isOwner = HasOwner(jobsChk);
                        }
                        else
                        {
                            var jobsMem = GetJobsFromStore(clubIdReq, username);
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            rights = MergeRights(rightsMap, jobsMem);
                            isOwner = HasOwner(jobsMem);
                        }
                        if (!(isOwner || rights.ManageVenueSettings)) { await WebSocketStore.SendAsync(ws, new { type = "club.accesskey.fail", code = 403 }); continue; }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var existsClub = await efSvc.ClubExistsAsync(clubIdReq);
                            if (!existsClub) { await WebSocketStore.SendAsync(ws, new { type = "club.accesskey.fail", code = 404 }); continue; }
                            var key = await efSvc.GetAccessKeyAsync(clubIdReq);
                            await WebSocketStore.SendAsync(ws, new { type = "club.accesskey", accessKey = key ?? string.Empty });
                        }
                        else
                        {
                            if (!Store.CreatedClubs.ContainsKey(clubIdReq)) { await WebSocketStore.SendAsync(ws, new { type = "club.accesskey.fail", code = 404 }); continue; }
                            if (!Store.ClubAccessKeysByClub.TryGetValue(clubIdReq, out var key))
                            {
                                key = Util.NewUid(24);
                                Store.ClubAccessKeysByClub[clubIdReq] = key; Store.ClubAccessKeysByKey[key] = clubIdReq;
                                await Persistence.SaveAsync();
                            }
                            await WebSocketStore.SendAsync(ws, new { type = "club.accesskey", accessKey = key });
                        }
                        continue;
                    }
                    if (type == "club.accesskey.regenerate")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "club.accesskey.regenerate.fail", code = 401 }); continue; }
                        bool isOwner;
                        Rights rights;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var jobsChk = await GetJobsFromDbAsync(efSvc, clubIdCur, username);
                            var rightsDb = await efSvc.GetJobRightsAsync(clubIdCur);
                            rights = MergeRights(rightsDb, jobsChk);
                            isOwner = HasOwner(jobsChk);
                            if (!(isOwner || rights.ManageVenueSettings)) { await WebSocketStore.SendAsync(ws, new { type = "club.accesskey.regenerate.fail", code = 403 }); continue; }
                            var newKey = await efSvc.RegenerateAccessKeyAsync(clubIdCur);
                            if (string.IsNullOrWhiteSpace(newKey)) { await WebSocketStore.SendAsync(ws, new { type = "club.accesskey.regenerate.fail", code = 404 }); continue; }
                            Store.ClubAccessKeysByClub[clubIdCur] = newKey!; Store.ClubAccessKeysByKey[newKey!] = clubIdCur;
                            await WebSocketStore.SendAsync(ws, new { type = "club.accesskey.regenerate.ok", accessKey = newKey });
                        }
                        else
                        {
                            var jobsMem = GetJobsFromStore(clubIdCur, username);
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            rights = MergeRights(rightsMap, jobsMem);
                            isOwner = HasOwner(jobsMem);
                            if (!(isOwner || rights.ManageVenueSettings)) { await WebSocketStore.SendAsync(ws, new { type = "club.accesskey.regenerate.fail", code = 403 }); continue; }
                            var newKey = Util.NewUid(24);
                            Store.ClubAccessKeysByClub[clubIdCur] = newKey; Store.ClubAccessKeysByKey[newKey] = clubIdCur;
                            await Persistence.SaveAsync();
                            await WebSocketStore.SendAsync(ws, new { type = "club.accesskey.regenerate.ok", accessKey = newKey });
                        }
                        continue;
                    }
                    if (type == "user.clubs.request")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        if (!Util.ValidateSession(token, out var usernameReq))
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "user.clubs", clubs = Array.Empty<string>() });
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfUc = app.Services.CreateScope();
                            var efUc = scopeEfUc.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var clubs = await efUc.GetUserClubsAsync(usernameReq) ?? Array.Empty<string>();
                            await WebSocketStore.SendAsync(ws, new { type = "user.clubs", clubs });
                        }
                        else
                        {
                            var clubsMem = Store.ClubUserJobs.Keys
                                .Where(k => {
                                    var p = k.IndexOf('|');
                                    if (p <= 0) return false;
                                    var user = k.Substring(p + 1);
                                    return string.Equals(user, usernameReq, StringComparison.Ordinal);
                                })
                                .Select(k => k.Substring(0, k.IndexOf('|')))
                                .Distinct()
                                .OrderBy(c => c, StringComparer.Ordinal)
                                .ToArray();
                            await WebSocketStore.SendAsync(ws, new { type = "user.clubs", clubs = clubsMem });
                        }
                        continue;
                    }
                    if (type == "user.clubs.created.request")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        if (!Util.ValidateSession(token, out var usernameReq))
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "user.clubs.created", clubs = Array.Empty<string>() });
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfCc = app.Services.CreateScope();
                            var efCc = scopeEfCc.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var clubs = await efCc.GetCreatedClubsAsync(usernameReq) ?? Array.Empty<string>();
                            await WebSocketStore.SendAsync(ws, new { type = "user.clubs.created", clubs });
                        }
                        else
                        {
                            var clubsMem = Store.CreatedClubs
                                .Where(kv => string.Equals(kv.Value, usernameReq, StringComparison.Ordinal))
                                .Select(kv => kv.Key)
                                .OrderBy(c => c, StringComparer.Ordinal)
                                .ToArray();
                            await WebSocketStore.SendAsync(ws, new { type = "user.clubs.created", clubs = clubsMem });
                        }
                        continue;
                    }
                    if (type == "server.announcement")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var message = root.TryGetProperty("message", out var m) ? (m.GetString() ?? string.Empty) : string.Empty;
                        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(message)) { await WebSocketStore.SendAsync(ws, new { type = "server.announcement.fail", message = "Missing token or message" }); continue; }
                        if (!Util.ValidateSession(token, out var usernameReq)) { await WebSocketStore.SendAsync(ws, new { type = "server.announcement.fail", message = "Invalid session" }); continue; }
                        var isAdmin = await IsServerAdminAsync(app, conn, usernameReq);
                        if (!isAdmin) { await WebSocketStore.SendAsync(ws, new { type = "server.announcement.fail", message = "No rights" }); continue; }
                        await WebSocketStore.BroadcastAsync(new { type = "server.announcement", message });
                        await WebSocketStore.SendAsync(ws, new { type = "server.announcement.ok" });
                        continue;
                    }
                    if (type == "maintenance.mode.request")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        if (string.IsNullOrWhiteSpace(token)) { await WebSocketStore.SendAsync(ws, new { type = "maintenance.mode.status.fail", message = "Missing token" }); continue; }
                        if (!Util.ValidateSession(token, out var usernameReq)) { await WebSocketStore.SendAsync(ws, new { type = "maintenance.mode.status.fail", message = "Invalid session" }); continue; }
                        var isAdmin = await IsServerAdminAsync(app, conn, usernameReq);
                        if (!isAdmin) { await WebSocketStore.SendAsync(ws, new { type = "maintenance.mode.status.fail", message = "No rights" }); continue; }
                        await WebSocketStore.SendAsync(ws, new { type = "maintenance.mode.status", active = Store.MaintenanceMode, pending = Store.MaintenanceModePendingEnable });
                        continue;
                    }
                    if (type == "maintenance.mode.set")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        if (string.IsNullOrWhiteSpace(token)) { await WebSocketStore.SendAsync(ws, new { type = "maintenance.mode.set.fail", message = "Missing token" }); continue; }
                        if (!Util.ValidateSession(token, out var usernameReq)) { await WebSocketStore.SendAsync(ws, new { type = "maintenance.mode.set.fail", message = "Invalid session" }); continue; }
                        var isAdmin = await IsServerAdminAsync(app, conn, usernameReq);
                        if (!isAdmin) { await WebSocketStore.SendAsync(ws, new { type = "maintenance.mode.set.fail", message = "No rights" }); continue; }
                        if (!root.TryGetProperty("enabled", out var enabledEl) || (enabledEl.ValueKind != JsonValueKind.True && enabledEl.ValueKind != JsonValueKind.False))
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "maintenance.mode.set.fail", message = "Missing enabled flag" });
                            continue;
                        }
                        var enabled = enabledEl.GetBoolean();
                        if (enabled)
                        {
                            if (!Store.MaintenanceMode)
                            {
                                Store.MaintenanceModePendingEnable = true;
                            }
                            else
                            {
                                Store.MaintenanceModePendingEnable = false;
                            }
                        }
                        else
                        {
                            Store.MaintenanceMode = false;
                            Store.MaintenanceModePendingEnable = false;
                        }
                        await Persistence.SaveMaintenanceStateAsync(Store.MaintenanceMode, Store.MaintenanceModePendingEnable);
                        await WebSocketStore.SendAsync(ws, new { type = "maintenance.mode.set.ok", active = Store.MaintenanceMode, pending = Store.MaintenanceModePendingEnable });
                        continue;
                    }
                    if (type == "server.shutdown" || type == "server.restart")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        if (string.IsNullOrWhiteSpace(token)) { await WebSocketStore.SendAsync(ws, new { type = type == "server.restart" ? "server.restart.fail" : "server.shutdown.fail", message = "Missing token" }); continue; }
                        if (!Util.ValidateSession(token, out var usernameReq)) { await WebSocketStore.SendAsync(ws, new { type = type == "server.restart" ? "server.restart.fail" : "server.shutdown.fail", message = "Invalid session" }); continue; }
                        var isAdmin = await IsServerAdminAsync(app, conn, usernameReq);
                        if (!isAdmin) { await WebSocketStore.SendAsync(ws, new { type = type == "server.restart" ? "server.restart.fail" : "server.shutdown.fail", message = "No rights" }); continue; }
                        var message = root.TryGetProperty("message", out var m) ? (m.GetString() ?? string.Empty) : string.Empty;
                        var minutesList = new System.Collections.Generic.List<int>();
                        if (root.TryGetProperty("minutes", out var minsEl) && minsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in minsEl.EnumerateArray())
                            {
                                if (el.ValueKind != JsonValueKind.Number) continue;
                                if (el.TryGetInt32(out var v) && v > 0 && v <= 1440) minutesList.Add(v);
                            }
                        }
                        var isRestart = type == "server.restart";
                        var minutesSet = new System.Collections.Generic.HashSet<int>();
                        for (int i = 0; i < minutesList.Count; i++) minutesSet.Add(minutesList[i]);
                        var minutes = new int[minutesSet.Count];
                        int idx = 0;
                        foreach (var v in minutesSet) { minutes[idx] = v; idx++; }
                        Array.Sort(minutes, (a, b) => b.CompareTo(a));
                        lock (_adminStateLock)
                        {
                            if (_shutdownCts != null)
                            {
                                try { _shutdownCts.Cancel(); } catch { }
                                _shutdownCts.Dispose();
                                _shutdownCts = null;
                            }
                            _shutdownCts = new System.Threading.CancellationTokenSource();
                            var ct = _shutdownCts.Token;
                            _shutdownTask = System.Threading.Tasks.Task.Run(async () =>
                            {
                                try
                                {
                                    var now = DateTimeOffset.UtcNow;
                                    var maxMinutes = minutes.Length > 0 ? minutes[0] : 0;
                                    var shutdownAt = now.AddMinutes(maxMinutes);
                                    for (int i = 0; i < minutes.Length; i++)
                                    {
                                        var announceAt = shutdownAt.AddMinutes(-minutes[i]);
                                        var delay = announceAt - DateTimeOffset.UtcNow;
                                        if (delay > TimeSpan.Zero) await System.Threading.Tasks.Task.Delay(delay, ct);
                                        if (ct.IsCancellationRequested) return;
                                        var msgOut = BuildAnnouncementMessage(message, minutes[i], isRestart);
                                        await WebSocketStore.BroadcastAsync(new { type = "server.announcement", message = msgOut });
                                    }
                                    var finalDelay = shutdownAt - DateTimeOffset.UtcNow;
                                    if (finalDelay > TimeSpan.Zero) await System.Threading.Tasks.Task.Delay(finalDelay, ct);
                                    if (ct.IsCancellationRequested) return;
                                    var finalMsg = BuildAnnouncementMessage(message, 0, isRestart);
                                    await WebSocketStore.BroadcastAsync(new { type = "server.announcement", message = finalMsg });
                                    await System.Threading.Tasks.Task.Delay(250, ct);
                                    if (ct.IsCancellationRequested) return;
                                    await WebSocketStore.CloseAllAsync();
                                    Store.StaffSessions.Clear();
                                    Store.StaffSessionExpiry.Clear();
                                    app.Lifetime.StopApplication();
                                }
                                catch (System.Threading.Tasks.TaskCanceledException) { }
                                catch (Exception ex) { app.Logger.LogDebug($"WS server shutdown schedule failed: {ex.Message}"); }
                            }, ct);
                        }
                        await WebSocketStore.SendAsync(ws, new { type = isRestart ? "server.restart.ok" : "server.shutdown.ok" });
                        continue;
                    }
                    if (type == "user.self.rights.request")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        if (!Util.ValidateSession(token, out var usernameReq)) { await WebSocketStore.SendAsync(ws, new { type = "user.self.rights", job = "Unassigned", rights = new System.Collections.Generic.Dictionary<string, bool>() }); continue; }
                        var clubReq = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        string[] jobsCur;
                        Rights rightsCur;
                        string jobCur;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var rightsDb = await efSvc.GetJobRightsAsync(clubReq);
                            jobsCur = await GetJobsFromDbAsync(efSvc, clubReq, usernameReq);
                            rightsCur = MergeRights(rightsDb, jobsCur);
                            jobCur = GetPrimaryJob(rightsDb, EnsureJobs(jobsCur));
                        }
                        else
                        {
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            jobsCur = GetJobsFromStore(clubReq, usernameReq);
                            rightsCur = MergeRights(rightsMap, jobsCur);
                            jobCur = GetPrimaryJob(rightsMap, EnsureJobs(jobsCur));
                        }
                        var rightsObj = new System.Collections.Generic.Dictionary<string, bool>
                        {
                            [nameof(Rights.AddVip)[0].ToString().ToLowerInvariant() + nameof(Rights.AddVip).Substring(1)] = rightsCur.AddVip,
                            [nameof(Rights.RemoveVip)[0].ToString().ToLowerInvariant() + nameof(Rights.RemoveVip).Substring(1)] = rightsCur.RemoveVip,
                            [nameof(Rights.EditVipHomeWorld)[0].ToString().ToLowerInvariant() + nameof(Rights.EditVipHomeWorld).Substring(1)] = rightsCur.EditVipHomeWorld,
                            [nameof(Rights.ManageUsers)[0].ToString().ToLowerInvariant() + nameof(Rights.ManageUsers).Substring(1)] = rightsCur.ManageUsers,
                            [nameof(Rights.DeleteStaffMember)[0].ToString().ToLowerInvariant() + nameof(Rights.DeleteStaffMember).Substring(1)] = rightsCur.DeleteStaffMember,
                            [nameof(Rights.ManageJobs)[0].ToString().ToLowerInvariant() + nameof(Rights.ManageJobs).Substring(1)] = rightsCur.ManageJobs,
                            [nameof(Rights.ManageVenueSettings)[0].ToString().ToLowerInvariant() + nameof(Rights.ManageVenueSettings).Substring(1)] = rightsCur.ManageVenueSettings,
                            [nameof(Rights.EditVipDuration)[0].ToString().ToLowerInvariant() + nameof(Rights.EditVipDuration).Substring(1)] = rightsCur.EditVipDuration,
                            [nameof(Rights.AddDj)[0].ToString().ToLowerInvariant() + nameof(Rights.AddDj).Substring(1)] = rightsCur.AddDj,
                            [nameof(Rights.RemoveDj)[0].ToString().ToLowerInvariant() + nameof(Rights.RemoveDj).Substring(1)] = rightsCur.RemoveDj,
                            [nameof(Rights.EditShiftPlan)[0].ToString().ToLowerInvariant() + nameof(Rights.EditShiftPlan).Substring(1)] = rightsCur.EditShiftPlan,
                        };
                        await WebSocketStore.SendAsync(ws, new { type = "user.self.rights", jobs = EnsureJobs(jobsCur), job = jobCur, rights = rightsObj });
                        continue;
                    }
                    if (type == "user.self.profile.request")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        if (!Util.ValidateSession(token, out var usernameReq)) { await WebSocketStore.SendAsync(ws, new { type = "user.self.profile", username = string.Empty, uid = string.Empty, isServerAdmin = false }); continue; }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var info = await efSvc.GetStaffUserByUsernameAsync(usernameReq);
                            var uid = info?.Uid ?? string.Empty;
                            var isServerAdmin = info?.IsServerAdmin ?? false;
                            await WebSocketStore.SendAsync(ws, new { type = "user.self.profile", username = usernameReq, uid, isServerAdmin });
                        }
                        else
                        {
                            Store.StaffUsers.TryGetValue(usernameReq, out var infoMem);
                            var uidMem = (infoMem != null && !string.IsNullOrWhiteSpace(infoMem.Uid)) ? infoMem.Uid : usernameReq;
                            var isServerAdmin = infoMem != null && infoMem.IsServerAdmin;
                            await WebSocketStore.SendAsync(ws, new { type = "user.self.profile", username = usernameReq, uid = uidMem, isServerAdmin });
                        }
                        continue;
                    }
                    if (type == "user.self.birthday.request")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        if (!Util.ValidateSession(token, out var usernameReq)) { await WebSocketStore.SendAsync(ws, new { type = "user.self.birthday", birthday = (DateTimeOffset?)null }); continue; }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var birthday = await efSvc.GetUserBirthdayAsync(usernameReq);
                            await WebSocketStore.SendAsync(ws, new { type = "user.self.birthday", birthday });
                        }
                        else
                        {
                            DateTimeOffset? birthday = null;
                            if (Store.StaffUsers.TryGetValue(usernameReq, out var infoMem) && infoMem != null) birthday = infoMem.Birthday;
                            await WebSocketStore.SendAsync(ws, new { type = "user.self.birthday", birthday });
                        }
                        continue;
                    }
                    if (type == "user.self.birthday.set")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "user.self.birthday.fail", code = 401 }); continue; }
                        DateTimeOffset? birthday = null;
                        if (root.TryGetProperty("birthday", out var b) && b.ValueKind != JsonValueKind.Null)
                        {
                            if (b.ValueKind == JsonValueKind.String)
                            {
                                if (DateTimeOffset.TryParse(b.GetString(), out var dt)) birthday = dt;
                            }
                        }
                        if (birthday.HasValue) birthday = birthday.Value.ToUniversalTime();
                        bool ok;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            ok = await efSvc.UpdateUserBirthdayAsync(username, birthday);
                            if (ok)
                            {
                                var clubs = await efSvc.GetUserClubsAsync(username);
                                for (int i = 0; i < clubs.Length; i++)
                                {
                                    await BroadcastUsersDetailsForClubDbAsync(clubs[i], efSvc);
                                }
                            }
                        }
                        else
                        {
                            ok = Store.StaffUsers.TryGetValue(username, out var info);
                            if (ok && info != null)
                            {
                                info.Birthday = birthday;
                                Store.StaffUsers[username] = info;
                                await Persistence.SaveAsync();
                                var clubs = GetUserClubsFromStore(username);
                                for (int i = 0; i < clubs.Length; i++)
                                {
                                    await BroadcastUsersDetailsForClubMemAsync(clubs[i]);
                                }
                            }
                        }
                        await WebSocketStore.SendAsync(ws, new { type = ok ? "user.self.birthday.ok" : "user.self.birthday.fail", code = ok ? 200 : 400 });
                        continue;
                    }
                    if (type == "user.exists.request")
                    {
                        var usernameChk = root.TryGetProperty("username", out var u) ? (u.GetString() ?? string.Empty) : string.Empty;
                        if (string.IsNullOrWhiteSpace(usernameChk)) { await WebSocketStore.SendAsync(ws, new { type = "user.exists", exists = false }); continue; }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var exists = (await efSvc.GetStaffUserByUsernameAsync(usernameChk)) != null;
                            await WebSocketStore.SendAsync(ws, new { type = "user.exists", exists });
                        }
                        else
                        {
                            var existsMem = Store.StaffUsers.ContainsKey(usernameChk);
                            await WebSocketStore.SendAsync(ws, new { type = "user.exists", exists = existsMem });
                        }
                        continue;
                    }
                    if (type == "club.register")
                    {
                        var clubIdReg = root.TryGetProperty("clubId", out var c) ? (c.GetString() ?? string.Empty) : string.Empty;
                        var defaultStaffPassword = root.TryGetProperty("defaultStaffPassword", out var dsp) ? (dsp.GetString() ?? string.Empty) : string.Empty;
                        var tokenReg = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var creatorUsername = (!string.IsNullOrWhiteSpace(tokenReg) && Util.ValidateSession(tokenReg, out var unameReg)) ? unameReg : (root.TryGetProperty("creatorUsername", out var cu) ? (cu.GetString() ?? string.Empty) : string.Empty);
                        if (string.IsNullOrWhiteSpace(clubIdReg)) { await WebSocketStore.SendAsync(ws, new { type = "club.register.fail", code = 400 }); app.Logger.LogDebug("WS club.register.fail reason=missing clubId"); continue; }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var existsClub = await efSvc.ClubExistsAsync(clubIdReg);
                            if (existsClub) { await WebSocketStore.SendAsync(ws, new { type = "club.register.fail", code = 409 }); app.Logger.LogDebug($"WS club.register.fail club={clubIdReg} reason=exists"); continue; }
                            await efSvc.EnsureDefaultsAsync(clubIdReg, defaultStaffPassword);
                            if (!string.IsNullOrWhiteSpace(creatorUsername))
                            {
                                var existingHash = await efSvc.GetStaffPasswordHashAsync("default", creatorUsername);
                                var hashToUse = !string.IsNullOrWhiteSpace(existingHash) ? existingHash! : Util.HashPassword(creatorUsername, defaultStaffPassword);
                                await efSvc.CreateStaffUserAsync(clubIdReg, creatorUsername!, hashToUse);
                                await efSvc.UpdateStaffUserJobsAsync(clubIdReg, creatorUsername!, new[] { "Owner" });
                                await efSvc.AddClubIfMissingAsync(clubIdReg, creatorUsername!);
                                var accessKey = await efSvc.GetAccessKeyAsync(clubIdReg) ?? Util.NewUid(24);
                                Store.ClubAccessKeysByClub[clubIdReg] = accessKey; Store.ClubAccessKeysByKey[accessKey] = clubIdReg;
                                var list = await efSvc.GetStaffUsersAsync(clubIdReg) ?? Array.Empty<StaffUserInfo>();
                                var rightsDb = await efSvc.GetJobRightsAsync(clubIdReg);
                                await WebSocketStore.BroadcastToClubAsync(clubIdReg, new { type = "user.update", username = creatorUsername!, jobs = new[] { "Owner" }, job = "Owner" });
                                var sockets = WebSocketStore.GetSocketsForClub(clubIdReg);
                                var ordered = list.OrderBy(u => u.Username, StringComparer.Ordinal).ToArray();
                                for (int i = 0; i < sockets.Length; i++)
                                {
                                    var viewer = ResolveViewerUsername(sockets[i].Id);
                                    var users = ordered.Select(u => BuildStaffUserPayload(u, rightsDb, viewer)).ToArray();
                                    await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
                                }
                            }
                        }
                        else
                        {
                            if (Store.CreatedClubs.ContainsKey(clubIdReg)) { await WebSocketStore.SendAsync(ws, new { type = "club.register.fail", code = 409 }); app.Logger.LogDebug($"WS club.register.fail club={clubIdReg} reason=exists mem"); continue; }
                            if (!string.IsNullOrWhiteSpace(creatorUsername))
                            {
                                Store.CreatedClubs[clubIdReg] = creatorUsername;
                                Store.SetJobsForUser(clubIdReg, creatorUsername, new[] { "Owner" });
                                var accessKey = Util.NewUid(24);
                                Store.ClubAccessKeysByClub[clubIdReg] = accessKey; Store.ClubAccessKeysByKey[accessKey] = clubIdReg;
                                await WebSocketStore.BroadcastToClubAsync(clubIdReg, new { type = "user.update", username = creatorUsername, jobs = new[] { "Owner" }, job = "Owner" });
                            }
                        }
                        await WebSocketStore.BroadcastAsync(new { type = "jobs.list", jobs = Store.JobRights.Keys.OrderBy(j => j, StringComparer.Ordinal).ToArray() });
                        await WebSocketStore.SendAsync(ws, new { type = "club.register.ok" });
                        app.Logger.LogDebug($"WS club.register.ok club={clubIdReg} creator={creatorUsername}");
                        continue;
                    }
                    if (type == "club.delete")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var clubIdDel = root.TryGetProperty("clubId", out var c) ? (c.GetString() ?? "default") : "default";
                        if (!Util.ValidateSession(token, out var usernameDel))
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "club.delete.fail", code = 401 });
                            app.Logger.LogDebug($"WS club.delete.fail club={clubIdDel} reason=session");
                            continue;
                        }
                        bool isOwner;
                        Rights rights;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var created = await efChk.GetCreatedClubsAsync(usernameDel) ?? Array.Empty<string>();
                            isOwner = Array.IndexOf(created, clubIdDel) >= 0;
                            if (!isOwner)
                            {
                                await WebSocketStore.SendAsync(ws, new { type = "club.delete.fail", code = 401 });
                                app.Logger.LogDebug($"WS club.delete.fail club={clubIdDel} reason=notowner");
                                continue;
                            }
                            var jobsChk = await GetJobsFromDbAsync(efChk, clubIdDel, usernameDel);
                            var rightsDb = await efChk.GetJobRightsAsync(clubIdDel);
                            rights = MergeRights(rightsDb, jobsChk);
                        }
                        else
                        {
                            isOwner = Store.CreatedClubs.TryGetValue(clubIdDel, out var ownerU) && string.Equals(ownerU, usernameDel, StringComparison.Ordinal);
                            if (!isOwner)
                            {
                                await WebSocketStore.SendAsync(ws, new { type = "club.delete.fail", code = 401 });
                                app.Logger.LogDebug($"WS club.delete.fail club={clubIdDel} reason=notowner");
                                continue;
                            }
                            var jobsMem = GetJobsFromStore(clubIdDel, usernameDel);
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            rights = MergeRights(rightsMap, jobsMem);
                        }
                        if (!rights.ManageVenueSettings)
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "club.delete.fail", code = 403 });
                            app.Logger.LogDebug($"WS club.delete.fail club={clubIdDel} reason=rights");
                            continue;
                        }
                        if (string.Equals(clubIdDel, "default", StringComparison.Ordinal))
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "club.delete.fail", code = 403 });
                            app.Logger.LogDebug("WS club.delete.fail reason=default club");
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfDel = app.Services.CreateScope();
                            var efDel = scopeEfDel.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            await efDel.DeleteClubAsync(clubIdDel);
                            await WebSocketStore.BroadcastToClubAsync(clubIdDel, new { type = "club.deleted" });
                            await WebSocketStore.BroadcastAsync(new { type = "membership.removed", username = string.Empty, clubId = clubIdDel });
                            app.Logger.LogDebug($"WS club.delete.ok club={clubIdDel} db");
                        }
                        else
                        {
                            var keys = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubIdDel + "|", StringComparison.Ordinal)).ToArray();
                            foreach (var k in keys) Store.ClubUserJobs.TryRemove(k, out _);
                            Store.CreatedClubs.TryRemove(clubIdDel, out _);
                            Store.JobRights.Clear();
                            Store.VipEntries.Clear();
                            await Persistence.SaveAsync();
                            await WebSocketStore.BroadcastToClubAsync(clubIdDel, new { type = "club.deleted" });
                            await WebSocketStore.BroadcastAsync(new { type = "membership.removed", username = string.Empty, clubId = clubIdDel });
                            app.Logger.LogDebug($"WS club.delete.ok club={clubIdDel} mem");
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "club.delete.ok" });
                        continue;
                    }
                    if (type == "club.join")
                    {
                        var tokenJoin = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var clubIdJoin = root.TryGetProperty("clubId", out var c) ? (c.GetString() ?? string.Empty) : string.Empty;
                        var joinPassword = root.TryGetProperty("password", out var p) ? (p.GetString() ?? string.Empty) : string.Empty;
                        if (!Util.ValidateSession(tokenJoin, out var usernameJoin)) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 401 }); app.Logger.LogDebug($"WS club.join.fail club={clubIdJoin} reason=session"); continue; }
                        if (string.IsNullOrWhiteSpace(clubIdJoin)) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 400 }); app.Logger.LogDebug("WS club.join.fail reason=missing clubId"); continue; }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var existsClub = await efSvc.ClubExistsAsync(clubIdJoin);
                            if (!existsClub) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 404 }); app.Logger.LogDebug($"WS club.join.fail club={clubIdJoin} reason=notfound"); continue; }
                            var joinHashDb = await efSvc.GetClubJoinPasswordHashAsync(clubIdJoin);
                            if (string.IsNullOrWhiteSpace(joinHashDb)) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 403 }); app.Logger.LogDebug($"WS club.join.fail club={clubIdJoin} reason=joinpasswordmissing"); continue; }
                            if (string.IsNullOrWhiteSpace(joinPassword)) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 403 }); app.Logger.LogDebug($"WS club.join.fail club={clubIdJoin} reason=passwordmissing"); continue; }
                            var passOkDb = Util.VerifyPassword(string.Empty, joinPassword!, joinHashDb!);
                            if (!passOkDb) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 401 }); app.Logger.LogDebug($"WS club.join.fail club={clubIdJoin} reason=password"); continue; }
                            var existsMembership = await efSvc.UserExistsAsync(clubIdJoin, usernameJoin);
                            if (existsMembership) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 409 }); app.Logger.LogDebug($"WS club.join.fail club={clubIdJoin} reason=already"); continue; }
                            var existingUserAny = await efSvc.GetStaffUserByUsernameAsync(usernameJoin);
                            var hash = existingUserAny?.PasswordHash ?? (Store.StaffUsers.TryGetValue(usernameJoin, out var inf) ? inf.PasswordHash : null);
                            if (string.IsNullOrWhiteSpace(hash)) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 401 }); continue; }
                            await efSvc.CreateStaffUserAsync(clubIdJoin, usernameJoin, hash!);
                            using var scopeEfWs = app.Services.CreateScope();
                            var efSvcWs = scopeEfWs.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var list = await efSvcWs.GetStaffUsersAsync(clubIdJoin) ?? Array.Empty<StaffUserInfo>();
                            var rightsDb = await efSvcWs.GetJobRightsAsync(clubIdJoin);
                            await WebSocketStore.BroadcastToClubAsync(clubIdJoin, new { type = "users.list", users = list.Select(u => u.Username).OrderBy(u => u, StringComparer.Ordinal).ToArray() });
                            var sockets = WebSocketStore.GetSocketsForClub(clubIdJoin);
                            var ordered = list.OrderBy(u => u.Username, StringComparer.Ordinal).ToArray();
                            for (int i = 0; i < sockets.Length; i++)
                            {
                                var viewer = ResolveViewerUsername(sockets[i].Id);
                                var users = ordered.Select(u => BuildStaffUserPayload(u, rightsDb, viewer)).ToArray();
                                await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
                            }
                            await WebSocketStore.BroadcastAsync(new { type = "membership.added", username = usernameJoin, clubId = clubIdJoin });
                            app.Logger.LogDebug($"WS club.join.ok club={clubIdJoin} user={usernameJoin} db");
                        }
                        else
                        {
                            if (!Store.CreatedClubs.ContainsKey(clubIdJoin)) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 404 }); app.Logger.LogDebug($"WS club.join.fail club={clubIdJoin} reason=notfound mem"); continue; }
                            if (!Store.ClubJoinPasswordHashes.TryGetValue(clubIdJoin, out var joinHashMem)) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 403 }); app.Logger.LogDebug($"WS club.join.fail club={clubIdJoin} reason=joinpasswordmissing mem"); continue; }
                            if (string.IsNullOrWhiteSpace(joinPassword)) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 403 }); app.Logger.LogDebug($"WS club.join.fail club={clubIdJoin} reason=passwordmissing mem"); continue; }
                            var passOkMem = Util.VerifyPassword(string.Empty, joinPassword!, joinHashMem!);
                            if (!passOkMem) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 401 }); app.Logger.LogDebug($"WS club.join.fail club={clubIdJoin} reason=password mem"); continue; }
                            if (Store.ClubUserJobs.ContainsKey(clubIdJoin + "|" + usernameJoin)) { await WebSocketStore.SendAsync(ws, new { type = "club.join.fail", code = 409 }); app.Logger.LogDebug($"WS club.join.fail club={clubIdJoin} reason=already mem"); continue; }
                            Store.SetJobsForUser(clubIdJoin, usernameJoin, new[] { "Unassigned" });
                            if (!Store.StaffUsers.ContainsKey(usernameJoin))
                            {
                                Store.StaffUsers[usernameJoin] = new StaffUserInfo { Username = usernameJoin, PasswordHash = Util.HashPassword(usernameJoin, string.Empty), Jobs = new[] { "Unassigned" }, Role = "power", CreatedAt = DateTimeOffset.UtcNow, Uid = usernameJoin };
                            }
                            await Persistence.SaveAsync();
                            var usersClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubIdJoin + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubIdJoin.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            await WebSocketStore.BroadcastToClubAsync(clubIdJoin, new { type = "users.list", users = usersClub });
                            var sockets = WebSocketStore.GetSocketsForClub(clubIdJoin);
                            for (int i = 0; i < sockets.Length; i++)
                            {
                                var viewer = ResolveViewerUsername(sockets[i].Id);
                                var users = usersClub.Select(u => BuildStaffUserPayloadFromStore(clubIdJoin, u, rightsMap, viewer)).ToArray();
                                await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
                            }
                            await WebSocketStore.BroadcastAsync(new { type = "membership.added", username = usernameJoin, clubId = clubIdJoin });
                            app.Logger.LogDebug($"WS club.join.ok club={clubIdJoin} user={usernameJoin} mem");
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "club.join.ok" });
                        continue;
                    }
                    if (type == "club.join.password.set")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var newPassword = root.TryGetProperty("newPassword", out var np) ? (np.GetString() ?? string.Empty) : string.Empty;
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "club.join.password.fail", code = 401 }); app.Logger.LogDebug("WS club.join.password.fail reason=session"); continue; }
                        bool isOwner;
                        Rights rights;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var created = await efChk.GetCreatedClubsAsync(username) ?? Array.Empty<string>();
                            isOwner = Array.IndexOf(created, clubIdCur) >= 0;
                            var jobsChk = await GetJobsFromDbAsync(efChk, clubIdCur, username);
                            var rightsDb = await efChk.GetJobRightsAsync(clubIdCur);
                            rights = MergeRights(rightsDb, jobsChk);
                        }
                        else
                        {
                            isOwner = Store.CreatedClubs.TryGetValue(clubIdCur, out var ownerU) && string.Equals(ownerU, username, StringComparison.Ordinal);
                            var jobsMem = GetJobsFromStore(clubIdCur, username);
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            rights = MergeRights(rightsMap, jobsMem);
                        }
                        if (!rights.ManageVenueSettings) { await WebSocketStore.SendAsync(ws, new { type = "club.join.password.fail", code = 403 }); continue; }
                        var hash = Util.HashPassword(string.Empty, newPassword ?? string.Empty);
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            await efSvc.SetClubJoinPasswordAsync(clubIdCur, hash);
                        }
                        else
                        {
                            Store.ClubJoinPasswordHashes[clubIdCur] = hash;
                            await Persistence.SaveAsync();
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "club.join.password.ok" });
                        app.Logger.LogDebug($"WS club.join.password.ok club={clubIdCur}");
                        continue;
                    }
                    if (type == "user.self.password.set")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var newPassword = root.TryGetProperty("newPassword", out var np) ? (np.GetString() ?? string.Empty) : string.Empty;
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "user.self.password.fail", code = 401 }); app.Logger.LogDebug("WS user.self.password.fail reason=session"); continue; }
                        if (string.IsNullOrWhiteSpace(newPassword)) { await WebSocketStore.SendAsync(ws, new { type = "user.self.password.fail", code = 400 }); app.Logger.LogDebug("WS user.self.password.fail reason=missing newPassword"); continue; }
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (!Store.StaffUsers.TryGetValue(username, out var info))
                        {
                            if (!string.IsNullOrWhiteSpace(conn))
                            {
                                using var scopeEf = app.Services.CreateScope();
                                var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                var infoDb = await efSvc.GetStaffUserAsync(clubIdCur, username);
                                if (infoDb == null) { await WebSocketStore.SendAsync(ws, new { type = "user.self.password.fail", code = 401 }); continue; }
                                info = infoDb;
                                Store.StaffUsers[username] = info;
                            }
                            else
                            {
                                await WebSocketStore.SendAsync(ws, new { type = "user.self.password.fail", code = 401 });
                                continue;
                            }
                        }
                        var newHash = Util.HashPassword(username, newPassword);
                        info.PasswordHash = newHash;
                        Store.StaffUsers[username] = info;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfUpd = app.Services.CreateScope();
                            var efUpd = scopeEfUpd.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            await efUpd.UpdateStaffPasswordAsync(clubIdCur, username, newHash);
                        }
                        else
                        {
                            await Persistence.SaveAsync();
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "user.self.password.ok" });
                        app.Logger.LogDebug($"WS user.self.password.ok user={username}");
                        continue;
                    }
                    if (type == "user.recovery.generate.request")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "user.recovery.generate.fail", code = 401 }); app.Logger.LogDebug("WS user.recovery.generate.fail reason=session"); continue; }
                        var recoveryCode = Util.NewUid(24);
                        var hash = Util.Sha256(recoveryCode);
                        var ok = false;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            ok = await efSvc.SetRecoveryCodeHashAsync(username, hash);
                        }
                        else
                        {
                            if (Store.StaffUsers.TryGetValue(username, out var info))
                            {
                                info.RecoveryCodeHash = hash;
                                Store.StaffUsers[username] = info;
                                await Persistence.SaveAsync();
                                ok = true;
                            }
                        }
                        if (!ok)
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "user.recovery.generate.fail", code = 404 });
                            app.Logger.LogDebug($"WS user.recovery.generate.fail user={username} reason=notfound");
                            continue;
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "user.recovery.generate.ok", recoveryCode });
                        app.Logger.LogDebug($"WS user.recovery.generate.ok user={username}");
                        continue;
                    }
                    if (type == "user.password.reset.recovery.request")
                    {
                        var username = root.TryGetProperty("username", out var u) ? (u.GetString() ?? string.Empty) : string.Empty;
                        var recoveryCode = root.TryGetProperty("recoveryCode", out var rc) ? (rc.GetString() ?? string.Empty) : string.Empty;
                        var newPassword = root.TryGetProperty("newPassword", out var np) ? (np.GetString() ?? string.Empty) : string.Empty;
                        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(recoveryCode) || string.IsNullOrWhiteSpace(newPassword))
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "user.password.reset.recovery.fail", code = 400, message = "Missing username, recovery code or password" });
                            app.Logger.LogDebug("WS user.password.reset.recovery.fail reason=missing");
                            continue;
                        }
                        if (!LoginRateLimiter.Allow("reset|" + username))
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "user.password.reset.recovery.fail", code = 429, message = "Too many attempts" });
                            app.Logger.LogDebug($"WS user.password.reset.recovery.fail user={username} reason=rate");
                            continue;
                        }
                        var recoveryHash = Util.Sha256(recoveryCode);
                        var newHash = Util.HashPassword(username, newPassword);
                        bool ok;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            ok = await efSvc.ResetPasswordByRecoveryCodeAsync(username, recoveryHash, newHash);
                        }
                        else
                        {
                            ok = Store.StaffUsers.TryGetValue(username, out var info) && string.Equals(info.RecoveryCodeHash ?? string.Empty, recoveryHash, StringComparison.Ordinal);
                            if (ok)
                            {
                                info.PasswordHash = newHash;
                                info.RecoveryCodeHash = string.Empty;
                                Store.StaffUsers[username] = info;
                                await Persistence.SaveAsync();
                            }
                        }
                        if (!ok)
                        {
                            await WebSocketStore.SendAsync(ws, new { type = "user.password.reset.recovery.fail", code = 401, message = "Invalid recovery data" });
                            app.Logger.LogDebug($"WS user.password.reset.recovery.fail user={username} reason=invalid");
                            continue;
                        }
                        if (Store.StaffUsers.TryGetValue(username, out var cached))
                        {
                            cached.PasswordHash = newHash;
                            cached.RecoveryCodeHash = string.Empty;
                            Store.StaffUsers[username] = cached;
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "user.password.reset.recovery.ok" });
                        app.Logger.LogDebug($"WS user.password.reset.recovery.ok user={username}");
                        continue;
                    }
                    if (type == "club.invite")
                    {
                        var token = root.TryGetProperty("token", out var tok) ? (tok.GetString() ?? string.Empty) : string.Empty;
                        var targetUid = root.TryGetProperty("targetUid", out var tu) ? (tu.GetString() ?? string.Empty) : string.Empty;
                        var targetUsername = root.TryGetProperty("targetUsername", out var tuser) ? (tuser.GetString() ?? string.Empty) : string.Empty;
                        var hasJobs = TryGetJobsFromPayload(root, out var jobsInvite);
                        var inviteJobs = hasJobs ? EnsureJobs(jobsInvite) : new[] { "Unassigned" };
                        var clubIdCur = (WebSocketStore.TryGetClub(id, out var cc) && !string.IsNullOrWhiteSpace(cc)) ? cc! : "default";
                        if (!Util.ValidateSession(token, out var username)) { await WebSocketStore.SendAsync(ws, new { type = "club.invite.fail", code = 401 }); app.Logger.LogDebug("WS club.invite.fail reason=session"); continue; }
                        bool isOwner;
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEfChk = app.Services.CreateScope();
                            var efChk = scopeEfChk.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var created = await efChk.GetCreatedClubsAsync(username) ?? Array.Empty<string>();
                            isOwner = Array.IndexOf(created, clubIdCur) >= 0;
                        }
                        else
                        {
                            isOwner = Store.CreatedClubs.TryGetValue(clubIdCur, out var ownerU) && string.Equals(ownerU, username, StringComparison.Ordinal);
                        }
                        var rightsMapMem = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                        var actorJobs = GetJobsFromStore(clubIdCur, username);
                        var actorRights = MergeRights(rightsMapMem, actorJobs);
                        var canManage = isOwner || actorRights.ManageUsers;
                        if (!canManage) { await WebSocketStore.SendAsync(ws, new { type = "club.invite.fail", code = 403 }); app.Logger.LogDebug("WS club.invite.fail reason=rights"); continue; }
                        var resolvedUsername = !string.IsNullOrWhiteSpace(targetUsername) ? targetUsername : string.Empty;
                        if (string.IsNullOrWhiteSpace(resolvedUsername) && !string.IsNullOrWhiteSpace(targetUid))
                        {
                            if (!string.IsNullOrWhiteSpace(conn))
                            {
                                using var scopeEfName = app.Services.CreateScope();
                                var efName = scopeEfName.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                                resolvedUsername = await efName.GetUsernameByUidAsync(targetUid) ?? string.Empty;
                            }
                            else
                            {
                                resolvedUsername = Store.StaffUsers.Where(kv => string.Equals(kv.Value.Uid, targetUid, StringComparison.Ordinal)).Select(kv => kv.Key).FirstOrDefault() ?? string.Empty;
                            }
                        }
                        if (string.IsNullOrWhiteSpace(resolvedUsername)) resolvedUsername = !string.IsNullOrWhiteSpace(targetUid) ? targetUid : string.Empty;
                        if (string.IsNullOrWhiteSpace(resolvedUsername)) { await WebSocketStore.SendAsync(ws, new { type = "club.invite.fail", code = 400 }); app.Logger.LogDebug("WS club.invite.fail reason=missing target"); continue; }
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            if (!string.IsNullOrWhiteSpace(targetUid))
                            {
                                var unameByUid = await efSvc.GetUsernameByUidAsync(targetUid);
                                if (string.IsNullOrWhiteSpace(unameByUid))
                                {
                                    await WebSocketStore.SendAsync(ws, new { type = "club.invite.fail", code = 404, message = "Unknown UID" });
                                    app.Logger.LogDebug($"WS club.invite.fail club={clubIdCur} reason=unknown_uid uid={targetUid}");
                                    continue;
                                }
                                var existsMembership = await efSvc.UserExistsAsync(clubIdCur, unameByUid!);
                                if (!existsMembership)
                                {
                                    var added = await efSvc.AddStaffMembershipByUidAsync(clubIdCur, targetUid, inviteJobs);
                                    if (!added)
                                    {
                                        await WebSocketStore.SendAsync(ws, new { type = "club.invite.fail", code = 409, message = "Already member" });
                                        app.Logger.LogDebug($"WS club.invite.fail club={clubIdCur} reason=exists uid={targetUid}");
                                        continue;
                                    }
                                }
                                else
                                {
                                    await efSvc.UpdateStaffUserJobsAsync(clubIdCur, unameByUid!, inviteJobs);
                                }
                                resolvedUsername = unameByUid!;
                            }
                            else
                            {
                                var existsMembership = await efSvc.UserExistsAsync(clubIdCur, resolvedUsername);
                                if (!existsMembership)
                                {
                                    var existingUserAny = await efSvc.GetStaffUserByUsernameAsync(resolvedUsername);
                                    var hash = existingUserAny?.PasswordHash ?? Util.HashPassword(resolvedUsername, string.Empty);
                                    await efSvc.CreateStaffUserAsync(clubIdCur, resolvedUsername, hash);
                                }
                                await efSvc.UpdateStaffUserJobsAsync(clubIdCur, resolvedUsername, inviteJobs);
                            }
                            using var scopeEfWs2 = app.Services.CreateScope();
                            var efSvcWs2 = scopeEfWs2.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            var list = await efSvcWs2.GetStaffUsersAsync(clubIdCur) ?? Array.Empty<StaffUserInfo>();
                            var rightsDb = await efSvcWs2.GetJobRightsAsync(clubIdCur);
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "users.list", users = list.Select(u => u.Username).OrderBy(u => u, StringComparer.Ordinal).ToArray() });
                            var sockets = WebSocketStore.GetSocketsForClub(clubIdCur);
                            var ordered = list.OrderBy(u => u.Username, StringComparer.Ordinal).ToArray();
                            for (int i = 0; i < sockets.Length; i++)
                            {
                                var viewer = ResolveViewerUsername(sockets[i].Id);
                                var users = ordered.Select(u => BuildStaffUserPayload(u, rightsDb, viewer)).ToArray();
                                await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
                            }
                            var primary = GetPrimaryJob(rightsDb, EnsureJobs(inviteJobs));
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "user.update", username = resolvedUsername, jobs = inviteJobs, job = primary });
                            await WebSocketStore.BroadcastAsync(new { type = "membership.added", username = resolvedUsername, clubId = clubIdCur });
                        }
                        else
                        {
                            Store.SetJobsForUser(clubIdCur, resolvedUsername, inviteJobs);
                            if (!Store.StaffUsers.TryGetValue(resolvedUsername, out var info))
                            {
                                info = new StaffUserInfo { Username = resolvedUsername, PasswordHash = Util.HashPassword(resolvedUsername, string.Empty), Jobs = EnsureJobs(inviteJobs), Role = "power", CreatedAt = DateTimeOffset.UtcNow, Uid = string.IsNullOrWhiteSpace(targetUid) ? resolvedUsername : targetUid };
                            }
                            else
                            {
                                info.Jobs = EnsureJobs(inviteJobs);
                                if (!string.IsNullOrWhiteSpace(targetUid)) info.Uid = targetUid;
                            }
                            Store.StaffUsers[resolvedUsername] = info;
                            await Persistence.SaveAsync();
                            var usersClub = Store.ClubUserJobs.Keys.Where(k => k.StartsWith(clubIdCur + "|", StringComparison.Ordinal)).Select(k => k.Substring(clubIdCur.Length + 1)).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
                            var rightsMap = Store.JobRights.ToDictionary(kv => kv.Key, kv => kv.Value);
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "users.list", users = usersClub });
                            var sockets = WebSocketStore.GetSocketsForClub(clubIdCur);
                            for (int i = 0; i < sockets.Length; i++)
                            {
                                var viewer = ResolveViewerUsername(sockets[i].Id);
                                var users = usersClub.Select(u => BuildStaffUserPayloadFromStore(clubIdCur, u, rightsMap, viewer)).ToArray();
                                await WebSocketStore.SendAsync(sockets[i].Socket, new { type = "users.details", users });
                            }
                            var primary = GetPrimaryJob(rightsMap, EnsureJobs(inviteJobs));
                            await WebSocketStore.BroadcastToClubAsync(clubIdCur, new { type = "user.update", username = resolvedUsername, jobs = inviteJobs, job = primary });
                            await WebSocketStore.BroadcastAsync(new { type = "membership.added", username = resolvedUsername, clubId = clubIdCur });
                        }
                        await WebSocketStore.SendAsync(ws, new { type = "club.invite.ok" });
                        app.Logger.LogDebug($"WS club.invite.ok club={clubIdCur} target={resolvedUsername} job={inviteJobs[0]}");
                        continue;
                    }
                }
            }
            catch (Exception ex) { app.Logger.LogDebug($"WS handler error: {ex.Message}"); }
            finally
            {
                var drainedTokens = WebSocketStore.DrainSessions(id);
                if (drainedTokens.Length > 0)
                {
                    var affectedUsers = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < drainedTokens.Length; i++)
                    {
                        var tok = drainedTokens[i];
                        if (string.IsNullOrWhiteSpace(tok)) continue;
                        if (Store.StaffSessions.TryRemove(tok, out var user))
                        {
                            Store.StaffSessionExpiry.TryRemove(tok, out _);
                            if (!string.IsNullOrWhiteSpace(user)) affectedUsers.Add(user);
                        }
                        else
                        {
                            Store.StaffSessionExpiry.TryRemove(tok, out _);
                        }
                    }
                    if (affectedUsers.Count > 0)
                    {
                        if (!string.IsNullOrWhiteSpace(conn))
                        {
                            using var scopeEf = app.Services.CreateScope();
                            var efSvc = scopeEf.ServiceProvider.GetRequiredService<VenuePlus.Server.Services.EfStore>();
                            foreach (var user in affectedUsers)
                            {
                                var clubs = await efSvc.GetUserClubsAsync(user) ?? Array.Empty<string>();
                                for (int i = 0; i < clubs.Length; i++)
                                {
                                    await BroadcastUsersDetailsForClubDbAsync(clubs[i], efSvc);
                                }
                            }
                        }
                        else
                        {
                            foreach (var user in affectedUsers)
                            {
                                var clubsMem = GetUserClubsFromStore(user);
                                for (int i = 0; i < clubsMem.Length; i++)
                                {
                                    await BroadcastUsersDetailsForClubMemAsync(clubsMem[i]);
                                }
                            }
                        }
                    }
                }
                WebSocketStore.Remove(id);
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", app.Lifetime.ApplicationStopping); } catch (Exception ex) { app.Logger.LogDebug($"WS close failed: {ex.Message}"); }
            }
        });
        app.Lifetime.ApplicationStopping.Register(() => { _ = WebSocketStore.CloseAllAsync(); });
    }
}
