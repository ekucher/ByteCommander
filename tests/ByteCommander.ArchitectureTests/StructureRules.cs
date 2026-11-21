// Правила, які простіше перевірити по вихідному коду, ніж по IL.
using System.Text.RegularExpressions;

namespace ByteCommander.ArchitectureTests;

public class StructureRules
{
    static readonly string Root = FindRepoRoot();
    static IEnumerable<string> Sources() => Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                 && !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}"));
    static string Rel(string f) => Path.GetRelativePath(Root, f).Replace('\\', '/');

    // AR-10: файл не довший за 400 рядків (24 з 30 файлів v0.3.0 уже такі).
    // Файли з baseline можуть лише зменшуватися: значення в baseline — стеля.
    [Fact]
    public void Files_do_not_grow_beyond_limit()
    {
        const int Limit = 400;
        var ceilings = Baseline.For("AR-10").Select(l => l.Split(" | ")).ToDictionary(p => p[1], p => int.Parse(p[2]));
        var problems = new List<string>();
        foreach (var f in Sources())
        {
            int lines = File.ReadLines(f).Count();
            var rel = Rel(f);
            if (ceilings.TryGetValue(rel, out int ceil))
            {
                if (lines > ceil) problems.Add($"{rel}: {lines} > стелі {ceil} (borg-клас не повинен рости)");
            }
            else if (lines > Limit) problems.Add($"{rel}: {lines} > {Limit}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // AR-11: нові статичні mutable-поля заборонені (наявні — у baseline).
    static readonly Regex StaticMutable = new(
        @"^\s*(public|internal|private|protected)?\s*static\s+(?!readonly|class|extern|partial|void|async|implicit|explicit|operator)(event\s+)?[\w<>\[\],?. ]+\s+(\w+)\s*(;|=(?!>)|\{\s*get;\s*(private\s+)?set;)",
        RegexOptions.Multiline);

    [Fact]
    public void No_new_static_mutable_state() =>
        Baseline.Assert("AR-11", Sources().SelectMany(f =>
            StaticMutable.Matches(File.ReadAllText(f)).Select(m => $"{Rel(f)}#{m.Groups[3].Value}")));

    // AR-12: порожні catch без пояснення.
    [Fact]
    public void No_new_empty_catch() =>
        Baseline.Assert("AR-12", Sources().SelectMany(f =>
            Regex.Matches(File.ReadAllText(f), @"catch(\s*\([^)]*\))?\s*\{\s*\}")
                 .Select(_ => Rel(f))).GroupBy(x => x).Select(g => $"{g.Key} | {g.Count()}"));
    // Примітка: для AR-12 значення — кількість у файлі; падає, якщо кількість змінилася (зросла — код, зменшилась — baseline).

    static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "FileManager.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Не знайдено корінь репозиторію");
    }
}
