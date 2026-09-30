using Melogold.Core.Domain;
using Xunit;

namespace Melogold.Tests;

/// <summary>Описание альбома из YouTube Music: строка «From Wikipedia (…) under …» отделяется от текста.</summary>
public sealed class DescriptionTextTests
{
    private const string Body = "Abbey Road is the eleventh studio album by the Beatles.\nIt is the last album the group recorded.";

    [Fact]
    public void EnglishFooterFromYouTubeMusic()
    {
        var text = Body + "\n\nFrom Wikipedia (https://en.wikipedia.org/wiki/Abbey_Road) under Creative Commons Attribution CC-BY-SA 3.0 (https://creativecommons.org/licenses/...)";
        var (body, source) = DescriptionText.Split(text);
        Assert.Equal(Body, body);
        Assert.NotNull(source);
        Assert.Equal("https://en.wikipedia.org/wiki/Abbey_Road", source.ArticleUrl);
        Assert.Equal("Creative Commons Attribution CC-BY-SA 3.0", source.License);
        // YouTube обрезал адрес лицензии — он восстановлен по названию
        Assert.Equal("https://creativecommons.org/licenses/by-sa/3.0/", source.LicenseUrl);
    }

    [Fact]
    public void RussianFooterWithFullLicenseLink()
    {
        var text = "Альбом группы «Кино».\n\nИз Википедии (https://ru.wikipedia.org/wiki/%D0%93%D1%80%D1%83%D0%BF%D0%BF%D0%B0_%D0%BA%D1%80%D0%BE%D0%B2%D0%B8) по лицензии Creative Commons Attribution CC-BY-SA 3.0 (https://creativecommons.org/licenses/by-sa/3.0/)";
        var (body, source) = DescriptionText.Split(text);
        Assert.Equal("Альбом группы «Кино».", body);
        Assert.StartsWith("https://ru.wikipedia.org/wiki/", source?.ArticleUrl);
        Assert.Equal("Creative Commons Attribution CC-BY-SA 3.0", source?.License);
        Assert.Equal("https://creativecommons.org/licenses/by-sa/3.0/", source?.LicenseUrl);
    }

    [Fact]
    public void FooterWithoutLicenseAndTruncatedUnknownLicense()
    {
        var (body, source) = DescriptionText.Split(Body + "\n\nFrom Wikipedia (https://en.wikipedia.org/wiki/Abbey_Road)");
        Assert.Equal(Body, body);
        Assert.Equal(new DescriptionSource("https://en.wikipedia.org/wiki/Abbey_Road", null, null), source);

        var other = DescriptionText.Split(Body + "\n\nFrom Wikipedia (https://en.wikipedia.org/wiki/X) under Some License (https://example.org/l...)").Source;
        Assert.Equal("Some License", other?.License);
        Assert.Null(other?.LicenseUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyDescription(string text) => Assert.Equal(("", null), DescriptionText.Split(text));

    [Fact]
    public void TextWithoutFooterStaysWhole()
    {
        Assert.Equal((Body, null), DescriptionText.Split(Body));
        // Вики упомянута в самом тексте, и после неё есть ещё текст — это не сноска
        var inText = "See https://en.wikipedia.org/wiki/Abbey_Road for more.\nAnd another line.";
        Assert.Equal((inText, null), DescriptionText.Split(inText));
    }

    [Fact]
    public void OnlyHttpsWikipediaAndCreativeCommonsOpen()
    {
        Assert.True(DescriptionText.IsWikipedia("https://en.wikipedia.org/wiki/Abbey_Road"));
        Assert.False(DescriptionText.IsWikipedia("http://en.wikipedia.org/wiki/Abbey_Road"));
        Assert.False(DescriptionText.IsWikipedia("https://evilwikipedia.org/wiki/x"));
        Assert.False(DescriptionText.IsWikipedia("https://wikipedia.org.evil.example/wiki/x"));
        Assert.True(DescriptionText.IsLicense("https://creativecommons.org/licenses/by-sa/3.0/"));
        Assert.False(DescriptionText.IsLicense("https://example.org/licenses/by-sa/3.0/"));
        // Адрес не вики в сноске — не источник
        Assert.Null(DescriptionText.Split(Body + "\n\nFrom wikipedia.org (https://evil.example/x)").Source);
    }
}
