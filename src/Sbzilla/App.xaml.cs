using Microsoft.UI.Xaml;
using Sbzilla.ServiceBus;

namespace Sbzilla;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;
        // Interactive Entra sign-in (WAM broker) needs a parent window handle.
        ServiceBusClients.ParentWindow = () => WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.Activate();
    }
}
