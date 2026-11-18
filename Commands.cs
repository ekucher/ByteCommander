using System.Windows.Input;

namespace ByteCommander;

/// <summary>Комбінація клавіш (модифікатори + клавіша) з людським підписом.</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static Hotkey Of(Key key, ModifierKeys modifiers = ModifierKeys.None) => new(modifiers, key);

    public bool IsFunctionKey => Key is >= Key.F1 and <= Key.F24;

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    static string KeyName(Key k) => k switch
    {
        Key.Add => "Num +",
        Key.Subtract => "Num −",
        Key.Multiply => "Num *",
        Key.OemPlus => "=",
        Key.Oem3 => "`",
        Key.Oem5 => "\\",
        Key.Delete => "Del",
        Key.Apps => "Menu",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Return => "Enter",
        _ => k.ToString(),
    };
}

/// <summary>
/// Команда застосунку — єдине джерело правди: з неї будуються обробник клавіш,
/// панель F-клавіш, контекстні меню, палітра команд.
/// </summary>
public sealed class AppCommand
{
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required Action Run { get; init; }

    /// <summary>Комбінації, які обробляє головне вікно.</summary>
    public IReadOnlyList<Hotkey> Keys { get; init; } = Array.Empty<Hotkey>();

    /// <summary>Клавіші, які обробляє сама панель (Space, Backspace, Insert…) — лише для підпису.</summary>
    public string? PanelKeys { get; init; }

    public string Gesture =>
        string.Join(" / ", (PanelKeys is null ? Enumerable.Empty<string>() : new[] { PanelKeys }).Concat(Keys.Select(k => k.ToString())));
}
