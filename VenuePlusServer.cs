using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http.Json;
using System.Net.WebSockets;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using VenuePlus.Server.Data;
using VenuePlus.Server.Services;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;

namespace VenuePlus.Server;

public class Program
{
    public static async System.Threading.Tasks.Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        HttpPipeline.AddCommonServices(builder);

        var defaultPassConf = builder.Configuration["Admin:DefaultStaffPassword"] ?? (Environment.GetEnvironmentVariable("VENUEPLUS_DEFAULT_STAFF_PASS") ?? Environment.GetEnvironmentVariable("VENUEPLUS_DEFAULT_STAFF_PASS") ?? string.Empty);
        var conn = builder.Configuration["Database:ConnectionString"] ?? (Environment.GetEnvironmentVariable("VENUEPLUS_DB_CONNECTION") ?? Environment.GetEnvironmentVariable("VENUEPLUS_DB_CONNECTION") ?? string.Empty);
        var urlConf = builder.Configuration["Server:Url"] ?? string.Empty;
        var portConf = builder.Configuration.GetValue<int?>("Server:Port");
        var envUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(envUrls)) builder.WebHost.UseUrls(envUrls);
        else if (!string.IsNullOrWhiteSpace(urlConf)) builder.WebHost.UseUrls(urlConf);
        else if (portConf.HasValue && portConf.Value > 0) builder.WebHost.UseUrls($"http://0.0.0.0:{portConf.Value}");
        else builder.WebHost.UseUrls("http://0.0.0.0:5000");
        if (!string.IsNullOrWhiteSpace(conn))
        {
            builder.Services.AddDbContext<VenuePlusDbContext>(o => o.UseNpgsql(conn));
            builder.Services.AddScoped<EfStore>();
        }
        var app = builder.Build();
        HttpPipeline.UseCommonMiddleware(app);

        if (string.IsNullOrWhiteSpace(conn)) Persistence.Load();
        InitializeDefaults(defaultPassConf);
        if (!string.IsNullOrWhiteSpace(conn))
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VenuePlusDbContext>();
            try { db.Database.Migrate(); }
            catch (Exception ex) { app.Logger.LogDebug($"DB migrate failed: {ex.Message}"); db.Database.EnsureCreated(); }
            var defaultClub = "default";
            var efSvcInit = scope.ServiceProvider.GetRequiredService<EfStore>();
            await efSvcInit.EnsureDefaultsAsync(defaultClub, defaultPassConf);
            var jobs = await efSvcInit.GetJobRightsAsync(defaultClub);
            if (jobs.Count > 0)
            {
                Store.JobRights.Clear();
                foreach (var kv in jobs) Store.JobRights[kv.Key] = kv.Value;
            }
            var staff = await efSvcInit.GetStaffUsersAsync(defaultClub);
            if (staff.Length > 0)
            {
                Store.StaffUsers.Clear();
                foreach (var u in staff) Store.StaffUsers[u.Username] = u;
            }
            var vip = await efSvcInit.LoadVipEntriesAsync(defaultClub);
            Store.VipEntries.Clear();
            var setVipDefault = Store.ClubVipKeys.GetOrAdd(defaultClub, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            foreach (var e in vip) { Store.VipEntries[e.Key] = e; setVipDefault[e.Key] = 1; }
        }

        PublicEndpoints.Map(app, conn);
        WebSocketMiddleware.Use(app, conn);
        await app.RunAsync();
    }

    private static void InitializeDefaults(string defaultStaffPassword)
    {
        if (Store.JobRights.IsEmpty)
        {
            Store.JobRights["Unassigned"] = new Rights();
            Store.JobRights["Greeter"] = new Rights();
            Store.JobRights["Barkeeper"] = new Rights();
            Store.JobRights["Dancer"] = new Rights();
            Store.JobRights["Escort"] = new Rights();
            Store.JobRights["Owner"] = new Rights { AddVip = true, RemoveVip = true, ManageUsers = true, ManageJobs = true };
        }
        if (Store.StaffUsers.IsEmpty)
        {
            if (!string.IsNullOrWhiteSpace(defaultStaffPassword))
            {
                Store.StaffUsers["staff"] = new StaffUserInfo
                {
                    Username = "staff",
                    PasswordHash = Util.HashPassword("staff", defaultStaffPassword),
                    Job = "Unassigned",
                    Role = "power",
                    CreatedAt = DateTimeOffset.UtcNow
                };
            }
        }
    }
}
