using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ByteCommander.Services;

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme { System, Dark, Light }

/// <summary>
/// Перемикає палітру на льоту: підміняє словник кольорів (перший у MergedDictionaries застосунку).
/// Усі стилі посилаються на кольори через DynamicResource, тож відкриті вікна перефарбовуються одразу.
/// Кожне вікно підключається через <see cref="Attach"/>: отримує заголовок під тему й слухає зміну теми Windows.
/// </summary>
public static class ThemeManager
{
    const int WM_SETTINGCHANGE = 0x001A;

    public static AppTheme Current { get; private set; } = AppTheme.System;
    public static bool IsDark { get; private set; } = true;

    /// <summary>Тема змінилась (для елементів, що малюють себе самі).</summary>
    public static event Action? Changed;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        IsDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemPrefersDark(),
        };
        var app = Application.Current;
        if (app == null) return;
        app.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"/ByteCommander;component/Themes/Colors.{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };
        foreach (Window w in app.Windows) ApplyChrome(w);
        Changed?.Invoke();
    }

    /// <summary>Наступна тема для кнопки на панелі інструментів: як у системі → темна → світла → …</summary>
    public static AppTheme Next(AppTheme t) => t switch
    {
        AppTheme.System => AppTheme.Dark,
        AppTheme.Dark => AppTheme.Light,
        _ => AppTheme.System,
    };

    public static string DisplayName(AppTheme t) => t switch
    {
        AppTheme.Dark => "темна",
        AppTheme.Light => "світла",
        _ => "як у системі",
    };

    /// <summary>
    /// Підключити вікно (викликається для кожного вікна застосунку при завантаженні): заголовок під тему
    /// і реакція на зміну теми Windows — зокрема у вікні переглядача в режимі --view, де головного вікна немає.
    /// </summary>
    public static void Attach(Window w)
    {
        ApplyChrome(w);
        HwndSource.FromHwnd(new WindowInteropHelper(w).Handle)?.AddHook(OnWindowMessage);
    }

    static IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            OnSystemThemeChanged();
        return IntPtr.Zero;
    }

    /// <summary>Користувач змінив тему Windows. Повідомлення приходить у кожне вікно — повторні виклики нічого не роблять.</summary>
    public static void OnSystemThemeChanged()
    {
        if (Current == AppTheme.System && SystemPrefersDark() != IsDark) Apply(AppTheme.System);
    }

    static void ApplyChrome(Window w)
    {
        var caption = (Application.Current?.TryFindResource(ThemeKeys.BgBrush) as SolidColorBrush)?.Color;
        NativeMethods.SetWindowChrome(new WindowInteropHelper(w).Handle, IsDark, caption);
    }

    static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
        catch
        {
            return true;
        }
    }
}
