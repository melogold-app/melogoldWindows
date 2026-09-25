using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>config/stream-clients.json: разбирается, и встроенный запасной список с ним совпадает.</summary>
public class StreamClientsTests
{
    [Fact]
    public void RepositoryFileMatchesBuiltIn()
    {
        var parsed = StreamClients.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "stream-clients.json")));
        Assert.NotNull(parsed);
        Assert.Equal(StreamClients.BuiltIn, parsed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema\":2,\"clients\":[]}")]
    [InlineData("{\"schema\":1,\"clients\":[]}")]
    [InlineData("{\"schema\":1,\"clients\":[{\"name\":\"X\",\"id\":1}]}")]
    [InlineData("not json")]
    public void BrokenFilesAreRejected(string json) => Assert.Null(StreamClients.Parse(json));
}
