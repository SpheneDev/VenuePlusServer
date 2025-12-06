using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VenuePlus.Server.Data;
using VenuePlus.Server;

namespace VenuePlus.Server.Services;

public sealed class EfStore
{
    private readonly VenuePlusDbContext _db;
    private readonly EncryptionService _crypto;

    public EfStore(VenuePlusDbContext db, EncryptionService crypto)
    {
        _db = db;
        _crypto = crypto;
    }

    public async Task EnsureDefaultsAsync(string clubId, string? defaultStaffPass)
    {
        if (!await _db.JobRights.AnyAsync(j => j.ClubId == clubId))
        {
            var names = new[] { "Unassigned", "Greeter", "Barkeeper", "Dancer", "Escort", "Owner" };
            foreach (var n in names)
            {
                var e = new JobRightEntity { ClubId = clubId, JobName = n };
                if (string.Equals(n, "Owner", StringComparison.Ordinal))
                {
                    e.AddVip = true; e.RemoveVip = true; e.ManageUsers = true; e.ManageJobs = true; e.EditVipDuration = true; e.AddDj = true; e.RemoveDj = true;
                    e.Rank = 10;
                }
                else if (string.Equals(n, "Unassigned", StringComparison.Ordinal))
                {
                    e.Rank = 0;
                }
                else
                {
                    e.Rank = 1;
                }
                _db.JobRights.Add(e);
            }
            await _db.SaveChangesAsync();
        }
        if (!await _db.StaffUsers.AnyAsync(u => u.ClubId == clubId) && !string.IsNullOrWhiteSpace(defaultStaffPass))
        {
            var existingUser = await _db.BaseUsers.FirstOrDefaultAsync(x => x.Username == "staff");
            if (existingUser == null)
            {
                existingUser = new BaseUserEntity { Uid = Util.NewUid(), Username = "staff", PasswordHash = Util.HashPassword("staff", defaultStaffPass!), CreatedAt = DateTimeOffset.UtcNow };
                _db.BaseUsers.Add(existingUser);
                await _db.SaveChangesAsync();
            }
            var existsMembership = await _db.StaffUsers.AnyAsync(x => x.ClubId == clubId && x.UserUid == existingUser.Uid);
            if (!existsMembership)
            {
                _db.StaffUsers.Add(new StaffUserEntity { ClubId = clubId, UserUid = existingUser.Uid, Job = "Unassigned", Role = "power", CreatedAt = DateTimeOffset.UtcNow });
                await _db.SaveChangesAsync();
            }
        }
    }

    public async Task NormalizeAllJobRanksAsync()
    {
        var list = await _db.JobRights.ToListAsync();
        bool changed = false;
        foreach (var j in list)
        {
            if (string.Equals(j.JobName, "Owner", StringComparison.Ordinal))
            {
                if (j.Rank != 10) { j.Rank = 10; changed = true; _db.JobRights.Update(j); }
            }
            else if (string.Equals(j.JobName, "Unassigned", StringComparison.Ordinal))
            {
                if (j.Rank != 0) { j.Rank = 0; changed = true; _db.JobRights.Update(j); }
            }
            else
            {
                if (j.Rank <= 0) { j.Rank = 1; changed = true; _db.JobRights.Update(j); }
            }
        }
        if (changed) await _db.SaveChangesAsync();
    }

    

    public async Task<VipEntry[]> LoadVipEntriesAsync(string clubId)
    {
        var list = await _db.VipEntries.Where(e => e.ClubId == clubId).ToListAsync();
        var res = new List<VipEntry>(list.Count);
        foreach (var e in list)
        {
            var name = _crypto.DecryptString(e.CharacterName);
            var world = _crypto.DecryptString(e.HomeWorld);
            res.Add(new VipEntry { CharacterName = name, HomeWorld = world, CreatedAt = e.CreatedAt, ExpiresAt = e.ExpiresAt, Duration = e.Duration });
        }
        return res.OrderBy(x => x.CharacterName, StringComparer.Ordinal).ToArray();
    }

