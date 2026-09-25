using System.Text.Json;
using Melogold.Core.Domain;
using Xunit;

namespace Melogold.Core.Tests;

/// <summary>Векторы из <c>spec/</c>: те же файлы проверяют сервер и Android.</summary>
public class SpecVectorTests
{
    private static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "spec", name);
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [Fact]
    public void Hwid()
    {
        var vectors = Load("hwid.vectors.json").GetProperty("vectors").EnumerateArray().ToList();
        Assert.NotEmpty(vectors);
        foreach (var v in vectors)
        {
            var expected = v.GetProperty("hwid").GetString();
            Assert.Equal(expected, Domain.Hwid.Compute(v.GetProperty("platformId").GetString()!, v.GetProperty("serverId").GetString()!));
        }
    }

    [Fact]
    public void ProofOfWorkSolutions()
    {
        var root = Load("pow.vectors.json");
        foreach (var s in root.GetProperty("solutions").EnumerateArray())
        {
            var challenge = s.GetProperty("challenge").GetString()!;
            var bits = s.GetProperty("bits").GetInt32();
            var nonce = s.GetProperty("nonce").GetString()!;
            Assert.Equal(nonce, ProofOfWork.Solve(challenge, bits));
            if (Str(s, "sha256") is { } digest) Assert.Equal(digest, ProofOfWork.Digest(challenge, nonce));
        }
    }

    [Fact]
    public void ServerAddresses()
    {
        foreach (var c in Load("server-address.vectors.json").GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var expected = c.GetProperty("expected");
            var actual = ServerAddressPolicy.Normalize(c.GetProperty("input").GetString());
            if (Str(expected, "error") is { } error)
            {
                var invalid = Assert.IsType<ServerAddress.Invalid>(actual, exactMatch: true);
                Assert.True(error == invalid.Code, $"{id}: expected {error}, got {invalid.Code}");
            }
            else
            {
                var valid = Assert.IsType<ServerAddress.Valid>(actual, exactMatch: true);
                Assert.True(Str(expected, "url") == valid.Url, $"{id}: expected {Str(expected, "url")}, got {valid.Url}");
                Assert.Equal(expected.GetProperty("insecure").GetBoolean(), valid.Insecure);
            }
        }
    }

    [Fact]
    public void YouTubeLinks()
    {
        var failures = new List<string>();
        foreach (var c in Load("youtube-links.vectors.json").GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var expected = c.GetProperty("expected");
            var actual = Describe(YouTubeLinkParser.Parse(c.GetProperty("input").GetString()));
            var want = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString());
            foreach (var (key, value) in want)
            {
                if (actual.GetValueOrDefault(key) != value) failures.Add($"{id}: {key} = {actual.GetValueOrDefault(key) ?? "null"}, expected {value ?? "null"}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static Dictionary<string, string?> Describe(LinkTarget target) => target switch
    {
        LinkTarget.Video v => new()
        {
            ["type"] = "Video", ["videoId"] = v.VideoId, ["playlistId"] = v.PlaylistId, ["index"] = v.Index?.ToString(),
            ["startMs"] = v.StartMs?.ToString(),
        },
        LinkTarget.Playlist p => new() { ["type"] = "Playlist", ["playlistId"] = p.PlaylistId },
        LinkTarget.Album a => new() { ["type"] = "Album", ["browseId"] = a.BrowseId },
        LinkTarget.Channel ch => new() { ["type"] = "Channel", ["channelId"] = ch.ChannelId },
        LinkTarget.Handle h => new() { ["type"] = "Handle", ["handle"] = h.Name },
        LinkTarget.LegacyChannel l => new() { ["type"] = "LegacyChannel", ["url"] = l.Url },
        LinkTarget.Search s => new() { ["type"] = "Search", ["query"] = s.Query },
        LinkTarget.External e => new() { ["type"] = "External", ["service"] = e.Service, ["url"] = e.Url },
        LinkTarget.Unsupported u => new() { ["type"] = "Unsupported", ["reason"] = u.Reason },
        _ => throw new InvalidOperationException(),
    };

    [Fact]
    public void TitleCleaning()
    {
        var failures = new List<string>();
        foreach (var c in Load("title-cleaner.vectors.json").GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var input = c.GetProperty("input");
            var expected = c.GetProperty("expected");
            var actual = TitleCleaner.Clean(input.GetProperty("title").GetString()!, Str(input, "channel"), Str(input, "videoType"));
            if (actual.Title != Str(expected, "title")) failures.Add($"{id}: title «{actual.Title}», expected «{Str(expected, "title")}»");
            if (actual.Artist != Str(expected, "artist")) failures.Add($"{id}: artist «{actual.Artist}», expected «{Str(expected, "artist")}»");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
