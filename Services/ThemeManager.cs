using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Win32;

namespace ByteCommander.Services;

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme { System, Dark, Light }

/// <summary>
/// Перемикає палітру на льоту: підміняє словник кольорів (перший у MergedDictionaries застосунку).
/// Усі стилі посилаються на кольори через DynamicResource, тож відкриті вікна перефарбовуються одразу.
/// </summary>
public static class ThemeManager
{
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
        var palette = new ResourceDictionary
        {
            Source = new Uri($"/ByteCommander;component/Themes/Colors.{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };
        app.Resources.MergedDictionaries[0] = palette;
        foreach (Window w in app.Windows) NativeMethods.ApplyChrome(w);
        Changed?.Invoke();
    }

    /// <summary>Викликається на WM_SETTINGCHANGE "ImmersiveColorSet" — користувач змінив тему Windows.</summary>
    public static void OnSystemThemeChanged()
    {
        if (Current == AppTheme.System && SystemPrefersDark() != IsDark) Apply(AppTheme.System);
    }

    public static string DisplayName(AppTheme t) => t switch
    {
        AppTheme.Dark => "темна",
        AppTheme.Light => "світла",
        _ => "як у системі",
    };

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
