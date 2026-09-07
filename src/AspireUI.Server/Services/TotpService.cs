using System.Security.Cryptography;
using System.Text;

namespace AspireUI.Server.Services;

/// <summary>
/// Time-based one-time passwords, RFC 6238 with the defaults every authenticator app assumes:
/// HMAC-SHA1, six digits, a thirty-second step. Written out rather than taken from a package —
/// it is thirty lines, and an authentication factor is not a good place for a surprise dependency.
/// </summary>
public static class TotpService
{
    public const int Digits = 6;
    public const int StepSeconds = 30;

    /// <summary>How many steps either side of now are accepted — one, for a clock that drifts.</summary>
    public const int Window = 1;

    private const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A fresh shared secret, base32 as the apps expect it (160 bits, the RFC's recommendation).</summary>
    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return ToBase32(bytes);
    }

    /// <summary>What goes into the QR code.</summary>
    public static string EnrolmentUri(string secret, string username, string issuer = "AspireUI") =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(username)}" +
        $"?secret={secret}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    /// <summary>The code for one step. Exposed so a test does not need to wait thirty seconds.</summary>
    public static string Code(string secret, long step)
    {
        var key = FromBase32(secret);
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % (int)Math.Pow(10, Digits)).ToString(new string('0', Digits));
    }

    public static long StepFor(DateTimeOffset when) => when.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>
    /// True when the code belongs to this secret, now. Compared in constant time and across a
    /// one-step window either side, because phones and servers disagree about the second.
    /// </summary>
    public static bool Verify(string? secret, string? code, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code)) return false;
        var digits = new string(code.Where(char.IsDigit).ToArray());
        if (digits.Length != Digits) return false;

        var step = StepFor(now ?? DateTimeOffset.UtcNow);
        for (var offset = -Window; offset <= Window; offset++)
        {
            var expected = Code(secret, step + offset);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(digits)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Recovery codes: the way back in when the phone is gone. Returned once in plain text and kept
    /// only as hashes, like a password — a stolen database must not be a stolen second factor.
    /// </summary>
    public static (List<string> Plain, List<string> Hashed) NewRecoveryCodes(int count = 8)
    {
        var plain = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var raw = ToBase32(RandomNumberGenerator.GetBytes(10))[..10].ToLowerInvariant();
            plain.Add(raw[..5] + "-" + raw[5..]);
        }
        return (plain, plain.Select(HashCode).ToList());
    }

    public static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));

    /// <summary>Finds the code in the list and returns the list without it: a recovery code is single-use.</summary>
    public static bool UseRecoveryCode(List<string> hashed, string? code, out List<string> left)
    {
        left = hashed;
        if (string.IsNullOrWhiteSpace(code)) return false;
        var hash = HashCode(code);
        if (!hashed.Contains(hash)) return false;
        left = hashed.Where(h => h != hash).ToList();
        return true;
    }

    private static string Normalize(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < data.Length; i += 5)
        {
            var chunk = new byte[5];
            var len = Math.Min(5, data.Length - i);
            Array.Copy(data, i, chunk, 0, len);
            var bits = (ulong)chunk[0] << 32 | (ulong)chunk[1] << 24 | (ulong)chunk[2] << 16 | (ulong)chunk[3] << 8 | chunk[4];
            var chars = len * 8 / 5 + (len * 8 % 5 == 0 ? 0 : 1);
            for (var c = 0; c < chars; c++)
                sb.Append(Base32[(int)(bits >> (35 - c * 5) & 0x1F)]);
        }
        return sb.ToString();
    }

    private static byte[] FromBase32(string secret)
    {
        var clean = secret.TrimEnd('=').ToUpperInvariant().Where(Base32.Contains).ToArray();
        var bits = 0;
        var value = 0;
        var outp = new List<byte>();
        foreach (var c in clean)
        {
            value = value << 5 | Base32.IndexOf(c);
            bits += 5;
            if (bits < 8) continue;
            outp.Add((byte)(value >> (bits - 8) & 0xFF));
            bits -= 8;
        }
        return outp.ToArray();
    }
}
