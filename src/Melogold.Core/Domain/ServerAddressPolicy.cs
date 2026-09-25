using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Melogold.Core.Domain;

/// <summary>Итог проверки адреса сервера, который ввёл человек (API §7.1).</summary>
public abstract record ServerAddress
{
    /// <summary><paramref name="Url"/> — base URL <c>scheme://host[:port][/prefix]</c> без <c>/</c> в конце.</summary>
    public sealed record Valid(string Url, bool Insecure) : ServerAddress;

    /// <summary><paramref name="Code"/>: <c>empty | malformed | unsupported_scheme | credentials_or_params | https_required</c>.</summary>
    public sealed record Invalid(string Code) : ServerAddress;
}

/// <summary>
/// Правила адреса сервера, общие для всех клиентов (API §7.1, векторы <c>spec/server-address.vectors.json</c>):
/// trim; без схемы — <c>https://</c>; схема и хост в нижнем регистре; <c>/</c> в конце убирается, префикс пути
/// остаётся; логин, параметры и фрагмент — отказ. <c>https</c> — на любой хост, <c>http</c> — только на частный.
/// Проверку после DNS делает вызывающий код (<see cref="IsPrivateAddress"/>), здесь имена не резолвятся.
/// </summary>
public static partial class ServerAddressPolicy
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*://")]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"^\d+(\.\d+)*$")]
    private static partial Regex NumericHostRegex();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex LabelRegex();

    private static readonly string[] PrivateSuffixes = [".local", ".lan", ".home.arpa", ".internal"];

    public static ServerAddress Normalize(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return new ServerAddress.Invalid("empty");
        var withScheme = SchemeRegex().IsMatch(text) ? text : "https://" + text;

        var schemeEnd = withScheme.IndexOf("://", StringComparison.Ordinal);
        var scheme = withScheme[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("https" or "http")) return new ServerAddress.Invalid("unsupported_scheme");

        var rest = withScheme[(schemeEnd + 3)..];
        var authorityEnd = rest.IndexOfAny(['/', '?', '#']);
        var authority = authorityEnd < 0 ? rest : rest[..authorityEnd];
        var tail = authorityEnd < 0 ? "" : rest[authorityEnd..];

        if (authority.Contains('@') || tail.Contains('?') || tail.Contains('#'))
            return new ServerAddress.Invalid("credentials_or_params");
        if (authority.Length == 0) return new ServerAddress.Invalid("malformed");

        string host;
        string portText;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return new ServerAddress.Invalid("malformed");
            host = authority[..(close + 1)].ToLowerInvariant();
            var after = authority[(close + 1)..];
            if (after.Length > 0 && !after.StartsWith(':')) return new ServerAddress.Invalid("malformed");
            portText = after.Length > 0 ? after[1..] : "";
            if (!IPAddress.TryParse(host[1..^1], out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
                return new ServerAddress.Invalid("malformed");
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = (colon < 0 ? authority : authority[..colon]).ToLowerInvariant();
            portText = colon < 0 ? "" : authority[(colon + 1)..];
            if (!IsValidHostName(host)) return new ServerAddress.Invalid("malformed");
        }

        var port = "";
        if (portText.Length > 0)
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
                return new ServerAddress.Invalid("malformed");
            port = ":" + number.ToString(CultureInfo.InvariantCulture);
        }

        if (tail.Any(char.IsWhiteSpace)) return new ServerAddress.Invalid("malformed");
        var path = tail.TrimEnd('/');

        var insecure = scheme == "http";
        if (insecure && !IsPrivateHost(host)) return new ServerAddress.Invalid("https_required");
        return new ServerAddress.Valid($"{scheme}://{host}{port}{path}", insecure);
    }

    /// <summary>Имя или IPv4 без скобок: метки <c>[a-z0-9-]</c>, числовой хост — только верный IPv4.</summary>
    private static bool IsValidHostName(string host)
    {
        if (host.Length == 0) return false;
        if (NumericHostRegex().IsMatch(host)) return TryParseIpv4(host, out _);
        var labels = host.Split('.');
        if (labels.Any(label => !LabelRegex().IsMatch(label))) return false;
        // Последняя метка имени начинается с буквы (как у java.net.URI): «1.2.3» — не имя
        return char.IsAsciiLetter(labels[^1][0]);
    }

    private static bool TryParseIpv4(string host, out byte[] octets)
    {
        octets = [];
        var parts = host.Split('.');
        if (parts.Length != 4) return false;
        var result = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            if (parts[i].Length is 0 or > 3 || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > 255)
                return false;
            result[i] = (byte)value;
        }
        octets = result;
        return true;
    }

    /// <summary>
    /// Хост, на который можно по <c>http</c>: IPv4 <c>10/8</c>, <c>172.16/12</c>, <c>192.168/16</c>, <c>169.254/16</c>,
    /// <c>127/8</c>, <c>100.64/10</c>; IPv6 <c>fc00::/7</c>, <c>fe80::/10</c>, <c>::1</c> (в скобках); имя
    /// <c>*.local</c>, <c>*.lan</c>, <c>*.home.arpa</c>, <c>*.internal</c> или из одного слова.
    /// </summary>
    public static bool IsPrivateHost(string host)
    {
        if (host.StartsWith('[') && host.EndsWith(']'))
            return IPAddress.TryParse(host[1..^1], out var v6) && IsPrivateAddress(v6);
        if (NumericHostRegex().IsMatch(host))
            return TryParseIpv4(host, out var octets) && IsPrivateAddress(new IPAddress(octets));
        return !host.Contains('.') || PrivateSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal) && host.Length > suffix.Length);
    }

    /// <summary>Адрес из частной сети — для проверки после DNS: при <c>http</c> все адреса имени должны быть такими.</summary>
    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var (a, b) = (bytes[0], bytes[1]);
            return a == 10 || a == 127 || (a == 172 && b is >= 16 and <= 31) || (a == 192 && b == 168) ||
                   (a == 169 && b == 254) || (a == 100 && b is >= 64 and <= 127);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IPAddress.IsLoopback(address) || (bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }
        return false;
    }
}
