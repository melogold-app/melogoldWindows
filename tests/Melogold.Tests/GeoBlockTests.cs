using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Melogold.Core.Domain;
using Melogold.InnerTube;
using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>Задание 0010: трек закрыт в стране — страна из visitorData, классификация отказа и текст ошибки.</summary>
public class GeoBlockTests
{
    private const string Netherlands = "CgtRTWpHWl9XellHZyjBhN7VBjIoCgJOTBIiEh4SHAsMDg8QERITFBUWFxgZGhscHR4fICEiIyQlJicgYw%3D%3D";

    // Полная строка — из VisitorDataTest.kt Android
    private const string Germany =
        "Cgs4bmZBZU9NZ2hGVSiLhd7VBjIOCgJERRIIEgAgRlICCHE6AggBYuACCt0CMTguWVRFPUdHUjNxZUdSUDVfUTFNMXVSVVVVYVRs" +
        "Q1BWb3VINWZaVmpWMlk5TkFJYWtwenZQdGlTS2NMMmRiV1pFbDFjNllRb0Nwd3pMRXhlVk5FYlVfSnhCb21TZnBZRE5pbEZjTzJP" +
        "NVdxR211MnlwSTJSWEt4TVllX1BiTzJ0YmlGM0gxWEpJckV4REU1TnliV1RRY0NWQ19tamZfMkIzVWw4Szg5cnRRYm1KbXc0aEtH" +
        "YmY0ZVA4c3pXWXhBM3hVaXNhOWd0eFlxYjNvUU1kMVZjQ01PWFFJTElTRG1ZdnpNU2IyOFpPWHNpRGY3RHZNeEFVX2kxYUZVOHk3" +
        "WTV4LVd0akg2c1RXRkZ5cGR1emxiX0Q1Ykg5b0VHN1pfVWFiNUlzNW9iUWhmOUQ2X2IwNGJIaTRTVWRKc1laSTZBM0FEcGdDR256" +
        "NnNkd1E2ZVRWYzNqQ0I0eFc5Zw%3D%3D";

    [Fact]
    public void CountryComesFromVisitorData()
    {
        Assert.Equal("NL", Playability.VisitorCountry(Netherlands));
        Assert.Equal("DE", Playability.VisitorCountry(Germany));
        Assert.Null(Playability.VisitorCountry(null));
        Assert.Null(Playability.VisitorCountry(""));
        Assert.Null(Playability.VisitorCountry("not base64 at all!"));
        // Только id посетителя (поле 1), поля 6 нет
        Assert.Null(Playability.VisitorCountry("CgtRTWpHWl9XellHZw"));
    }

    /// <summary>Ответ WEB: страна (через visitorData) и список открытых стран — у Saba «Photosynthesis» 122 страны без России.</summary>
    private static Playability Answer(string visitorData, int open, bool withRussia, string status = "UNPLAYABLE", string? reason = "Video unavailable")
    {
        var countries = new JsonArray();
        foreach (var code in Enumerable.Range(0, open).Select(i => $"{(char)('A' + i / 26 % 26)}{(char)('A' + i % 26)}").Where(c => c != "RU").Take(withRussia ? open - 1 : open))
            countries.Add(code);
        if (withRussia) countries.Add("RU");
        var json = new JsonObject
        {
            ["playabilityStatus"] = new JsonObject { ["status"] = status, ["reason"] = reason },
            ["responseContext"] = new JsonObject { ["visitorData"] = visitorData },
            ["microformat"] = new JsonObject { ["playerMicroformatRenderer"] = new JsonObject { ["availableCountries"] = countries } },
        };
        return Playability.From(json);
    }

    // visitorData с кодом RU: поле 6 { поле 1 = "RU" } в base64
    private static string VisitorIn(string country)
    {
        var inner = new byte[] { 0x0A, 0x02, (byte)country[0], (byte)country[1] };
        var bytes = new byte[] { 0x0A, 0x02, (byte)'i', (byte)'d', 0x32, (byte)inner.Length }.Concat(inner).ToArray();
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').Replace("=", "%3D", StringComparison.Ordinal);
    }

    [Fact]
    public void ClosedInTheCountryCarriesCountryAndCount()
    {
        var failure = new StreamException(StreamErrorKind.Unavailable, "VISIONOS: UNPLAYABLE Video unavailable");
        var answer = Answer(VisitorIn("RU"), 122, withRussia: false);
        Assert.Equal("RU", answer.Country);
        Assert.True(answer.IsBlockedHere);
        var result = StreamResolver.Diagnose(answer, failure);
        Assert.NotNull(result);
        Assert.Equal(StreamErrorKind.Geo, result.Kind);
        Assert.Equal("RU", result.Country);
        Assert.Equal(122, result.OpenCountries);
    }

    [Fact]
    public void OpenHereKeepsTheOldError()
    {
        var failure = new StreamException(StreamErrorKind.Extractor, "VISIONOS: no audio with a plain URL");
        Assert.Null(StreamResolver.Diagnose(Answer(VisitorIn("RU"), 122, withRussia: true, status: "OK", reason: null), failure));
        Assert.Null(StreamResolver.Diagnose(null, failure));
    }

