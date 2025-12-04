using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace VenuePlus.Server.Services;

public sealed class EncryptionService
{
    private readonly byte[] _key;

    public EncryptionService(IConfiguration configuration)
    {
        var env = Environment.GetEnvironmentVariable("VENUEPLUS_ENCRYPTION_KEY") ?? string.Empty;
        var conf = configuration["Security:DataEncryptionKey"] ?? string.Empty;
        var secret = string.IsNullOrWhiteSpace(env) ? conf : env;
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Missing encryption key");
        using var sha = SHA256.Create();
        _key = sha.ComputeHash(Encoding.UTF8.GetBytes(secret));
    }

    public string EncryptDeterministic(string plaintext, string? context = null)
    {
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var nonce = DeriveNonce(pt, context);
        var cipher = new byte[pt.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key))
        {
            aes.Encrypt(nonce, pt, cipher, tag);
        }
        var combined = new byte[nonce.Length + cipher.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(cipher, 0, combined, nonce.Length, cipher.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length + cipher.Length, tag.Length);
        return "enc:" + Convert.ToBase64String(combined);
    }

    public string DecryptString(string value)
    {
        if (!IsEncrypted(value)) return value;
        var b64 = value.AsSpan(4);
        var data = Convert.FromBase64String(b64.ToString());
        var nonce = new byte[12];
        var tag = new byte[16];
        var cipher = new byte[data.Length - nonce.Length - tag.Length];
        Buffer.BlockCopy(data, 0, nonce, 0, nonce.Length);
        Buffer.BlockCopy(data, nonce.Length, cipher, 0, cipher.Length);
        Buffer.BlockCopy(data, nonce.Length + cipher.Length, tag, 0, tag.Length);
        var pt = new byte[cipher.Length];
        using (var aes = new AesGcm(_key))
        {
            aes.Decrypt(nonce, cipher, tag, pt);
        }
        return Encoding.UTF8.GetString(pt);
    }

    public bool IsEncrypted(string value)
    {
        return value != null && value.StartsWith("enc:", StringComparison.Ordinal);
    }

    private byte[] DeriveNonce(byte[] plaintext, string? context)
    {
        using var hmac = new HMACSHA256(_key);
        if (!string.IsNullOrWhiteSpace(context))
        {
            var ctx = Encoding.UTF8.GetBytes(context);
            hmac.TransformBlock(ctx, 0, ctx.Length, null, 0);
            hmac.TransformFinalBlock(plaintext, 0, plaintext.Length);
            var full = hmac.Hash!;
            var nonce = new byte[12];
            Buffer.BlockCopy(full, 0, nonce, 0, nonce.Length);
            return nonce;
        }
        var full2 = hmac.ComputeHash(plaintext);
        var nonce2 = new byte[12];
        Buffer.BlockCopy(full2, 0, nonce2, 0, nonce2.Length);
        return nonce2;
    }
}

