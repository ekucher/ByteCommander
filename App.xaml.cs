using System.Text;
using System.Windows;
using ByteCommander.Services;

namespace ByteCommander;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Темний заголовок вікна (Windows 10/11) для всіх вікон застосунку.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => NativeMethods.ApplyDarkChrome((Window)s)));

        DispatcherUnhandledException += (_, ex) =>
        {
            Dialogs.Error(MainWindow, "Неочікувана помилка:\n" + ex.Exception.Message);
            ex.Handled = true;
        };

        base.OnStartup(e);
    }
}
