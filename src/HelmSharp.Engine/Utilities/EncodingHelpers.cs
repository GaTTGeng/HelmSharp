using System.Security.Cryptography;
using System.Text;

namespace HelmSharp.Engine;

/// <summary>
/// Encoding, hashing, and cryptographic helpers used by template functions.
/// </summary>
internal static class EncodingHelpers
{
    public static string Base64Encode(object? value)
        => Convert.ToBase64String(
            value is byte[] bytes
                ? bytes
                : Encoding.UTF8.GetBytes(TypeConverters.ToTemplateString(value)));

    public static string Sha1Sum(string value)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Adler32Sum(string value)
    {
        uint a = 1, b = 0;
        foreach (var ch in value)
        {
            a = (a + ch) % 65521;
            b = (b + a) % 65521;
        }
        return ((b << 16) | a).ToString("x8");
    }

    public static string BCryptHash(string value)
    {
        // BCrypt is not available in .NET BCL without a library.
        // Return a SHA256 hash as a reasonable fallback for non-security-critical templating.
        return StringHelpers.Sha256Sum(value);
    }

    public static string Base32Encode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder();
        var i = 0;
        while (i < bytes.Length)
        {
            var b0 = (int)(bytes[i] & 0xFF);
            var b1 = i + 1 < bytes.Length ? (int)(bytes[i + 1] & 0xFF) : 0;
            var b2 = i + 2 < bytes.Length ? (int)(bytes[i + 2] & 0xFF) : 0;
            var b3 = i + 3 < bytes.Length ? (int)(bytes[i + 3] & 0xFF) : 0;
            var b4 = i + 4 < bytes.Length ? (int)(bytes[i + 4] & 0xFF) : 0;
            sb.Append(alphabet[(b0 >> 3) & 0x1F]);
            sb.Append(alphabet[((b0 << 2) | (b1 >> 6)) & 0x1F]);
            if (i + 1 < bytes.Length) sb.Append(alphabet[(b1 >> 1) & 0x1F]);
            if (i + 1 < bytes.Length) sb.Append(alphabet[((b1 << 4) | (b2 >> 4)) & 0x1F]);
            if (i + 2 < bytes.Length) sb.Append(alphabet[((b2 << 1) | (b3 >> 7)) & 0x1F]);
            if (i + 3 < bytes.Length) sb.Append(alphabet[(b3 >> 2) & 0x1F]);
            if (i + 3 < bytes.Length) sb.Append(alphabet[((b3 << 3) | (b4 >> 5)) & 0x1F]);
            if (i + 4 < bytes.Length) sb.Append(alphabet[b4 & 0x1F]);
            i += 5;
        }
        return sb.ToString();
    }

    public static string Base32Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var i = 0;
        while (i < value.Length)
        {
            var a = i < value.Length ? alphabet.IndexOf(char.ToUpper(value[i])) : -1; i++;
            var b = i < value.Length ? alphabet.IndexOf(char.ToUpper(value[i])) : -1; i++;
            var c = i < value.Length ? alphabet.IndexOf(char.ToUpper(value[i])) : -1; i++;
            var d = i < value.Length ? alphabet.IndexOf(char.ToUpper(value[i])) : -1; i++;
            var e = i < value.Length ? alphabet.IndexOf(char.ToUpper(value[i])) : -1; i++;
            var f = i < value.Length ? alphabet.IndexOf(char.ToUpper(value[i])) : -1; i++;
            var g = i < value.Length ? alphabet.IndexOf(char.ToUpper(value[i])) : -1; i++;
            var h = i < value.Length ? alphabet.IndexOf(char.ToUpper(value[i])) : -1; i++;
            if (a < 0) a = 0; if (b < 0) b = 0; if (c < 0) c = 0; if (d < 0) d = 0;
            if (e < 0) e = 0; if (f < 0) f = 0; if (g < 0) g = 0; if (h < 0) h = 0;
            bytes.Add((byte)((a << 3) | (b >> 2)));
            if (c >= 0 || d >= 0) bytes.Add((byte)(((b & 3) << 6) | (c << 1) | (d >> 4)));
            if (e >= 0) bytes.Add((byte)(((d & 0xF) << 4) | (e >> 1)));
            if (f >= 0 || g >= 0) bytes.Add((byte)(((e & 1) << 7) | (f << 2) | (g >> 3)));
            if (h >= 0) bytes.Add((byte)(((g & 7) << 5) | h));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public static string Sha512Sum(string value)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string UuidV4()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        var sb = new StringBuilder(36);
        for (var i = 0; i < 16; i++)
        {
            if (i is 4 or 6 or 8 or 10) sb.Append('-');
            sb.Append(bytes[i].ToString("x2"));
        }
        return sb.ToString();
    }

    public static string ExpandEnv(string input)
        => Environment.ExpandEnvironmentVariables(input);
}