    public async Task<DjEntry[]> LoadDjEntriesAsync(string clubId)
    {
        try
        {
            var list = await _db.DjEntries.Where(e => e.ClubId == clubId).OrderBy(e => e.DjName).ToListAsync();
            return list.Select(e => new DjEntry
            {
                DjName = e.DjName,
                TwitchLink = e.TwitchLink,
                CreatedAt = e.CreatedAt
            }).ToArray();
        }
        catch
        {
            return Array.Empty<DjEntry>();
        }
    }

    public async Task PersistAddVipAsync(string clubId, VipEntry entry)
    {
        var list = await _db.VipEntries.Where(e => e.ClubId == clubId).ToListAsync();
        var tgt = list.FirstOrDefault(e => string.Equals(_crypto.DecryptString(e.CharacterName), entry.CharacterName, StringComparison.Ordinal)
                                         && string.Equals(_crypto.DecryptString(e.HomeWorld), entry.HomeWorld, StringComparison.Ordinal));
        if (tgt == null)
        {
            _db.VipEntries.Add(new VipEntryEntity
            {
                ClubId = clubId,
                CharacterName = _crypto.EncryptDeterministic(entry.CharacterName, "vip:" + clubId),
                HomeWorld = _crypto.EncryptDeterministic(entry.HomeWorld, "vip:" + clubId),
                CreatedAt = entry.CreatedAt,
                ExpiresAt = entry.ExpiresAt,
                Duration = entry.Duration
            });
        }
        else
        {
            tgt.CreatedAt = entry.CreatedAt;
            tgt.ExpiresAt = entry.ExpiresAt;
            tgt.Duration = entry.Duration;
            _db.VipEntries.Update(tgt);
        }
        await _db.SaveChangesAsync();
    }

