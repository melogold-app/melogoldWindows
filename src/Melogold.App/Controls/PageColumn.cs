using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Controls;

/// <summary>
/// Колонка страницы не шире <c>maxWidth</c> посередине прокрутки. Сама WinUI ставит элемент с <c>MaxWidth</c> не по
/// центру, когда окно шире колонки (развёрнутое окно): колонка уезжает вправо и обрезается краем. Здесь ширина и
/// левый отступ считаются от ширины прокрутки.
/// </summary>
internal static class PageColumn
{
    public static void Center(ScrollViewer scroller, FrameworkElement column, double maxWidth)
    {
        var margin = column.Margin;
        column.MaxWidth = double.PositiveInfinity;
        column.HorizontalAlignment = HorizontalAlignment.Left;
        void Layout()
        {
            var available = scroller.ActualWidth;
            if (available <= 0) return;
            var width = Math.Max(0, Math.Min(maxWidth, available - margin.Left - margin.Right));
            column.Width = width;
            column.Margin = new Thickness(Math.Max(margin.Left, (available - width) / 2), margin.Top, 0, margin.Bottom);
        }
        scroller.SizeChanged += (_, _) => Layout();
        Layout();
    }
}
