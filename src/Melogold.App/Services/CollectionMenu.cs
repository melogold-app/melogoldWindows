using Melogold.Core.Data;
using Melogold.Core.Music;
using Melogold.Playback;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Melogold.App.Services;

/// <summary>Меню коллекции (REWRITE §3.11.5): «Играть следующим», «В конец очереди», «Добавить в плейлист…», «Включить радио», ссылка.</summary>
public sealed class CollectionMenu(PlayerEngine engine, Library library, Snackbar snackbar)
{
    public MenuFlyout Build(Func<Task<IReadOnlyList<Track>>> tracks, string? link)
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
        if (link is not null)
        {
            Add("MenuCopyLink", "", () =>
            {
                var package = new DataPackage();
                package.SetText(link);
                Clipboard.SetContent(package);
                snackbar.Show(Loc.Get("LinkCopied"));
                return Task.CompletedTask;
            });
        }
        return menu;
    }
}
