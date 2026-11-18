using System.Diagnostics;
using System.Text;
using ByteCommander.Services;

namespace ByteCommander.Tests;

/// <summary>
/// Тимчасовий робочий каталог для тестів + той самий каталог, доступний через інший диск (subst),
/// щоб перевіряти переміщення між томами. Після тестів subst знімається, каталог видаляється.
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    public string Root { get; }
    /// <summary>Корінь <see cref="Root"/>, видимий як окремий том (наприклад "T:\").</summary>
    public string OtherVolumeRoot { get; }

    readonly string driveLetter;

    public TestWorkspace()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Root = Path.Combine(Path.GetTempPath(), "ByteCommander.Tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);

        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = "TQRSPOVWXUN".FirstOrDefault(c => !used.Contains(c));
        if (free == default) throw new InvalidOperationException("Немає вільної літери диска для subst.");
        driveLetter = free + ":";
        RunSubst($"{driveLetter} \"{Root}\"");
        OtherVolumeRoot = driveLetter + "\\";
        if (!Directory.Exists(OtherVolumeRoot)) throw new InvalidOperationException("subst не спрацював.");
    }

    public string Fresh(string name)
    {
        var d = Path.Combine(Root, name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Записати файл у підкаталог робочого каталогу (підкаталог — зазвичай ім'я тестового класу).</summary>
    public string WriteFile(string group, string name, byte[] bytes)
    {
        var dir = Path.Combine(Root, group);
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    /// <summary>Той самий каталог, але шлях через інший том.</summary>
    public string ViaOtherVolume(string pathUnderRoot) =>
        Path.Combine(OtherVolumeRoot, Path.GetRelativePath(Root, pathUnderRoot));

    public static bool NoTempFiles(string dir) =>
        !Directory.EnumerateFiles(dir, "*.bctmp", SearchOption.AllDirectories).Any();

    public static FileOperation Op(ConflictChoice answer) => new(new ProgressState(), (_, _) => answer);

    public static void CreateJunction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        p.WaitForExit();
        if (!Directory.Exists(link)) throw new InvalidOperationException("Не вдалося створити junction.");
    }

    public static void CreateHardLink(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        p.WaitForExit();
        if (!File.Exists(link)) throw new InvalidOperationException("Не вдалося створити жорстке посилання.");
    }

    static void RunSubst(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("subst.exe", args) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
    }

    public void Dispose()
    {
        RunSubst($"{driveLetter} /d");
        try
        {
            // Спершу прибираємо junction-и, щоб рекурсивне видалення не пішло в цілі посилань.
            foreach (var d in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories)
                         .Where(d => File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)).ToList())
                Directory.Delete(d);
            Directory.Delete(Root, true);
        }
        catch
        {
            // тимчасовий каталог — не критично
        }
    }
}

[CollectionDefinition(Name)]
public sealed class WorkspaceCollection : ICollectionFixture<TestWorkspace>
{
    public const string Name = "workspace";
}
