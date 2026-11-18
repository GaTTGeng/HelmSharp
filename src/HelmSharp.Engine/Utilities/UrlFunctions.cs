using System.Net;
using System.Text;
using System.Text.RegularExpressions;

// Feature folders share the stable HelmSharp.Engine namespace.
// ReSharper disable once CheckNamespace
namespace HelmSharp.Engine;

/// <summary>Sprig URL dictionary conversion using Go's URL escaping rules.</summary>
internal static class UrlFunctions
{
    private enum EscapeMode { Path, Fragment, User, Host }

    public static object Parse(object? value)
    {
        if (value is not string input)
            throw new InvalidOperationException("urlParse requires a string");

        try
        {
            return ParseUrl(input);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"unable to parse url: {ex.Message}", ex);
        }
    }

    private static Dictionary<string, object?> ParseUrl(string input)
    {
        if (input.Any(c => c < 32 || c == 127))
            throw new FormatException("net/url: invalid control character in URL");

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["scheme"] = "", ["host"] = "", ["hostname"] = "", ["path"] = "",
            ["query"] = "", ["opaque"] = "", ["fragment"] = "", ["userinfo"] = ""
        };
        var rest = input;
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            result["fragment"] = Unescape(rest[(hash + 1)..]);
            rest = rest[..hash];
        }

        var scheme = Regex.Match(rest, "^([A-Za-z][A-Za-z0-9+.-]*):");
        if (scheme.Success)
        {
            result["scheme"] = scheme.Groups[1].Value.ToLowerInvariant();
            rest = rest[scheme.Length..];
        }
        var question = rest.IndexOf('?');
        if (question >= 0)
        {
            result["query"] = rest[(question + 1)..];
            rest = rest[..question];
        }

        if (!rest.StartsWith('/') && scheme.Success)
        {
            result["opaque"] = rest;
            return result;
        }
        if (!scheme.Success && !rest.StartsWith('/') && rest.Split('/')[0].Contains(':'))
            throw new FormatException("first path segment in URL cannot contain colon");

        if (rest.StartsWith("//", StringComparison.Ordinal) &&
            (scheme.Success || !rest.StartsWith("///", StringComparison.Ordinal)))
        {
            var slash = rest.IndexOf('/', 2);
            var authority = slash < 0 ? rest[2..] : rest[2..slash];
            rest = slash < 0 ? "" : rest[slash..];
            var at = authority.LastIndexOf('@');
            var host = at < 0 ? authority : authority[(at + 1)..];
            if (at >= 0)
                result["userinfo"] = NormalizeUserinfo(authority[..at]);
            host = ParseHost(host);
            result["host"] = host;
            var colon = host.LastIndexOf(':');
            var hostname = colon >= 0 && IsPort(host[colon..]) ? host[..colon] : host;
            if (hostname.StartsWith('[') && hostname.EndsWith(']')) hostname = hostname[1..^1];
            result["hostname"] = hostname;
        }
        result["path"] = Unescape(rest);
        return result;
    }

    public static object Join(object? value)
    {
        if (value is not IDictionary<string, object?> dict)
            throw new InvalidOperationException("urlJoin requires a dictionary");
        string Get(string key)
        {
            if (!dict.TryGetValue(key, out var item)) return "";
            return item as string ?? throw new InvalidOperationException($"unable to parse {key} key, must be of type string");
        }

        var scheme = Get("scheme");
        var host = Get("host");
        var path = Get("path");
        var query = Get("query");
        var opaque = Get("opaque");
        var fragment = Get("fragment");
        var userinfo = Get("userinfo");
        var normalizedUserinfo = "";
        if (userinfo.Length > 0)
        {
            try
            {
                // Sprig parses userinfo through a temporary URL; delimiters may
                // terminate its authority before the appended @host.
                normalizedUserinfo = (string)ParseUrl($"proto://{userinfo}@host")["userinfo"]!;
            }
            catch (FormatException ex) { throw new InvalidOperationException($"unable to parse userinfo in dict: {ex.Message}", ex); }
        }
        var result = new StringBuilder();
        if (scheme.Length > 0) result.Append(scheme).Append(':');
        if (opaque.Length > 0) result.Append(opaque);
        else
        {
            if ((scheme.Length > 0 || host.Length > 0 || normalizedUserinfo.Length > 0) &&
                (host.Length > 0 || path.Length > 0 || normalizedUserinfo.Length > 0))
            {
                result.Append("//");
                if (normalizedUserinfo.Length > 0) result.Append(normalizedUserinfo).Append('@');
                result.Append(Escape(host, EscapeMode.Host));
            }
            var escapedPath = Escape(path, EscapeMode.Path);
            if (host.Length > 0 && escapedPath.Length > 0 && escapedPath[0] != '/') result.Append('/');
            if (result.Length == 0 && escapedPath.Split('/')[0].Contains(':')) result.Append("./");
            result.Append(escapedPath);
        }
        if (query.Length > 0) result.Append('?').Append(query);
        if (fragment.Length > 0) result.Append('#').Append(Escape(fragment, EscapeMode.Fragment));
        return result.ToString();
    }

    private static string ParseHost(string host)
    {
        var open = host.LastIndexOf('[');
        if (open >= 0)
        {
            var close = host.LastIndexOf(']');
            if (close < open) throw new FormatException("missing ']' in host");
            if (!IsPort(host[(close + 1)..])) throw new FormatException("invalid port after host");
            var address = Unescape(host[(open + 1)..close]);
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                throw new FormatException("invalid IP-literal");
            return host[..open] + "[" + address + "]" + host[(close + 1)..];
        }
        var colon = host.LastIndexOf(':');
        if (colon >= 0 && !IsPort(host[colon..])) throw new FormatException("invalid port after host");
        for (var i = 0; i < host.Length; i++)
        {
            var c = host[i];
            if (c == '%')
            {
                if (i + 2 >= host.Length || !Uri.IsHexDigit(host[i + 1]) || !Uri.IsHexDigit(host[i + 2]))
                    throw new FormatException("invalid URL escape");
                var b = Convert.ToByte(host.Substring(i + 1, 2), 16);
                if (b < 128 && b != 37) throw new FormatException("invalid URL escape");
                i += 2;
            }
            else if (c < 128 && !Allowed((byte)c, EscapeMode.Host))
                throw new FormatException("invalid character in host name");
        }
        return Unescape(host);
    }

    private static bool IsPort(string port) => port.Length == 0 || port[0] == ':' && port.Skip(1).All(char.IsAsciiDigit);

    private static string NormalizeUserinfo(string input)
    {
        if (input.Any(c => !char.IsAsciiLetterOrDigit(c) && !"-._:~!$&'()*+,;=%@".Contains(c)))
            throw new FormatException("net/url: invalid userinfo");
        var colon = input.IndexOf(':');
        if (colon < 0) return Escape(Unescape(input), EscapeMode.User);
        return Escape(Unescape(input[..colon]), EscapeMode.User) + ":" + Escape(Unescape(input[(colon + 1)..]), EscapeMode.User);
    }

    private static string Unescape(string input)
    {
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] != '%') continue;
            if (i + 2 >= input.Length || !Uri.IsHexDigit(input[i + 1]) || !Uri.IsHexDigit(input[i + 2]))
                throw new FormatException("invalid URL escape");
            i += 2;
        }
        return Uri.UnescapeDataString(input);
    }

    private static string Escape(string input, EscapeMode mode)
    {
        var result = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(input))
        {
            if (Allowed(b, mode)) result.Append((char)b);
            else result.Append('%').Append(b.ToString("X2"));
        }
        return result.ToString();
    }

    private static bool Allowed(byte b, EscapeMode mode)
    {
        if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9') return true;
        var c = (char)b;
        if ("-_.~".Contains(c)) return true;
        if (mode == EscapeMode.Host && "!$&'()*+,;=:[]<>\"".Contains(c)) return true;
        if (mode == EscapeMode.Path && "$&+,/:;=@".Contains(c)) return true;
        if (mode == EscapeMode.User && "$&+,;=".Contains(c)) return true;
        return mode == EscapeMode.Fragment && "$&+,/:;=?@!()*".Contains(c);
    }
}
