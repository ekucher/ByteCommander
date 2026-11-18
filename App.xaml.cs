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
            new ViewerWindow(file, ViewerMode.View) { WindowStartupLocation = WindowStartupLocation.CenterScreen }.Show();
            return;
        }

        // ByteCommander.exe --diff <ліве> <праве> — порівняння файлів або каталогів (можна підключити як difftool).
        if (e.Args.Length >= 1 && e.Args[0] is "--diff" or "/diff")
        {
            var paths = e.Args.Skip(1).Take(2).Select(Path.GetFullPath).ToArray();
            bool files = paths.Length == 2 && paths.All(File.Exists), dirs = paths.Length == 2 && paths.All(Directory.Exists);
            if (!files && !dirs)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dialogs.Error(null, "Вкажіть два файли або два каталоги, що існують.\n\nВикористання: ByteCommander.exe --diff <ліве> <праве>"
                    + (paths.Length > 0 ? "\n\n" + string.Join("\n", paths.Select(p => (File.Exists(p) || Directory.Exists(p) ? "✓ " : "✗ ") + p)) : ""));
                Shutdown(2);
                return;
            }
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            Window w = files ? new Compare.TextCompareWindow(paths[0], paths[1]) : new Compare.FolderCompareWindow(paths[0], paths[1]);
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Show();
            return;
        }

        MainWindow = new MainWindow(settings);
        MainWindow.Show();
    }
}