    public async Task PersistRemoveVipAsync(string clubId, string characterName, string homeWorld)
    {
        var list = await _db.VipEntries.Where(x => x.ClubId == clubId).ToListAsync();
        var e = list.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.CharacterName), characterName, StringComparison.Ordinal)
                                      && string.Equals(_crypto.DecryptString(x.HomeWorld), homeWorld, StringComparison.Ordinal));
        if (e == null) return;
        _db.VipEntries.Remove(e);
        await _db.SaveChangesAsync();
    }

    public async Task PersistAddOrUpdateDjAsync(string clubId, DjEntry entry)
    {
        try
        {
            var exists = await _db.DjEntries.AnyAsync(e => e.ClubId == clubId && e.DjName == entry.DjName);
            if (!exists)
            {
                _db.DjEntries.Add(new DjEntryEntity
                {
                    ClubId = clubId,
                    DjName = entry.DjName,
                    TwitchLink = entry.TwitchLink ?? string.Empty,
                    CreatedAt = entry.CreatedAt
                });
            }
            else
            {
                var e = await _db.DjEntries.FirstAsync(x => x.ClubId == clubId && x.DjName == entry.DjName);
                e.TwitchLink = entry.TwitchLink ?? string.Empty;
                e.CreatedAt = entry.CreatedAt;
                _db.DjEntries.Update(e);
            }
            await _db.SaveChangesAsync();
        }
        catch { }
    }

    public async Task PersistRemoveDjAsync(string clubId, string djName)
    {
        try
        {
            var e = await _db.DjEntries.FirstOrDefaultAsync(x => x.ClubId == clubId && x.DjName == djName);
            if (e != null)
            {
                _db.DjEntries.Remove(e);
                await _db.SaveChangesAsync();
            }
        }
        catch { }
    }

    public async Task<StaffUserInfo?> GetStaffUserAsync(string clubId, string username)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return null;
        var member = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.UserUid == baseUser.Uid);
        if (member == null) return null;
        return new StaffUserInfo { Username = _crypto.DecryptString(baseUser.Username), PasswordHash = baseUser.PasswordHash, Job = member.Job, Role = member.Role, CreatedAt = member.CreatedAt };
    }

    public async Task<StaffUserInfo?> GetStaffUserByUsernameAsync(string username)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return null;
        return new StaffUserInfo { Username = _crypto.DecryptString(baseUser.Username), PasswordHash = baseUser.PasswordHash, Job = "Unassigned", Role = "power", CreatedAt = baseUser.CreatedAt, Uid = baseUser.Uid };
    }

    public async Task<string?> GetUsernameByUidAsync(string uid)
    {
        var baseUser = await _db.BaseUsers.FirstOrDefaultAsync(x => x.Uid == uid);
        return baseUser == null ? null : _crypto.DecryptString(baseUser.Username);
    }

    public async Task<StaffUserInfo[]> GetStaffUsersAsync(string clubId)
    {
        var list = await _db.StaffUsers.Where(x => x.ClubId == clubId).ToListAsync();
        var uids = list.Select(x => x.UserUid).Distinct().ToArray();
        var baseUsers = await _db.BaseUsers.Where(b => uids.Contains(b.Uid)).ToDictionaryAsync(b => b.Uid, b => b);
        return list.OrderBy(x => baseUsers.TryGetValue(x.UserUid, out var bu) ? _crypto.DecryptString(bu.Username) : x.UserUid, StringComparer.Ordinal).Select(u => new StaffUserInfo
        {
            Username = (baseUsers.TryGetValue(u.UserUid, out var bu) ? _crypto.DecryptString(bu.Username) : u.UserUid),
            PasswordHash = (baseUsers.TryGetValue(u.UserUid, out var bu2) ? bu2.PasswordHash : string.Empty),
            Job = u.Job,
            Role = u.Role,
            CreatedAt = u.CreatedAt,
            Uid = u.UserUid
        }).ToArray();
    }

    public async Task CreateStaffUserAsync(string clubId, string username, string passwordHash)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null)
        {
            baseUser = new BaseUserEntity { Uid = Util.NewUid(), Username = _crypto.EncryptDeterministic(username, "user"), PasswordHash = passwordHash, CreatedAt = DateTimeOffset.UtcNow };
            _db.BaseUsers.Add(baseUser);
            await _db.SaveChangesAsync();
        }
        var exists = await _db.StaffUsers.AnyAsync(x => x.ClubId == clubId && x.UserUid == baseUser.Uid);
        if (exists) return;
        _db.StaffUsers.Add(new StaffUserEntity { ClubId = clubId, UserUid = baseUser.Uid, Job = "Unassigned", Role = "power", CreatedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();
    }

    public async Task<bool> CreateBaseUserAsync(string username, string passwordHash)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var exists = buList.Any(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (exists) return false;
        var baseUser = new BaseUserEntity { Uid = Util.NewUid(), Username = _crypto.EncryptDeterministic(username, "user"), PasswordHash = passwordHash, CreatedAt = DateTimeOffset.UtcNow };
        _db.BaseUsers.Add(baseUser);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task DeleteStaffUserAsync(string clubId, string username)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return;
        var u = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.UserUid == baseUser.Uid);
        if (u != null)
        {
            _db.StaffUsers.Remove(u);
            await _db.SaveChangesAsync();
        }
    }

    public async Task UpdateStaffUserJobAsync(string clubId, string username, string job)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return;
        var u = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.UserUid == baseUser.Uid);
        if (u != null)
        {
            u.Job = job;
            _db.StaffUsers.Update(u);
            await _db.SaveChangesAsync();
        }
    }

    public async Task UpdateStaffUserRoleAsync(string clubId, string username, string role)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return;
        var u = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.UserUid == baseUser.Uid);
        if (u != null)
        {
            u.Role = role;
            _db.StaffUsers.Update(u);
            await _db.SaveChangesAsync();
        }
    }

    public async Task UpdateStaffPasswordAsync(string clubId, string username, string newHash)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return;
        baseUser.PasswordHash = newHash;
        _db.BaseUsers.Update(baseUser);
        await _db.SaveChangesAsync();
    }

    public async Task<string?> GetStaffPasswordHashAsync(string clubId, string username)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        return baseUser?.PasswordHash;
    }

    public async Task<Dictionary<string, Rights>> GetJobRightsAsync(string clubId)
    {
        var list = await _db.JobRights.Where(x => x.ClubId == clubId).ToListAsync();
        var dict = new Dictionary<string, Rights>(StringComparer.Ordinal);
        foreach (var j in list)
        {
            int rankVal;
            if (string.Equals(j.JobName, "Owner", StringComparison.Ordinal)) rankVal = 10;
            else if (string.Equals(j.JobName, "Unassigned", StringComparison.Ordinal)) rankVal = 0;
            else rankVal = j.Rank <= 0 ? 1 : (j.Rank > 9 ? 9 : j.Rank);
            dict[j.JobName] = new Rights { AddVip = j.AddVip, RemoveVip = j.RemoveVip, ManageUsers = j.ManageUsers, ManageJobs = j.ManageJobs, EditVipDuration = j.EditVipDuration, AddDj = j.AddDj, RemoveDj = j.RemoveDj, Rank = rankVal, ColorHex = j.ColorHex, IconKey = j.IconKey };
        }
        if (!dict.TryGetValue("Owner", out var own))
        {
            dict["Owner"] = new Rights { AddVip = true, RemoveVip = true, ManageUsers = true, ManageJobs = true, EditVipDuration = true, AddDj = true, RemoveDj = true, Rank = 10, ColorHex = dict.TryGetValue("Owner", out var ex) ? (ex.ColorHex ?? "#FFFFFF") : "#FFFFFF", IconKey = dict.TryGetValue("Owner", out var ex2) ? (ex2.IconKey ?? "User") : "User" };
        }
        else
        {
            own.AddVip = true; own.RemoveVip = true; own.ManageUsers = true; own.ManageJobs = true; own.EditVipDuration = true; own.AddDj = true; own.RemoveDj = true; own.Rank = 10;
            dict["Owner"] = own;
        }
        return dict;
    }

    public async Task<string[]> GetJobsAsync(string clubId)
    {
        return await _db.JobRights.Where(x => x.ClubId == clubId).Select(x => x.JobName).Distinct().OrderBy(x => x).ToArrayAsync();
    }

    public async Task UpdateJobRightsAsync(string clubId, string name, Rights rights)
    {
        var j = await _db.JobRights.FirstOrDefaultAsync(x => x.ClubId == clubId && x.JobName == name);
        if (j == null)
        {
            j = new JobRightEntity { ClubId = clubId, JobName = name };
            _db.JobRights.Add(j);
        }
        var isOwner = string.Equals(name, "Owner", StringComparison.Ordinal);
        j.AddVip = isOwner ? true : rights.AddVip;
        j.RemoveVip = isOwner ? true : rights.RemoveVip;
        j.ManageUsers = isOwner ? true : rights.ManageUsers;
        j.ManageJobs = isOwner ? true : rights.ManageJobs;
        j.EditVipDuration = isOwner ? true : rights.EditVipDuration;
        j.AddDj = isOwner ? true : rights.AddDj;
        j.RemoveDj = isOwner ? true : rights.RemoveDj;
        if (isOwner) j.Rank = 10;
        else if (string.Equals(name, "Unassigned", StringComparison.Ordinal)) j.Rank = 0;
        else j.Rank = rights.Rank <= 0 ? 1 : (rights.Rank > 9 ? 9 : rights.Rank);
        j.ColorHex = rights.ColorHex ?? "#FFFFFF";
        j.IconKey = rights.IconKey ?? "User";
        _db.JobRights.Update(j);
        await _db.SaveChangesAsync();
    }

    public async Task<bool> ExistsVipAsync(string clubId, string characterName, string homeWorld)
    {
        var list = await _db.VipEntries.Where(e => e.ClubId == clubId).ToListAsync();
        return list.Any(e => string.Equals(_crypto.DecryptString(e.CharacterName), characterName, StringComparison.Ordinal)
                          && string.Equals(_crypto.DecryptString(e.HomeWorld), homeWorld, StringComparison.Ordinal));
    }

    public async Task AddJobAsync(string clubId, string name)
    {
        var exists = await _db.JobRights.AnyAsync(x => x.ClubId == clubId && x.JobName == name);
        if (exists) return;
        _db.JobRights.Add(new JobRightEntity { ClubId = clubId, JobName = name, Rank = 1 });
        await _db.SaveChangesAsync();
    }

    public async Task DeleteJobAsync(string clubId, string name)
    {
        var j = await _db.JobRights.FirstOrDefaultAsync(x => x.ClubId == clubId && x.JobName == name);
        if (j != null)
        {
            _db.JobRights.Remove(j);
            await _db.SaveChangesAsync();
        }
    }

    

    public async Task<string[]> GetUserClubsAsync(string username)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return Array.Empty<string>();
        return await _db.StaffUsers.Where(x => x.UserUid == baseUser.Uid).Select(x => x.ClubId).Distinct().OrderBy(x => x).ToArrayAsync();
    }

    public async Task<bool> UserExistsAsync(string clubId, string username)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return false;
        return await _db.StaffUsers.AnyAsync(x => x.ClubId == clubId && x.UserUid == baseUser.Uid);
    }

    public async Task<bool> ClubExistsAsync(string clubId)
    {
        return await _db.Clubs.AnyAsync(x => x.ClubId == clubId);
    }

    public async Task AddClubIfMissingAsync(string clubId, string? creatorUsername)
    {
        var exists = await _db.Clubs.AnyAsync(x => x.ClubId == clubId);
        if (exists) return;
        _db.Clubs.Add(new ClubEntity
        {
            ClubId = clubId,
            CreatedByUsername = string.IsNullOrWhiteSpace(creatorUsername) ? string.Empty : _crypto.EncryptDeterministic(creatorUsername!, "club:" + clubId),
            CreatedAt = DateTimeOffset.UtcNow,
            AccessKey = VenuePlus.Server.Util.NewUid(24)
        });
        await _db.SaveChangesAsync();
    }

    public async Task<string[]> GetCreatedClubsAsync(string username)
    {
        var list = await _db.Clubs.ToListAsync();
        return list.Where(x => string.Equals(_crypto.DecryptString(x.CreatedByUsername), username, StringComparison.Ordinal)).Select(x => x.ClubId).OrderBy(x => x).ToArray();
    }

    public async Task DeleteClubAsync(string clubId)
    {
        var vip = await _db.VipEntries.Where(e => e.ClubId == clubId).ToListAsync();
        if (vip.Count > 0) _db.VipEntries.RemoveRange(vip);
        var members = await _db.StaffUsers.Where(s => s.ClubId == clubId).ToListAsync();
        if (members.Count > 0) _db.StaffUsers.RemoveRange(members);
        var rights = await _db.JobRights.Where(r => r.ClubId == clubId).ToListAsync();
        if (rights.Count > 0) _db.JobRights.RemoveRange(rights);
        var club = await _db.Clubs.FirstOrDefaultAsync(c => c.ClubId == clubId);
        if (club != null) _db.Clubs.Remove(club);
        await _db.SaveChangesAsync();
    }

    public async Task UpgradeEncryptionAsync()
    {
        var users = await _db.BaseUsers.ToListAsync();
        foreach (var u in users)
        {
            if (!_crypto.IsEncrypted(u.Username))
            {
                u.Username = _crypto.EncryptDeterministic(u.Username, "user");
                _db.BaseUsers.Update(u);
            }
        }
        var clubs = await _db.Clubs.ToListAsync();
        foreach (var c in clubs)
        {
            if (!string.IsNullOrWhiteSpace(c.CreatedByUsername) && !_crypto.IsEncrypted(c.CreatedByUsername))
            {
                c.CreatedByUsername = _crypto.EncryptDeterministic(c.CreatedByUsername, "club:" + c.ClubId);
                _db.Clubs.Update(c);
            }
        }
        var vips = await _db.VipEntries.ToListAsync();
        foreach (var v in vips)
        {
            if (!_crypto.IsEncrypted(v.CharacterName))
            {
                v.CharacterName = _crypto.EncryptDeterministic(v.CharacterName, "vip:" + v.ClubId);
            }
            if (!_crypto.IsEncrypted(v.HomeWorld))
            {
                v.HomeWorld = _crypto.EncryptDeterministic(v.HomeWorld, "vip:" + v.ClubId);
            }
            _db.VipEntries.Update(v);
        }
        await _db.SaveChangesAsync();
    }

    public async Task<string?> GetAccessKeyAsync(string clubId)
    {
        var club = await _db.Clubs.FirstOrDefaultAsync(c => c.ClubId == clubId);
        if (club == null) return null;
        if (string.IsNullOrWhiteSpace(club.AccessKey))
        {
            club.AccessKey = VenuePlus.Server.Util.NewUid(24);
            _db.Clubs.Update(club);
            await _db.SaveChangesAsync();
        }
        return club.AccessKey;
    }

    public async Task<string?> GetClubIdByAccessKeyAsync(string accessKey)
    {
        var club = await _db.Clubs.FirstOrDefaultAsync(c => c.AccessKey == accessKey);
        return club?.ClubId;
    }

    public async Task<string?> RegenerateAccessKeyAsync(string clubId)
    {
        var club = await _db.Clubs.FirstOrDefaultAsync(c => c.ClubId == clubId);
        if (club == null) return null;
        club.AccessKey = VenuePlus.Server.Util.NewUid(24);
        _db.Clubs.Update(club);
        await _db.SaveChangesAsync();
        return club.AccessKey;
    }

    public async Task<string?> GetClubLogoAsync(string clubId)
    {
        var club = await _db.Clubs.FirstOrDefaultAsync(c => c.ClubId == clubId);
        return club?.LogoBase64;
    }

    public async Task SetClubLogoAsync(string clubId, string? logoBase64)
    {
        var club = await _db.Clubs.FirstOrDefaultAsync(c => c.ClubId == clubId);
        if (club == null)
        {
            club = new ClubEntity { ClubId = clubId, CreatedByUsername = string.Empty, CreatedAt = DateTimeOffset.UtcNow };
            _db.Clubs.Add(club);
        }
        club.LogoBase64 = logoBase64;
        _db.Clubs.Update(club);
        await _db.SaveChangesAsync();
    }

    public async Task SetClubJoinPasswordAsync(string clubId, string? joinPasswordHash)
    {
        var club = await _db.Clubs.FirstOrDefaultAsync(c => c.ClubId == clubId);
        if (club == null)
        {
            club = new ClubEntity { ClubId = clubId, CreatedByUsername = string.Empty, CreatedAt = DateTimeOffset.UtcNow };
            _db.Clubs.Add(club);
        }
        club.JoinPasswordHash = joinPasswordHash;
        _db.Clubs.Update(club);
        await _db.SaveChangesAsync();
    }

    public async Task<string?> GetClubJoinPasswordHashAsync(string clubId)
    {
        var club = await _db.Clubs.FirstOrDefaultAsync(c => c.ClubId == clubId);
        return club?.JoinPasswordHash;
    }

    public async Task<bool> AddStaffMembershipByUidAsync(string clubId, string targetUid, string job)
    {
        var baseUser = await _db.BaseUsers.FirstOrDefaultAsync(x => x.Uid == targetUid);
        if (baseUser == null) return false;
        var exists = await _db.StaffUsers.AnyAsync(x => x.ClubId == clubId && x.UserUid == baseUser.Uid);
        if (exists) return false;
        _db.StaffUsers.Add(new StaffUserEntity { ClubId = clubId, UserUid = baseUser.Uid, Job = string.IsNullOrWhiteSpace(job) ? "Unassigned" : job, Role = "power", CreatedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();
        return true;
    }
}
