using Melogold.Core.Data;
using Melogold.Core.Music;
using Melogold.Playback;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Melogold.App.Services;

/// <summary>Чем делиться из меню коллекции (tasks/0016): название, подпись («исполнитель») и ссылка.</summary>
public sealed record ShareTarget(string Title, string? Subtitle, string Url);

/// <summary>
/// Меню коллекции (REWRITE §3.11.5): «Играть следующим», «В конец очереди», «Добавить в плейлист…», «Включить радио»,
/// «Скопировать ссылку» и «Поделиться» (tasks/0016).
/// </summary>
public sealed class CollectionMenu(PlayerEngine engine, Library library, Snackbar snackbar)
{
    public MenuFlyout Build(Func<Task<IReadOnlyList<Track>>> tracks, ShareTarget? share)
    {
        var menu = new MenuFlyout();

        void Add(string key, string glyph, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key), Icon = new FontIcon { Glyph = glyph } };
            item.Click += async (_, _) =>
            {
                try
                {
                    await action();
                }
                catch (Exception e)
                {
                    Log.Warn("Collection action failed", e);
                    snackbar.Show(Loc.Get("ErrorUnknown"));
                }
            };
            menu.Items.Add(item);
        }

        Add("MenuPlayNext", "", async () =>
        {
            var list = await tracks();
            engine.PlayNext(list);
            snackbar.Show(Loc.Plural("PlayingNextTracks", list.Count));
        });
        Add("MenuAddToQueue", "", async () =>
        {
            var list = await tracks();
            engine.AddToEnd(list);
            snackbar.Show(Loc.Plural("AddedToQueueTracks", list.Count));
        });
        Add("MenuAddToPlaylist", "", async () => await PlaylistPicker.ShowAsync(await tracks(), library, snackbar));
        Add("MenuStartRadio", "", async () =>
        {
            var list = await tracks();
            if (list.Count > 0) engine.StartRadio(list[0]);
        });
        if (share is not null)
        {
            Add("MenuCopyLink", "", () =>
            {
                Share.CopyLink(share.Url);
                return Task.CompletedTask;
            });
            Add("MenuShare", "\uE72D", () =>
            {
                Share.Link(share.Title, share.Subtitle, share.Url);
                return Task.CompletedTask;
            });
        }
        return menu;
    }
}
