using System.Security.Cryptography;
using System.Text;

namespace INRFS.Financer.Infrastructure;

internal static class Security
{
    public static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    public static string Token(int bytes = 48) =>
        Convert
            .ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

    public static string Otp() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string TemporaryPassword()
    {
        const string remaining = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@$%";
        var chars = new[]
        {
            "ABCDEFGHJKLMNPQRSTUVWXYZ"[RandomNumberGenerator.GetInt32(24)],
            "abcdefghijkmnopqrstuvwxyz"[RandomNumberGenerator.GetInt32(24)],
            "23456789"[RandomNumberGenerator.GetInt32(8)],
            "!@$%"[RandomNumberGenerator.GetInt32(4)],
        }.Concat(Enumerable.Range(0, 8).Select(_ => remaining[RandomNumberGenerator.GetInt32(remaining.Length)])).ToArray();
        RandomNumberGenerator.Shuffle<char>(chars);
        return new string(chars);
    }

    public static string Protect(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var plain = Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant());
        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(keyBytes, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public static string Mask(string? encryptedOrPlain, int visible = 4) =>
        string.IsNullOrWhiteSpace(encryptedOrPlain)
            ? ""
            : $"******{encryptedOrPlain[^Math.Min(visible, encryptedOrPlain.Length)..]}";
}
