using Melogold.Core.Music;
using Melogold.Playback;

namespace Melogold.App.Services;

/// <summary>
/// «Сохранить файлом» (Android <c>SaveFileEntry</c>): трек — файлом .m4a с названием, исполнителем, альбомом и обложкой,
/// туда, куда выберет пользователь (по умолчанию «Музыка»). Байты — из загрузок или кэша, а если трека нет целиком
/// нигде — из сети (заодно он ляжет в кэш). Звук не перекодируется: те же кадры AAC (<see cref="Mp4Writer"/>).
/// </summary>
public sealed class FileExport(TrackDownloads downloads, ImageCache images, Snackbar snackbar)
{
    private const int MaxNameLength = 120;

    public async Task SaveAsync(Track track)
    {
        if (App.Current?.Window is not { } window) return;
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary,
            SuggestedFileName = FileName(track),
        };
        picker.FileTypeChoices.Add(Loc.Get("SaveFileType"), [".m4a"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSaveFileAsync() is not { } file) return;

        snackbar.Show(Loc.Format("SaveFileStartedFormat", track.Title));
        try
        {
            var bytes = await downloads.ReadWholeAsync(track.VideoId);
            var cover = await CoverAsync(track);
            var m4a = await Task.Run(() => Mp4Writer.FromFragmented(bytes, new Mp4Tags(track.Title, track.ArtistsText, track.AlbumTitle, cover)));
            await File.WriteAllBytesAsync(file.Path, m4a);
            Log.Info($"Saved {track.VideoId} as a file ({m4a.Length / 1024} KB)");
            snackbar.Show(Loc.Get("SaveFileDone"), Loc.Get("SaveFileOpen"), () => _ = Windows.System.Launcher.LaunchFileAsync(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or StreamException or Mp4FormatException or TaskCanceledException)
        {
            Log.Warn($"Save of {track.VideoId} as a file failed", e);
            snackbar.Show(Loc.Format("SaveFileFailedFormat", track.Title));
        }
    }

    /// <summary>«Исполнитель — Название» без знаков, которые Windows не пускает в имя файла.</summary>
    private static string FileName(Track track)
    {
        var name = string.IsNullOrWhiteSpace(track.ArtistsText) ? track.Title : $"{track.ArtistsText} — {track.Title}";
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name;
    }

    /// <summary>Обложка 1200 px из кэша изображений (у кадра видео — уже без чёрных полей); нет — файл без обложки.</summary>
    private async Task<byte[]?> CoverAsync(Track track)
    {
        if (Thumbnails.Sized(track.ThumbnailUrl ?? Thumbnails.ForVideo(track.VideoId), 1200) is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        try
        {
            return await images.GetAsync(uri) is { } path ? await File.ReadAllBytesAsync(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
