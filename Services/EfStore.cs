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
                _db.StaffUsers.Add(new StaffUserEntity { ClubId = clubId, UserUid = existingUser.Uid, Role = "power", CreatedAt = DateTimeOffset.UtcNow });
                _db.StaffUserJobs.Add(new StaffUserJobEntity { ClubId = clubId, UserUid = existingUser.Uid, JobName = "Unassigned" });
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
                CreatedAt = e.CreatedAt,
                StartAt = e.StartAt,
                EndAt = e.EndAt
            }).ToArray();
        }
        catch
        {
            return Array.Empty<DjEntry>();
        }
    }

    public async Task<ShiftEntry[]> LoadShiftEntriesAsync(string clubId)
    {
        try
        {
            var list = await _db.Shifts.Where(e => e.ClubId == clubId).OrderBy(e => e.StartAt).ToListAsync();
            return list.Select(e => new ShiftEntry
            {
                Id = e.Id,
                Title = e.Title,
                DjName = e.DjName,
                AssignedUid = e.AssignedUid,
                Job = e.Job,
                StartAt = e.StartAt,
                EndAt = e.EndAt
            }).ToArray();
        }
        catch
        {
            return Array.Empty<ShiftEntry>();
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
                    CreatedAt = entry.CreatedAt,
                    StartAt = entry.StartAt,
                    EndAt = entry.EndAt
                });
            }
            else
            {
                var e = await _db.DjEntries.FirstAsync(x => x.ClubId == clubId && x.DjName == entry.DjName);
                e.TwitchLink = entry.TwitchLink ?? string.Empty;
                e.CreatedAt = entry.CreatedAt;
                e.StartAt = entry.StartAt;
                e.EndAt = entry.EndAt;
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

    public async Task<ShiftEntry> PersistAddOrUpdateShiftAsync(string clubId, ShiftEntry entry)
    {
        try
        {
            var exists = await _db.Shifts.AnyAsync(s => s.ClubId == clubId && s.Id == entry.Id);
            if (!exists)
            {
                var newId = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id;
                _db.Shifts.Add(new ShiftEntryEntity
                {
                    Id = newId,
                    ClubId = clubId,
                    Title = entry.Title ?? string.Empty,
                    DjName = string.IsNullOrWhiteSpace(entry.DjName) ? null : entry.DjName,
                    AssignedUid = string.IsNullOrWhiteSpace(entry.AssignedUid) ? null : entry.AssignedUid,
                    Job = string.IsNullOrWhiteSpace(entry.Job) ? null : entry.Job,
                    StartAt = entry.StartAt,
                    EndAt = entry.EndAt
                });
                entry.Id = newId;
            }
            else
            {
                var e = await _db.Shifts.FirstAsync(s => s.ClubId == clubId && s.Id == entry.Id);
                e.Title = entry.Title ?? string.Empty;
                e.DjName = string.IsNullOrWhiteSpace(entry.DjName) ? null : entry.DjName;
                e.AssignedUid = string.IsNullOrWhiteSpace(entry.AssignedUid) ? null : entry.AssignedUid;
                e.Job = string.IsNullOrWhiteSpace(entry.Job) ? null : entry.Job;
                e.StartAt = entry.StartAt;
                e.EndAt = entry.EndAt;
                _db.Shifts.Update(e);
            }
            await _db.SaveChangesAsync();
            return entry;
        }
        catch { return entry; }
    }

    public async Task PersistRemoveShiftAsync(string clubId, Guid id)
    {
        try
        {
            var e = await _db.Shifts.FirstOrDefaultAsync(s => s.ClubId == clubId && s.Id == id);
            if (e != null)
            {
                _db.Shifts.Remove(e);
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
        var jobs = await _db.StaffUserJobs.Where(x => x.ClubId == clubId && x.UserUid == baseUser.Uid).Select(x => x.JobName).ToArrayAsync();
        if (jobs.Length == 0) jobs = new[] { "Unassigned" };
        Array.Sort(jobs, StringComparer.Ordinal);
        return new StaffUserInfo { Username = _crypto.DecryptString(baseUser.Username), PasswordHash = baseUser.PasswordHash, Jobs = jobs, Role = member.Role, CreatedAt = member.CreatedAt, Uid = baseUser.Uid, Birthday = baseUser.Birthday };
    }

    public async Task<StaffUserInfo?> GetStaffUserByUsernameAsync(string username)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return null;
        return new StaffUserInfo { Username = _crypto.DecryptString(baseUser.Username), PasswordHash = baseUser.PasswordHash, Jobs = Array.Empty<string>(), Role = "power", CreatedAt = baseUser.CreatedAt, Uid = baseUser.Uid, Birthday = baseUser.Birthday };
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
        var jobList = await _db.StaffUserJobs.Where(x => x.ClubId == clubId).ToListAsync();
        var jobsByUser = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var j in jobList)
        {
            if (!jobsByUser.TryGetValue(j.UserUid, out var userJobs))
            {
                userJobs = new List<string>();
                jobsByUser[j.UserUid] = userJobs;
            }
            if (!userJobs.Contains(j.JobName)) userJobs.Add(j.JobName);
        }
        DateTimeOffset? GetBirthday(StaffUserEntity entry)
        {
            if (entry.IsManual) return entry.Birthday;
            return baseUsers.TryGetValue(entry.UserUid, out var bu) ? bu.Birthday : null;
        }
        string GetDisplayName(StaffUserEntity entry)
        {
            if (entry.IsManual) return string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.UserUid : entry.DisplayName;
            if (baseUsers.TryGetValue(entry.UserUid, out var bu)) return _crypto.DecryptString(bu.Username);
            return entry.UserUid;
        }
        string GetPasswordHash(StaffUserEntity entry)
        {
            return baseUsers.TryGetValue(entry.UserUid, out var bu) ? bu.PasswordHash : string.Empty;
        }
        return list.OrderBy(x => GetDisplayName(x), StringComparer.Ordinal).Select(u => new StaffUserInfo
        {
            Username = GetDisplayName(u),
            PasswordHash = GetPasswordHash(u),
            Jobs = GetSortedJobs(jobsByUser, u.UserUid),
            Role = u.Role,
            CreatedAt = u.CreatedAt,
            Uid = u.UserUid,
            IsManual = u.IsManual,
            Birthday = GetBirthday(u)
        }).ToArray();
    }

    public async Task<StaffUserInfo?> GetManualStaffUserAsync(string clubId, string displayName)
    {
        var entry = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.IsManual && x.DisplayName == displayName);
        if (entry == null) return null;
        var jobs = await _db.StaffUserJobs.Where(x => x.ClubId == clubId && x.UserUid == entry.UserUid).Select(x => x.JobName).ToArrayAsync();
        if (jobs.Length == 0) jobs = new[] { "Unassigned" };
        Array.Sort(jobs, StringComparer.Ordinal);
        return new StaffUserInfo
        {
            Username = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.UserUid : entry.DisplayName,
            PasswordHash = string.Empty,
            Jobs = jobs,
            Role = entry.Role,
            CreatedAt = entry.CreatedAt,
            Uid = entry.UserUid,
            IsManual = true,
            Birthday = entry.Birthday
        };
    }

    public async Task<StaffUserInfo?> GetStaffUserByUidAsync(string clubId, string uid)
    {
        var member = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.UserUid == uid);
        if (member == null) return null;
        var baseUser = await _db.BaseUsers.FirstOrDefaultAsync(x => x.Uid == uid);
        var jobs = await _db.StaffUserJobs.Where(x => x.ClubId == clubId && x.UserUid == uid).Select(x => x.JobName).ToArrayAsync();
        if (jobs.Length == 0) jobs = new[] { "Unassigned" };
        Array.Sort(jobs, StringComparer.Ordinal);
        var name = member.IsManual ? (string.IsNullOrWhiteSpace(member.DisplayName) ? member.UserUid : member.DisplayName) : (baseUser == null ? member.UserUid : _crypto.DecryptString(baseUser.Username));
        return new StaffUserInfo
        {
            Username = name,
            PasswordHash = baseUser?.PasswordHash ?? string.Empty,
            Jobs = jobs,
            Role = member.Role,
            CreatedAt = member.CreatedAt,
            Uid = member.UserUid,
            IsManual = member.IsManual,
            Birthday = member.IsManual ? member.Birthday : baseUser?.Birthday
        };
    }

    public async Task<StaffUserInfo?> CreateManualStaffEntryAsync(string clubId, string displayName, string[] jobs, DateTimeOffset? birthday)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        var name = displayName.Trim();
        var existingManual = await _db.StaffUsers.AnyAsync(x => x.ClubId == clubId && x.IsManual && x.DisplayName == name);
        if (existingManual) return null;
        var buList = await _db.BaseUsers.ToListAsync();
        var existsBase = buList.Any(x => string.Equals(_crypto.DecryptString(x.Username), name, StringComparison.Ordinal));
        if (existsBase) return null;
        string uid;
        do
        {
            uid = Util.NewUid();
        } while (await _db.BaseUsers.AnyAsync(x => x.Uid == uid) || await _db.StaffUsers.AnyAsync(x => x.ClubId == clubId && x.UserUid == uid));
        var now = DateTimeOffset.UtcNow;
        _db.StaffUsers.Add(new StaffUserEntity
        {
            ClubId = clubId,
            UserUid = uid,
            Role = "power",
            CreatedAt = now,
            IsManual = true,
            DisplayName = name,
            Birthday = birthday?.ToUniversalTime()
        });
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job)) continue;
            if (set.Add(job)) _db.StaffUserJobs.Add(new StaffUserJobEntity { ClubId = clubId, UserUid = uid, JobName = job });
        }
        if (set.Count == 0) _db.StaffUserJobs.Add(new StaffUserJobEntity { ClubId = clubId, UserUid = uid, JobName = "Unassigned" });
        await _db.SaveChangesAsync();
        var arr = set.Count == 0 ? new[] { "Unassigned" } : set.ToArray();
        Array.Sort(arr, StringComparer.Ordinal);
        return new StaffUserInfo
        {
            Username = name,
            PasswordHash = string.Empty,
            Jobs = arr,
            Role = "power",
            CreatedAt = now,
            Uid = uid,
            IsManual = true,
            Birthday = birthday?.ToUniversalTime()
        };
    }

    public async Task<StaffUserInfo?> LinkManualStaffEntryAsync(string clubId, string manualUid, string targetUid)
    {
        if (string.IsNullOrWhiteSpace(manualUid) || string.IsNullOrWhiteSpace(targetUid)) return null;
        var manual = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.UserUid == manualUid && x.IsManual);
        if (manual == null) return null;
        var baseUser = await _db.BaseUsers.FirstOrDefaultAsync(x => x.Uid == targetUid);
        if (baseUser == null) return null;
        var manualJobs = await _db.StaffUserJobs.Where(x => x.ClubId == clubId && x.UserUid == manualUid).ToListAsync();
        var targetEntry = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.UserUid == targetUid);
        if (targetEntry == null)
        {
            targetEntry = new StaffUserEntity
            {
                ClubId = clubId,
                UserUid = targetUid,
                Role = manual.Role,
                CreatedAt = manual.CreatedAt,
                IsManual = false,
                DisplayName = null
            };
            _db.StaffUsers.Add(targetEntry);
        }
        else
        {
            targetEntry.Role = manual.Role;
            targetEntry.IsManual = false;
            targetEntry.DisplayName = null;
            targetEntry.CreatedAt = manual.CreatedAt;
            _db.StaffUsers.Update(targetEntry);
        }
        var existingTargetJobs = await _db.StaffUserJobs.Where(x => x.ClubId == clubId && x.UserUid == targetUid).ToListAsync();
        if (existingTargetJobs.Count > 0) _db.StaffUserJobs.RemoveRange(existingTargetJobs);
        var jobSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var j in manualJobs)
        {
            if (string.IsNullOrWhiteSpace(j.JobName)) continue;
            if (jobSet.Add(j.JobName)) _db.StaffUserJobs.Add(new StaffUserJobEntity { ClubId = clubId, UserUid = targetUid, JobName = j.JobName });
        }
        if (jobSet.Count == 0) _db.StaffUserJobs.Add(new StaffUserJobEntity { ClubId = clubId, UserUid = targetUid, JobName = "Unassigned" });
        _db.StaffUsers.Remove(manual);
        if (manualJobs.Count > 0) _db.StaffUserJobs.RemoveRange(manualJobs);
        await _db.SaveChangesAsync();
        var username = _crypto.DecryptString(baseUser.Username);
        var jobArr = jobSet.Count == 0 ? new[] { "Unassigned" } : jobSet.ToArray();
        Array.Sort(jobArr, StringComparer.Ordinal);
        return new StaffUserInfo
        {
            Username = username,
            PasswordHash = baseUser.PasswordHash,
            Jobs = jobArr,
            Role = targetEntry.Role,
            CreatedAt = targetEntry.CreatedAt,
            Uid = targetUid,
            IsManual = false
        };
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
        _db.StaffUsers.Add(new StaffUserEntity { ClubId = clubId, UserUid = baseUser.Uid, Role = "power", CreatedAt = DateTimeOffset.UtcNow });
        _db.StaffUserJobs.Add(new StaffUserJobEntity { ClubId = clubId, UserUid = baseUser.Uid, JobName = "Unassigned" });
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
        if (baseUser != null)
        {
            var u = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.UserUid == baseUser.Uid);
            if (u != null)
            {
                _db.StaffUsers.Remove(u);
                var jobs = await _db.StaffUserJobs.Where(x => x.ClubId == clubId && x.UserUid == baseUser.Uid).ToListAsync();
                if (jobs.Count > 0) _db.StaffUserJobs.RemoveRange(jobs);
                await _db.SaveChangesAsync();
            }
            return;
        }
        var manual = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.IsManual && x.DisplayName == username);
        if (manual == null) return;
        _db.StaffUsers.Remove(manual);
        var manualJobs = await _db.StaffUserJobs.Where(x => x.ClubId == clubId && x.UserUid == manual.UserUid).ToListAsync();
        if (manualJobs.Count > 0) _db.StaffUserJobs.RemoveRange(manualJobs);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteBaseUserAsync(string username)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return;
        var members = await _db.StaffUsers.Where(x => x.UserUid == baseUser.Uid).ToListAsync();
        if (members.Count > 0) _db.StaffUsers.RemoveRange(members);
        var jobs = await _db.StaffUserJobs.Where(x => x.UserUid == baseUser.Uid).ToListAsync();
        if (jobs.Count > 0) _db.StaffUserJobs.RemoveRange(jobs);
        _db.BaseUsers.Remove(baseUser);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateStaffUserJobsAsync(string clubId, string username, string[] jobs)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser != null)
        {
            var existing = await _db.StaffUserJobs.Where(x => x.ClubId == clubId && x.UserUid == baseUser.Uid).ToListAsync();
            if (existing.Count > 0) _db.StaffUserJobs.RemoveRange(existing);
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var job in jobs)
            {
                if (string.IsNullOrWhiteSpace(job)) continue;
                if (set.Add(job)) _db.StaffUserJobs.Add(new StaffUserJobEntity { ClubId = clubId, UserUid = baseUser.Uid, JobName = job });
            }
            if (set.Count == 0) _db.StaffUserJobs.Add(new StaffUserJobEntity { ClubId = clubId, UserUid = baseUser.Uid, JobName = "Unassigned" });
            await _db.SaveChangesAsync();
            return;
        }
        var manual = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.IsManual && x.DisplayName == username);
        if (manual == null) return;
        var existingManual = await _db.StaffUserJobs.Where(x => x.ClubId == clubId && x.UserUid == manual.UserUid).ToListAsync();
        if (existingManual.Count > 0) _db.StaffUserJobs.RemoveRange(existingManual);
        var setManual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job)) continue;
            if (setManual.Add(job)) _db.StaffUserJobs.Add(new StaffUserJobEntity { ClubId = clubId, UserUid = manual.UserUid, JobName = job });
        }
        if (setManual.Count == 0) _db.StaffUserJobs.Add(new StaffUserJobEntity { ClubId = clubId, UserUid = manual.UserUid, JobName = "Unassigned" });
        await _db.SaveChangesAsync();
    }

    public async Task UpdateStaffUserRoleAsync(string clubId, string username, string role)
    {
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser != null)
        {
            var u = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.UserUid == baseUser.Uid);
            if (u != null)
            {
                u.Role = role;
                _db.StaffUsers.Update(u);
                await _db.SaveChangesAsync();
            }
            return;
        }
        var manual = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.IsManual && x.DisplayName == username);
        if (manual == null) return;
        manual.Role = role;
        _db.StaffUsers.Update(manual);
        await _db.SaveChangesAsync();
    }

    public async Task<DateTimeOffset?> GetUserBirthdayAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        return baseUser?.Birthday;
    }

    public async Task<bool> UpdateUserBirthdayAsync(string username, DateTimeOffset? birthday)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return false;
        var normalized = birthday.HasValue ? birthday.Value.ToUniversalTime() : (DateTimeOffset?)null;
        baseUser.Birthday = normalized;
        _db.BaseUsers.Update(baseUser);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpdateManualStaffBirthdayAsync(string clubId, string displayName, DateTimeOffset? birthday)
    {
        if (string.IsNullOrWhiteSpace(clubId) || string.IsNullOrWhiteSpace(displayName)) return false;
        var manual = await _db.StaffUsers.FirstOrDefaultAsync(x => x.ClubId == clubId && x.IsManual && x.DisplayName == displayName);
        if (manual == null) return false;
        var normalized = birthday.HasValue ? birthday.Value.ToUniversalTime() : (DateTimeOffset?)null;
        manual.Birthday = normalized;
        _db.StaffUsers.Update(manual);
        await _db.SaveChangesAsync();
        return true;
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

    public async Task<bool> SetRecoveryCodeHashAsync(string username, string recoveryCodeHash)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return false;
        baseUser.RecoveryCodeHash = recoveryCodeHash ?? string.Empty;
        _db.BaseUsers.Update(baseUser);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ResetPasswordByRecoveryCodeAsync(string username, string recoveryCodeHash, string newHash)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(recoveryCodeHash)) return false;
        var buList = await _db.BaseUsers.ToListAsync();
        var baseUser = buList.FirstOrDefault(x => string.Equals(_crypto.DecryptString(x.Username), username, StringComparison.Ordinal));
        if (baseUser == null) return false;
        if (!string.Equals(baseUser.RecoveryCodeHash ?? string.Empty, recoveryCodeHash, StringComparison.Ordinal)) return false;
        baseUser.PasswordHash = newHash;
        baseUser.RecoveryCodeHash = string.Empty;
        _db.BaseUsers.Update(baseUser);
        await _db.SaveChangesAsync();
        return true;
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
            dict[j.JobName] = new Rights { AddVip = j.AddVip, RemoveVip = j.RemoveVip, ManageUsers = j.ManageUsers, ManageJobs = j.ManageJobs, ManageVenueSettings = j.ManageVenueSettings, EditVipDuration = j.EditVipDuration, AddDj = j.AddDj, RemoveDj = j.RemoveDj, EditShiftPlan = j.EditShiftPlan, Rank = rankVal, ColorHex = j.ColorHex, IconKey = j.IconKey };
        }
        if (!dict.TryGetValue("Owner", out var own))
        {
            dict["Owner"] = new Rights { AddVip = true, RemoveVip = true, ManageUsers = true, ManageJobs = true, ManageVenueSettings = true, EditVipDuration = true, AddDj = true, RemoveDj = true, EditShiftPlan = true, Rank = 10, ColorHex = dict.TryGetValue("Owner", out var ex) ? (ex.ColorHex ?? "#FFFFFF") : "#FFFFFF", IconKey = dict.TryGetValue("Owner", out var ex2) ? (ex2.IconKey ?? "User") : "User" };
        }
        else
        {
            own.AddVip = true; own.RemoveVip = true; own.ManageUsers = true; own.ManageJobs = true; own.ManageVenueSettings = true; own.EditVipDuration = true; own.AddDj = true; own.RemoveDj = true; own.EditShiftPlan = true; own.Rank = 10;
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
        j.ManageVenueSettings = isOwner ? true : rights.ManageVenueSettings;
        j.EditVipDuration = isOwner ? true : rights.EditVipDuration;
        j.AddDj = isOwner ? true : rights.AddDj;
        j.RemoveDj = isOwner ? true : rights.RemoveDj;
        j.EditShiftPlan = isOwner ? true : rights.EditShiftPlan;
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

    public async Task SetClubCreatorAsync(string clubId, string newUsername)
    {
        var club = await _db.Clubs.FirstOrDefaultAsync(c => c.ClubId == clubId);
        if (club == null)
        {
            club = new ClubEntity { ClubId = clubId, CreatedByUsername = string.Empty, CreatedAt = DateTimeOffset.UtcNow };
            _db.Clubs.Add(club);
        }
        club.CreatedByUsername = _crypto.EncryptDeterministic(newUsername, "club:" + clubId);
        _db.Clubs.Update(club);
        await _db.SaveChangesAsync();
    }

    public async Task<bool> AddStaffMembershipByUidAsync(string clubId, string targetUid, string[] jobs)
    {
        var baseUser = await _db.BaseUsers.FirstOrDefaultAsync(x => x.Uid == targetUid);
        if (baseUser == null) return false;
        var exists = await _db.StaffUsers.AnyAsync(x => x.ClubId == clubId && x.UserUid == baseUser.Uid);
        if (exists) return false;
        _db.StaffUsers.Add(new StaffUserEntity { ClubId = clubId, UserUid = baseUser.Uid, Role = "power", CreatedAt = DateTimeOffset.UtcNow });
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job)) continue;
            if (set.Add(job)) _db.StaffUserJobs.Add(new StaffUserJobEntity { ClubId = clubId, UserUid = baseUser.Uid, JobName = job });
        }
        if (set.Count == 0) _db.StaffUserJobs.Add(new StaffUserJobEntity { ClubId = clubId, UserUid = baseUser.Uid, JobName = "Unassigned" });
        await _db.SaveChangesAsync();
        return true;
    }

    private static string[] GetSortedJobs(Dictionary<string, List<string>> jobsByUser, string userUid)
    {
        if (!jobsByUser.TryGetValue(userUid, out var list) || list.Count == 0) return new[] { "Unassigned" };
        var arr = list.ToArray();
        Array.Sort(arr, StringComparer.Ordinal);
        return arr;
    }
}
