using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Melogold.App.Controls;

/// <summary>
/// Нажимаемая карточка настроек. У <see cref="SettingsCard"/> с <c>IsClickEnabled</c> UI Automation обещает Invoke, но
/// не выполняет его (E_NOINTERFACE): экранный диктор и автоматизация не могут её нажать. Здесь Invoke работает;
/// обработчик — <see cref="Activated"/>, одинаково для мыши, клавиатуры и диктора.
/// </summary>
public partial class ClickableCard : SettingsCard
{
    public ClickableCard()
    {
        IsClickEnabled = true;
        Click += (_, e) => Activated?.Invoke(this, e);
    }

    public event RoutedEventHandler? Activated;

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(ClickableCard owner) : SettingsCardAutomationPeer(owner), IInvokeProvider
    {
        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke && owner.IsClickEnabled ? this : base.GetPatternCore(patternInterface)!;

        public void Invoke()
        {
            if (owner.IsEnabled && owner.IsClickEnabled) owner.Activated?.Invoke(owner, new RoutedEventArgs());
        }
    }
}
