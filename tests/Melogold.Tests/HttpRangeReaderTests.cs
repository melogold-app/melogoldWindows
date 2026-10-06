using System.Net;
using Melogold.Playback;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Обрыв посреди песни (пользователь 2026-10-06, «googlevideo 403 after fresh URLs»): адрес googlevideo привязан к IP,
/// после смены сети или сервера VPN первые свежие адреса ещё получают 403. Чтение не сдаётся, пока не перепробует четыре.
/// </summary>
public sealed class HttpRangeReaderTests
{
    private sealed class FakeGoogleVideo(Func<int, HttpStatusCode> status) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var code = status(++Requests);
            var response = new HttpResponseMessage(code);
            if (code == HttpStatusCode.PartialContent)
            {
                var range = request.Headers.Range!.Ranges.First();
                var length = (int)(range.To!.Value - range.From!.Value + 1);
                response.Content = new ByteArrayContent(new byte[length]);
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(range.From.Value, range.To.Value, 10_000_000);
            }
            return Task.FromResult(response);
        }
    }

    private static StreamInfo Info(int n) => new() { VideoId = "aaaaaaaaaaa", Url = $"https://rr1.googlevideo.com/videoplayback?n={n}", Source = "VISIONOS", ExpiresAtMs = long.MaxValue, ContentLength = 10_000_000 };

    private static readonly TimeSpan[] NoPauses = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    [Fact]
    public async Task ThreeForbiddenInARowThenTheFourthFreshUrlPlays()
    {
        // Первый адрес и ещё два свежих — 403 (переключение VPN), третий свежий уже работает
        var googleVideo = new FakeGoogleVideo(n => n <= 3 ? HttpStatusCode.Forbidden : HttpStatusCode.PartialContent);
        var refreshes = 0;
        var reader = new HttpRangeReader(new HttpClient(googleVideo), Info(0), _ => Task.FromResult(Info(++refreshes)), refreshPauses: NoPauses);
        var bytes = await reader.ReadAsync(1_048_576, 65_536, TestContext.Current.CancellationToken);
        Assert.Equal(65_536, bytes.Length);
        Assert.Equal(3, refreshes);
    }

    [Fact]
    public async Task ForbiddenForeverGivesUpAfterFourFreshUrls()
    {
        var googleVideo = new FakeGoogleVideo(_ => HttpStatusCode.Forbidden);
        var refreshes = 0;
        var reader = new HttpRangeReader(new HttpClient(googleVideo), Info(0), _ => Task.FromResult(Info(++refreshes)), refreshPauses: NoPauses);
        var error = await Assert.ThrowsAsync<StreamException>(() => reader.ReadAsync(1_048_576, 65_536, TestContext.Current.CancellationToken));
        Assert.Equal(StreamErrorKind.Extractor, error.Kind);
        Assert.Equal(4, refreshes);
        Assert.Contains("at byte 1048576", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkingUrlResetsTheCount()
    {
        // 403 через раз: каждый раз хватает одного свежего адреса — до отказа не доходит
        var googleVideo = new FakeGoogleVideo(n => n % 2 == 1 ? HttpStatusCode.Forbidden : HttpStatusCode.PartialContent);
        var refreshes = 0;
        var reader = new HttpRangeReader(new HttpClient(googleVideo), Info(0), _ => Task.FromResult(Info(++refreshes)), refreshPauses: NoPauses);
        for (var i = 0; i < 10; i++) await reader.ReadAsync(i * 65_536L, 65_536, TestContext.Current.CancellationToken);
        Assert.Equal(10, refreshes);
    }
}
