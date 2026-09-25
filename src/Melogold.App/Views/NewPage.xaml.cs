using Melogold.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Views;

public sealed partial class NewPage : Page, IScrollToTop
{
    public NewPage()
    {
        InitializeComponent();
    }

    public void ScrollToTop() => Scroller.ChangeView(null, 0, null);
}
