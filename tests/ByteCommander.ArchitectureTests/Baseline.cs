// Ratchet-baseline: відоме порушення дозволене, нове — ні; виправлене треба прибрати з файлу.
// Формат рядка: <RuleId> | <повне ім'я типу або шлях файлу>[ | <значення>]
// Кожен прогін записує фактичний стан у architecture-baseline.actual.txt поруч зі збіркою тестів
// (CI завантажує його як артефакт) — з нього оновлюють build/architecture-baseline.txt, коли борг зменшився.
using System.Text.RegularExpressions;

namespace ByteCommander.ArchitectureTests;

public static class Baseline
{
    static readonly object Gate = new();
    static readonly string ActualPath = Path.Combine(AppContext.BaseDirectory, "architecture-baseline.actual.txt");

    static Baseline()
    {
        if (File.Exists(ActualPath)) File.Delete(ActualPath);   // свіжий знімок на кожен прогін
    }

    static readonly Lazy<HashSet<string>> entries = new(() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "architecture-baseline.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal));

    public static IEnumerable<string> For(string ruleId) =>
        entries.Value.Where(e => e.StartsWith(ruleId + " |", StringComparison.Ordinal));

    /// <summary>Згенеровані компілятором вкладені типи (замикання, async-машини) рахуються як їхній зовнішній тип.</summary>
    public static string Normalize(string typeName) => Regex.Replace(typeName, @"\+<.*$", "");

    /// <summary>Порівнює фактичні порушення правила з baseline і падає з поясненням у будь-який бік.</summary>
    public static void Assert(string ruleId, IEnumerable<string> actualSubjects)
    {
        var actual = actualSubjects.Select(s => $"{ruleId} | {Normalize(s)}").ToHashSet(StringComparer.Ordinal);
        lock (Gate) File.AppendAllLines(ActualPath, actual.Order(StringComparer.Ordinal));

        var known = For(ruleId).ToHashSet(StringComparer.Ordinal);
        var added = actual.Except(known).Order(StringComparer.Ordinal).ToList();
        var fixedButListed = known.Except(actual).Order(StringComparer.Ordinal).ToList();

        var msg = new List<string>();
        if (added.Count > 0)
            msg.Add($"Нові порушення {ruleId} (виправте код; baseline не розширюється):\n  " + string.Join("\n  ", added));
        if (fixedButListed.Count > 0)
            msg.Add("Виправлено, але ще в baseline — видаліть рядки з build/architecture-baseline.txt:\n  " + string.Join("\n  ", fixedButListed));
        if (msg.Count > 0) Xunit.Assert.Fail(string.Join("\n\n", msg));
    }
}
