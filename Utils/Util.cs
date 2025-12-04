using System;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace VenuePlus.Server;

public static class Util
{
    public static string Sha256(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    public static string HashPassword(string username, string password)
    {
        var envSalt = Environment.GetEnvironmentVariable("VENUEPLUS_ARGON2_SALT_BYTES") ?? Environment.GetEnvironmentVariable("VENUEPLUS_ARGON2_SALT_BYTES");
        var saltLen = 16;
        if (int.TryParse(envSalt, out var s) && s >= 16 && s <= 64) saltLen = s;
        var salt = RandomNumberGenerator.GetBytes(saltLen);
        var envIter = Environment.GetEnvironmentVariable("VENUEPLUS_ARGON2_ITER") ?? Environment.GetEnvironmentVariable("VENUEPLUS_ARGON2_ITER");
        var iterations = 3;
        if (int.TryParse(envIter, out var it) && it >= 1 && it <= 10) iterations = it;
        var envMem = Environment.GetEnvironmentVariable("VENUEPLUS_ARGON2_MEMORY_KIB") ?? Environment.GetEnvironmentVariable("VENUEPLUS_ARGON2_MEMORY_KIB");
        var memKiB = 65536;
        if (int.TryParse(envMem, out var m) && m >= 8192 && m <= 1048576) memKiB = m;
        var envPar = Environment.GetEnvironmentVariable("VENUEPLUS_ARGON2_PARALLELISM") ?? Environment.GetEnvironmentVariable("VENUEPLUS_ARGON2_PARALLELISM");
        var parallel = Math.Clamp(Environment.ProcessorCount >= 2 ? 2 : 1, 1, 8);
        if (int.TryParse(envPar, out var p) && p >= 1 && p <= 8) parallel = p;
        var pepper = Environment.GetEnvironmentVariable("VENUEPLUS_PASSWORD_PEPPER") ?? Environment.GetEnvironmentVariable("VENUEPLUS_PASSWORD_PEPPER") ?? string.Empty;
        var input = (password ?? string.Empty) + pepper;
        var argon = new Argon2id(Encoding.UTF8.GetBytes(input)) { Salt = salt, Iterations = iterations, MemorySize = memKiB, DegreeOfParallelism = parallel };
        var hash = argon.GetBytes(32);
        return $"ARGON2ID${iterations}${memKiB}${parallel}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string username, string password, string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        if (!stored.StartsWith("ARGON2ID$", StringComparison.Ordinal)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 6) return false;
        if (!int.TryParse(parts[1], out var iter)) return false;
        if (!int.TryParse(parts[2], out var memKiB)) return false;
        if (!int.TryParse(parts[3], out var par)) return false;
        var salt = Convert.FromBase64String(parts[4]);
        var expected = Convert.FromBase64String(parts[5]);
        var pepper = Environment.GetEnvironmentVariable("VENUEPLUS_PASSWORD_PEPPER") ?? Environment.GetEnvironmentVariable("VENUEPLUS_PASSWORD_PEPPER") ?? string.Empty;
        var input = (password ?? string.Empty) + pepper;
        var argon = new Argon2id(Encoding.UTF8.GetBytes(input)) { Salt = salt, Iterations = iter, MemorySize = memKiB, DegreeOfParallelism = par };
        var actual = argon.GetBytes(expected.Length);
        if (CryptographicOperations.FixedTimeEquals(actual, expected)) return true;
        if (!string.IsNullOrEmpty(pepper))
        {
            var argonNoPepper = new Argon2id(Encoding.UTF8.GetBytes(password ?? string.Empty)) { Salt = salt, Iterations = iter, MemorySize = memKiB, DegreeOfParallelism = par };
            var actualNoPepper = argonNoPepper.GetBytes(expected.Length);
            if (CryptographicOperations.FixedTimeEquals(actualNoPepper, expected)) return true;
        }
        return false;
    }

    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }

    public static string NewUid(int length = 15)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }

    public static string GetClientIp(HttpContext ctx)
    {
        var fwd = ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var xff) && xff.Count > 0 ? xff[0] : null;
        if (!string.IsNullOrWhiteSpace(fwd)) return fwd.Split(',')[0].Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static bool ValidateSession(string token, out string username)
    {
        username = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!Store.StaffSessions.TryGetValue(token, out var u)) return false;
        if (!Store.StaffSessionExpiry.TryGetValue(token, out var exp) || exp <= DateTimeOffset.UtcNow) return false;
        username = u;
        Store.StaffSessionExpiry[token] = DateTimeOffset.UtcNow.AddHours(8);
        return true;
    }
}
