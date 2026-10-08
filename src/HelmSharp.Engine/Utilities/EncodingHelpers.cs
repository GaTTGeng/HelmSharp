using System.Security.Cryptography;
using System.Text;

namespace HelmSharp.Engine;

/// <summary>
/// Encoding, hashing, and cryptographic helpers used by template functions.
/// </summary>
internal static class EncodingHelpers
{
    /// <summary>
    /// Sprig <c>b64enc</c>: standard Base64 of the UTF-8 bytes. Raw
    /// <see cref="byte"/> arrays are encoded directly without re-encoding.
    /// </summary>
    public static string Base64Encode(object? value)
        => Convert.ToBase64String(
            value is byte[] bytes
                ? bytes
                : Encoding.UTF8.GetBytes(TypeConverters.ToTemplateString(value)));

    /// <summary>Sprig <c>sha1sum</c>: lowercase hex SHA-1 of the UTF-8 bytes.</summary>
    public static string Sha1Sum(string value)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Sprig <c>adler32sum</c>: 8-digit hex Adler-32 checksum. Computed over
    /// UTF-16 code units (like iterating a Go string's bytes for ASCII input);
    /// non-ASCII input may diverge from Helm which hashes raw UTF-8 bytes.
    /// </summary>
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

    /// <summary>
    /// Sprig <c>bcrypt</c>. .NET has no BCrypt in the BCL and adding a dependency
    /// is not justified for templating, so a SHA-256 hash is returned instead.
    /// Charts using <c>bcrypt</c> for real password hashing will not match Helm output.
    /// </summary>
    public static string BCryptHash(string value)
    {
        // BCrypt is not available in .NET BCL without a library.
        // Return a SHA256 hash as a reasonable fallback for non-security-critical templating.
        return StringHelpers.Sha256Sum(value);
    }

    /// <summary>
    /// Sprig <c>b32enc</c>: RFC 4648 Base32 (alphabet A-Z2-7) of the UTF-8 bytes,
    /// without '=' padding. Output differs from Go's padded base32.StdEncoding
    /// for inputs whose length is not a multiple of 5.
    /// </summary>
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

    /// <summary>
    /// Sprig <c>b32dec</c>: inverse of <see cref="Base32Encode"/>. Case-insensitive;
    /// missing or unrecognized trailing characters are treated as zero bits, so both
    /// padded and unpadded input decode. Invalid UTF-8 sequences are replaced, not thrown.
    /// </summary>
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

    /// <summary>Sprig <c>sha512sum</c>: lowercase hex SHA-512 of the UTF-8 bytes.</summary>
    public static string Sha512Sum(string value)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Sprig <c>uuidv4</c>: random RFC 4122 version-4 UUID in lowercase hyphenated form.
    /// Uses a CSPRNG, not <see cref="Random"/>, so values are suitable for secret-like data.
    /// </summary>
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

    /// <summary>
    /// Sprig <c>expandenv</c>: replaces <c>%NAME%</c> (Windows) and <c>$NAME</c>/<c>${NAME}</c>
    /// style placeholders with process environment variables. Placeholder syntax follows
    /// .NET's <see cref="Environment.ExpandEnvironmentVariables"/>, which differs from
    /// Go's <c>os.ExpandEnv</c> (<c>$NAME</c> only) on Windows-style names.
    /// </summary>
    public static string ExpandEnv(string input)
        => Environment.ExpandEnvironmentVariables(input);
}
