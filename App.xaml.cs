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
            var rawArgs = e.Args.Skip(1).Take(2).ToArray();
            var tempFiles = new List<string>();
            var paths = rawArgs.Select(a => ResolveDiffArgument(a, tempFiles)).ToArray();
            Exit += (_, _) => DeleteDiffTempFiles(tempFiles);
            bool files = paths.Length == 2 && paths.All(p => p is not null && File.Exists(p));
            bool dirs = paths.Length == 2 && paths.All(p => p is not null && Directory.Exists(p));
            if (!files && !dirs)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dialogs.Error(null, "Вкажіть два файли або два каталоги, що існують.\n\nВикористання: ByteCommander.exe --diff <ліве> <праве>"
                    + (rawArgs.Length > 0 ? "\n\n" + string.Join("\n", rawArgs.Select((a, i) => (paths[i] is { } p && (File.Exists(p) || Directory.Exists(p)) ? "✓ " : "✗ ") + a)) : ""));
                Shutdown(2);
                return;
            }
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            // files/dirs істинні лише якщо обидва шляхи не null (перевірено вище).
            Window w = files ? new Compare.TextCompareWindow(paths[0]!, paths[1]!) : new Compare.FolderCompareWindow(paths[0]!, paths[1]!);
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Show();
            return;
        }

        MainWindow = new MainWindow(settings);
        MainWindow.Show();
    }

    /// <summary>
    /// Повертає повний шлях для аргументу --diff. Null-пристрій замінюється порожнім тимчасовим файлом,
    /// який додається до <paramref name="tempFiles"/> для видалення при виході. Повертає <c>null</c>, якщо шлях некоректний.
    /// </summary>
    static string? ResolveDiffArgument(string arg, List<string> tempFiles)
    {
        if (!DiffArguments.IsNullDevice(arg))
            return DiffArguments.TryGetFullPath(arg);

        var tempPath = Path.Combine(Path.GetTempPath(), $"ByteCommander-empty-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        tempFiles.Add(tempPath);
        return tempPath;
    }

    /// <summary>Видаляє тимчасові файли, створені для null-пристрою; помилки видалення ігноруються.</summary>
    static void DeleteDiffTempFiles(IEnumerable<string> tempFiles)
    {
        foreach (var file in tempFiles)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
