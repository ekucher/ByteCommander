using System.Text;
using System.Windows;
using ByteCommander.Services;

namespace ByteCommander;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Кожне вікно: заголовок під тему + реакція на зміну теми Windows.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => ThemeManager.Attach((Window)s)));

        DispatcherUnhandledException += (_, ex) =>
        {
            Dialogs.Error(MainWindow, "Неочікувана помилка:\n" + ex.Exception.Message);
            ex.Handled = true;
        };

        base.OnStartup(e);

        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);

        // ByteCommander.exe --view <файл> — лише переглядач (зручно для «Відкрити за допомогою»).
        if (e.Args.Length >= 1 && e.Args[0] is "--view" or "/view")
        {
            var file = e.Args.Length >= 2 ? Path.GetFullPath(e.Args[1]) : null;
            if (file == null || !File.Exists(file))
            {
                // Інакше закриття діалогу (останнього вікна) завершить застосунок з кодом 0 раніше за Shutdown(2).
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dialogs.Error(null, file == null
                    ? "Не вказано файл для перегляду.\n\nВикористання: ByteCommander.exe --view <файл>"
                    : $"Файл не знайдено:\n{file}");
                Shutdown(2);
                return;
            }
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            new ViewerWindow(file, false) { WindowStartupLocation = WindowStartupLocation.CenterScreen }.Show();
            return;
        }

        MainWindow = new MainWindow(settings);
        MainWindow.Show();
    }
}
