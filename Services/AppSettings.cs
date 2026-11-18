using System.Text.Json;
using System.Text.Json.Serialization;

namespace ByteCommander.Services;

/// <summary>Колонка, за якою сортується панель. У JSON зберігається рядком ("Name", "Size", …).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SortKey>))]
public enum SortKey { Name, Ext, Size, Date }

public sealed class PanelState
{
    public List<string> Tabs { get; set; } = new();
    public int ActiveTab { get; set; }
    public SortKey SortBy { get; set; } = SortKey.Name;
    public bool SortDesc { get; set; }
}

public sealed class WindowBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class AppSettings
{
    public PanelState Left { get; set; } = new();
    public PanelState Right { get; set; } = new();
    public bool RightPanelActive { get; set; }
    public bool ShowHidden { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public List<string> Bookmarks { get; set; } = new();
    public List<string> CommandHistory { get; set; } = new();
    public WindowBounds? Window { get; set; }
    public bool Maximized { get; set; }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ByteCommander", "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            // пошкоджений файл налаштувань — стартуємо з типових
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // не критично
        }
    }
}