    [Theory]
    [InlineData("The uploader has not made this video available in your country", StreamErrorKind.Geo)]
    [InlineData("This video is not available in your country", StreamErrorKind.Geo)]
    [InlineData("Sign in to confirm your age", StreamErrorKind.Age)]
    [InlineData("This video may be inappropriate for some users.", StreamErrorKind.Age)]
    [InlineData("Private video", StreamErrorKind.Unavailable)]
    [InlineData("This video has been removed by the uploader", StreamErrorKind.Unavailable)]
    [InlineData("This video is no longer available because the YouTube account associated with this video has been terminated.", StreamErrorKind.Unavailable)]
    public void PhrasesOfYouTubeDecide(string reason, StreamErrorKind kind)
    {
        var failure = new StreamException(StreamErrorKind.Extractor, "VISIONOS: UNPLAYABLE");
        // Страна неизвестна, список пуст: решают слова YouTube — в ответе WEB или в сообщении клиента потока
        var fromReason = StreamResolver.Diagnose(new Playability("UNPLAYABLE", reason, null, []), failure);
        Assert.Equal(kind, fromReason?.Kind);
        var fromClient = StreamResolver.Diagnose(null, new StreamException(StreamErrorKind.Extractor, "IOS: UNPLAYABLE " + reason));
        Assert.Equal(kind, fromClient?.Kind);
        if (kind == StreamErrorKind.Geo) Assert.Null(fromReason!.OpenCountries);
    }

    [Fact]
    public void GeoPhraseWithKnownCountryKeepsTheCountry()
    {
        var failure = new StreamException(StreamErrorKind.Unavailable, "UNPLAYABLE");
        var result = StreamResolver.Diagnose(new Playability("UNPLAYABLE", "The uploader has not made this video available in your country", "RU", []), failure);
        Assert.Equal(StreamErrorKind.Geo, result?.Kind);
        Assert.Equal("RU", result?.Country);
        Assert.Null(result?.OpenCountries);
    }

    [Theory]
    [InlineData(1, "one", "one")]
    [InlineData(2, "few", "other")]
    [InlineData(5, "many", "other")]
    [InlineData(11, "many", "other")]
    [InlineData(21, "one", "other")]
    [InlineData(121, "one", "other")]
    [InlineData(122, "few", "other")]
    [InlineData(125, "many", "other")]
    public void PluralForms(long count, string russian, string english)
    {
        Assert.Equal(russian, Plurals.Form(count, russian: true));
        Assert.Equal(english, Plurals.Form(count, russian: false));
    }

    private static Dictionary<string, string> Strings(string lang) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", lang, "Resources.resw")).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    /// <summary>Текст карточки, как его собирает PlayerViewModel.ErrorText.</summary>
    private static string Text(Dictionary<string, string> strings, bool russian, string country, int? open) => open is { } n
        ? string.Format(CultureInfo.InvariantCulture, strings["PlayErrorGeoCountryOpenFormat"], country,
            string.Format(CultureInfo.InvariantCulture, strings[$"GeoOtherCountries_{Plurals.Form(n, russian)}"], n))
        : string.Format(CultureInfo.InvariantCulture, strings["PlayErrorGeoCountryFormat"], country);

    [Fact]
    public void TextsInBothLanguages()
    {
        var ru = Strings("ru-RU");
        Assert.Equal("Недоступно в стране «Россия»: YouTube считает, что вы там, а правообладатель открыл трек в 122 других странах. С VPN выберите сервер другой страны: некоторые серверы YouTube тоже относит к стране «Россия».",
            Text(ru, true, "Россия", 122));
        Assert.Contains("в 121 другой стране.", Text(ru, true, "Россия", 121), StringComparison.Ordinal);
        Assert.Equal("Недоступно в стране «Россия»: YouTube считает, что вы там, а правообладатель закрыл трек для этой страны. С VPN выберите сервер другой страны: некоторые серверы YouTube тоже относит к стране «Россия».",
            Text(ru, true, "Россия", null));
        Assert.Equal("Недоступно в вашей стране", ru["PlayErrorGeo"]);

        var en = Strings("en-US");
        Assert.Equal("Unavailable in Russia: YouTube places you there, and the rights holder opened this track in 122 other countries. With a VPN, pick a server in another country: YouTube counts some VPN servers as Russia too.",
            Text(en, false, "Russia", 122));
        Assert.Contains("in 1 other country.", Text(en, false, "Russia", 1), StringComparison.Ordinal);
        Assert.Equal("Unavailable in Russia: YouTube places you there, and the rights holder closed this track for it. With a VPN, pick a server in another country: YouTube counts some VPN servers as Russia too.",
            Text(en, false, "Russia", null));
        Assert.Equal("Unavailable in your country", en["PlayErrorGeo"]);
    }

    [Fact]
    public void CountryNamesFollowTheInterfaceLanguage()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            Assert.Equal("Russia", CountryNames.Of("RU"));
            CultureInfo.CurrentUICulture = new CultureInfo("ru-RU");
            Assert.Equal("Россия", CountryNames.Of("RU"));
            Assert.Equal("Германия", CountryNames.Of("DE"));
            Assert.Equal("R1", CountryNames.Of("R1"));
            Assert.Equal("QM", CountryNames.Of("QM"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
