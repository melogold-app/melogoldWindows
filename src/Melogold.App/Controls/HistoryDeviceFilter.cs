using Melogold.App.Services;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Controls;

/// <summary>
/// Чьи прослушивания показать (tasks/0002 §3.5) — История и «Итоги» (tasks/0015): «Все устройства · Это устройство ·
/// имя…». Устройства, которого уже нет в аккаунте, в списке нет вовсе (tasks/0018): его прослушивания — только во «Все
/// устройства». Виден с аккаунтом, когда есть прослушивания других устройств аккаунта.
/// </summary>
public sealed partial class HistoryDeviceFilter : ComboBox
{
    private readonly Library _library = App.Services.GetRequiredService<Library>();
    private readonly AccountService _account = App.Services.GetRequiredService<AccountService>();
    private readonly KnownDevices _devices = App.Services.GetRequiredService<KnownDevices>();

    public HistoryDeviceFilter()
    {
        MinWidth = 200;
        VerticalAlignment = VerticalAlignment.Top;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(this, Loc.Get("HistoryDeviceChoose"));
        ToolTipService.SetToolTip(this, Loc.Get("HistoryDeviceChoose"));
        SelectionChanged += (_, _) =>
        {
            if (SelectedItem is not ComboBoxItem { Tag: HistoryDevice filter } || filter == Filter) return;
            Filter = filter;
            FilterChanged?.Invoke();
        };
        Loaded += (_, _) => _devices.Changed += OnDevicesChanged;
        Unloaded += (_, _) => _devices.Changed -= OnDevicesChanged;
    }

    public HistoryDevice Filter { get; private set; } = HistoryDevice.All;

    public event Action? FilterChanged;

    private string? CurrentDeviceId => _account.State is AccountState.SignedIn signedIn ? signedIn.DeviceId : null;

    // Выбранное устройство удалили, пока открыт экран, — снова «Все устройства»
    private void OnDevicesChanged() => DispatcherQueue.TryEnqueue(async () =>
    {
        if (await RefreshAsync()) FilterChanged?.Invoke();
    });

    /// <summary>
    /// Перечитать устройства с прослушиваниями; без аккаунта или без других устройств аккаунта фильтра нет. true —
    /// выбранного устройства больше нет, фильтр вернулся к «Все устройства».
    /// </summary>
    public async Task<bool> RefreshAsync()
    {
        var selected = Filter;
        var current = CurrentDeviceId;
        IReadOnlyList<DeviceDto> others = [];
        if (current is not null)
        {
            var ids = await Task.Run(_library.HistoryDeviceIds);
            others = KnownDevices.Others(ids, current, await _devices.ListAsync());
        }
        if (current is null || others.Count == 0)
        {
            Visibility = Visibility.Collapsed;
            Filter = HistoryDevice.All;
            return selected != HistoryDevice.All;
        }
        var options = new List<(string Text, string? Glyph, HistoryDevice Filter)>
        {
            (Loc.Get("HistoryDeviceAll"), null, HistoryDevice.All),
            (Loc.Get("HistoryDeviceThis"), DeviceSymbols.Glyph("windows"), HistoryDevice.This(current)),
        };
        options.AddRange(others
            .Select(d => (d.Name, (string?)DeviceSymbols.Glyph(d.Platform), HistoryDevice.Other(d.Id)))
            .OrderBy(o => o.Item1, StringComparer.CurrentCulture));
        Items.Clear();
        foreach (var (text, glyph, filter) in options)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (glyph is not null) content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
            content.Children.Add(new TextBlock { Text = text });
            var item = new ComboBoxItem { Content = content, Tag = filter };
            AutomationProperties.SetName(item, text);
            Items.Add(item);
        }
        SelectedIndex = Math.Max(0, options.FindIndex(o => o.Filter == selected));
        Filter = options[SelectedIndex].Filter;
        Visibility = Visibility.Visible;
        return Filter != selected;
    }
}
