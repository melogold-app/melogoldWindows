using Melogold.App.Services;
using Melogold.App.Views;
using Melogold.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Controls;

/// <summary>
/// «Импорт из ViTune или ViMusic» и «Импорт копии» (tasks/0004 §4): выбор файла, «Импортируем библиотеку…» без кнопок,
/// пока импорт идёт в фоне, затем «Библиотека импортирована» с числами и примечаниями или «Не удалось импортировать» с
/// причиной. Импорт только добавляет: из библиотеки ничего не удаляется.
/// </summary>
public static class ImportFlow
{
    public static async Task RunAsync(XamlRoot root)
    {
        if (App.Current?.Window is not { } window) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads };
        // Копии приходят и без расширения (из мессенджеров)
        picker.FileTypeFilter.Add(".db");
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
#if DEBUG
        // Проверка снимками (tools/shot.ps1): системное окно выбора им не нажать — файл из переменной окружения
        var file = Environment.GetEnvironmentVariable("MELOGOLD_DEBUG_IMPORT") is { Length: > 0 } debug
            ? await Windows.Storage.StorageFile.GetFileFromPathAsync(debug)
            : await picker.PickSingleFileAsync();
        if (file is null) return;
#else
        if (await picker.PickSingleFileAsync() is not { } file) return;
#endif
        await ImportAsync(root, file.Path);
    }

    /// <summary>Импорт копии из файла: выбранного в окне или перетащенного в окно.</summary>
    public static async Task ImportAsync(XamlRoot root, string path)
    {
        var name = Path.GetFileName(path);
        var running = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Get("ImportRunning"),
            Content = new ProgressBar { IsIndeterminate = true, MinWidth = 320, Margin = new Thickness(0, 8, 0, 8) },
        };
        var shown = running.ShowAsync();
        var library = App.Services.GetRequiredService<Library>();
        ImportSummary? summary = null;
        var failure = ImportFailure.Unreadable;
        try
        {
            summary = await Task.Run(() => LibraryImport.Import(library, path, AppInfo.Version));
            Log.Info($"Imported {name}: {summary}");
        }
        catch (ImportException e)
        {
            failure = e.Reason;
            Log.Warn($"Import of {name} failed: {e.Reason}", e.InnerException);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            Log.Warn($"Import of {name} failed", e);
        }
        running.Hide();
        await shown;

        var result = summary is null ? Failed(root, failure) : Done(root, summary);
        if (await result.ShowAsync() == ContentDialogResult.Primary) App.Services.GetRequiredService<Navigator>().Open(typeof(HistoryPage));
    }

    private static ContentDialog Done(XamlRoot root, ImportSummary s)
    {
        var lines = new List<string> { Loc.Format("ImportSummaryFormat", s.Tracks, s.Plays, s.Favorites, s.Lyrics, s.Playlists, s.Saved) };
        if (s.PlaysKnown > 0) lines.Add(Loc.Format("ImportPlaysKnownFormat", s.PlaysKnown));
        if (s.LocalSkipped > 0) lines.Add(Loc.Format("ImportLocalSkippedFormat", s.LocalSkipped));
        if (s.DatesSkipped > 0) lines.Add(Loc.Format("ImportDatesSkippedFormat", s.DatesSkipped));
        // С аккаунтом история уходит на другие устройства через сервер — не сразу
        if (s.Plays > 0 && App.Services.GetRequiredService<Melogold.Server.AccountService>().State is Melogold.Server.AccountState.SignedIn)
            lines.Add(Loc.Get("ImportSyncNote"));
        return new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Get("ImportDoneTitle"),
            Content = new TextBlock { Text = string.Join("\n\n", lines), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            PrimaryButtonText = Loc.Get("ImportOpenHistory"),
            CloseButtonText = Loc.Get("ImportDoneOk"),
            DefaultButton = ContentDialogButton.Close,
        };
    }

    private static ContentDialog Failed(XamlRoot root, ImportFailure failure) => new()
    {
        XamlRoot = root,
        Title = Loc.Get("ImportFailedTitle"),
        Content = new TextBlock
        {
            Text = Loc.Get(failure switch
            {
                ImportFailure.NotABackup => "ImportNotBackup",
                ImportFailure.TooOld => "ImportTooOld",
                ImportFailure.Unsupported => "ImportUnsupported",
                _ => "ImportUnreadable",
            }),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
        },
        CloseButtonText = Loc.Get("ImportDoneOk"),
    };
}
