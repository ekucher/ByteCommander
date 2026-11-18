using System.Text.Json;

namespace ByteCommander.Services;

public sealed class PanelState
{
    public List<string> Tabs { get; set; } = new();
    public int ActiveTab { get; set; }
    public string SortBy { get; set; } = "Name";
    public bool SortDesc { get; set; }
}

public sealed class AppSettings
{
    public PanelState Left { get; set; } = new();
    public PanelState Right { get; set; } = new();
    public string ActiveSide { get; set; } = "Left";
    public bool ShowHidden { get; set; }
    public List<string> Bookmarks { get; set; } = new();
    public List<string> CommandHistory { get; set; } = new();

    public double? WinX { get; set; }
    public double? WinY { get; set; }
    public double? WinW { get; set; }
    public double? WinH { get; set; }
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
