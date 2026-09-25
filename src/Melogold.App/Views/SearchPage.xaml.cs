using Melogold.App.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Melogold.App.Views;

/// <summary>Выдача поиска «Всё · Музыка · YouTube» (§5.4); открывается в стеке текущего раздела.</summary>
public sealed partial class SearchPage : Page, IScrollToTop
{
    public SearchPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        QueryText.Text = e.Parameter as string ?? "";
    }

    public void ScrollToTop() => Scroller.ChangeView(null, 0, null);
}
