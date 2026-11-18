using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ByteCommander.Models;

public sealed class FileItem : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsParent { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public FileAttributes Attributes { get; init; }
    public ImageSource? Icon { get; init; }

    bool isMarked;
    long? dirSize;
    bool isSizeCalculating;

    public bool IsMarked
    {
        get => isMarked;
        set { if (isMarked != value) { isMarked = value; Raise(); } }
    }

    /// <summary>Порахований розмір каталогу; null — ще не рахували. Встановлення завершує підрахунок.</summary>
    public long? DirSize
    {
        get => dirSize;
        set
        {
            dirSize = value;
            isSizeCalculating = false;
            Raise();
            Raise(nameof(SizeText));
        }
    }

    public bool IsSizeCalculating
    {
        get => isSizeCalculating;
        set
        {
            isSizeCalculating = value;
            Raise();
            Raise(nameof(SizeText));
        }
    }

    public bool IsHidden => !IsParent && (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();

    public string SizeText => IsParent ? "" :
        IsDirectory ? (isSizeCalculating ? "…" : dirSize is long s ? Util.FormatSize(s) : "<DIR>") :
        Util.FormatSize(Size);

    public string DateText => IsParent ? "" : Modified.ToString("dd.MM.yyyy HH:mm");

    public string AttrText
    {
        get
        {
            if (IsParent) return "";
            var a = Attributes;
            return string.Concat(
                a.HasFlag(FileAttributes.ReadOnly) ? "r" : "-",
                a.HasFlag(FileAttributes.Archive) ? "a" : "-",
                a.HasFlag(FileAttributes.Hidden) ? "h" : "-",
                a.HasFlag(FileAttributes.System) ? "s" : "-");
        }
    }

    public long EffectiveSize => IsDirectory ? dirSize ?? 0 : Size;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed class PanelTab : INotifyPropertyChanged
{
    string location = "";
    bool isActive;

    public string Location
    {
        get => location;
        set { location = value; Raise(); Raise(nameof(Title)); }
    }

    public string Title
    {
        get
        {
            if (location.Length == 0) return "";
            var name = Path.GetFileName(location.TrimEnd('\\'));
            return string.IsNullOrEmpty(name) ? location.TrimEnd('\\') : name;
        }
    }

    public bool IsActive
    {
        get => isActive;
        set { isActive = value; Raise(); }
    }

    public Stack<string> Back { get; } = new();
    public Stack<string> Forward { get; } = new();
    public string? SelectedName { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
