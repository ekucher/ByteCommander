// Правила з docs/architecture/dependency-rules.md (AR-01…AR-07).
// API звірено з вихідним кодом TngTech.ArchUnitNET 0.13.5: ResideInNamespaceOrBelow, NotDependOnAny(IObjectProvider),
// Types(includeReferenced), SliceRuleDefinition.Slices().Matching(...).Should().BeFreeOfCycles(), ICanBeEvaluated.Evaluate.
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Slices;
using ArchUnitNET.Fluent.Syntax.Elements.Types;
using ArchUnitNET.Loader;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace ByteCommander.ArchitectureTests;

public class DependencyRules
{
    // Збірка читається з диска як метадані (Mono.Cecil), без завантаження WPF у процес тестів.
    // Types() без includeReferenced = лише типи завантажених збірок. З етапу G3 сюди додається ByteCommander.Core.dll.
    static readonly Architecture Arch = new ArchLoader()
        .LoadFilteredDirectory(AppContext.BaseDirectory, "ByteCommander.dll", SearchOption.TopDirectoryOnly)
        .Build();

    /// <summary>
    /// Типи UI: кореневий простір імен застосунку (App, MainWindow, вікна, Dialogs, Util), Controls, Themes,
    /// а в Compare/Viewer — лише вікна й елементи відображення. Алгоритми цих папок (FolderComparer, RowSources)
    /// належать до майбутніх модулів Core і сюди не входять.
    /// </summary>
    const string PresentationPattern =
        @"^ByteCommander\.[^.+]+(\+.*)?$" +
        @"|^ByteCommander\.(Controls|Themes)\." +
        @"|^ByteCommander\.(Compare|Viewer)\.[^.+]*(Window|View|Row|LineText)(\+.*)?$";

    static GivenTypesConjunction PresentationTypes() => Types().That().HaveFullNameMatching(PresentationPattern);

    static IEnumerable<string> Violations(ICanBeEvaluated rule) =>
        rule.Evaluate(Arch)
            .Where(r => !r.Passed)
            .Select(r => r.EvaluatedObject is IHasName n ? n.FullName : r.Description)
            .Distinct();

    [Fact] // AR-01: жодних циклів між підпросторами імен застосунку (з G3 — і між модулями Core).
    public void No_cycles_between_modules() =>
        Baseline.Assert("AR-01", Violations(
            SliceRuleDefinition.Slices().Matching("ByteCommander.(*)").Should().BeFreeOfCycles()));

    [Fact] // AR-02: Core не залежить від WPF, Win32 і платформних адаптерів. Поки Core немає — правило порожнє.
    public void Core_is_platform_neutral() =>
        Baseline.Assert("AR-02", Violations(
            Types().That().ResideInNamespaceOrBelow("ByteCommander.Core")
                .Should().NotDependOnAny(Types(true).That().ResideInNamespaceOrBelow("System.Windows")
                    .Or().ResideInNamespaceOrBelow("Microsoft.Win32")
                    .Or().ResideInNamespaceOrBelow("ByteCommander.Platform"))
                .WithoutRequiringPositiveResults()));

    [Fact] // AR-03: Core не знає про UI застосунку.
    public void Core_does_not_depend_on_presentation() =>
        Baseline.Assert("AR-03", Violations(
            Types().That().ResideInNamespaceOrBelow("ByteCommander.Core")
                .Should().NotDependOnAny(PresentationTypes())
                .WithoutRequiringPositiveResults()));

    [Fact] // AR-04: UI не працює з диском напряму (наявне — у baseline).
    public void Presentation_does_not_touch_file_system() =>
        Baseline.Assert("AR-04", Violations(
            PresentationTypes().Should().NotDependOnAny(Types(true).That()
                .HaveFullName("System.IO.File").Or().HaveFullName("System.IO.Directory")
                .Or().HaveFullName("System.IO.FileInfo").Or().HaveFullName("System.IO.DirectoryInfo")
                .Or().HaveFullName("System.IO.DriveInfo").Or().HaveFullName("System.IO.FileStream")
                .Or().HaveFullName("System.IO.FileSystemWatcher"))));

    [Fact] // AR-05: сервіси не залежать від вікон (зараз OperationRunner → ProgressWindow).
    public void Services_do_not_depend_on_windows() =>
        Baseline.Assert("AR-05", Violations(
            Types().That().ResideInNamespaceOrBelow("ByteCommander.Services")
                .Or().ResideInNamespaceOrBelow("ByteCommander.Core")
                .Should().NotDependOnAny(Types().That().HaveNameEndingWith("Window"))));

    [Fact] // AR-06: ніхто, крім composition root (App), не посилається на MainWindow.
    public void Only_composition_root_knows_MainWindow() =>
        Baseline.Assert("AR-06", Violations(
            Types().That().DoNotHaveFullName("ByteCommander.App")
                .And().DoNotHaveFullName("ByteCommander.MainWindow")
                .And().DoNotHaveFullNameMatching(@"^ByteCommander\.(App|MainWindow)\+")   // вкладені типи й замикання
                .Should().NotDependOnAny(Types().That().HaveFullName("ByteCommander.MainWindow"))));

    [Fact] // AR-07: не звертатися до Internal модулів Core ззовні (діє з G3; поки правило порожнє).
    public void No_access_to_other_modules_internals() =>
        Baseline.Assert("AR-07", Violations(
            Types().That().ResideInNamespaceOrBelow("ByteCommander.Core").And().DoNotHaveFullNameMatching(@"\.Internal\.")
                .Should().NotDependOnAny(Types().That().HaveFullNameMatching(@"^ByteCommander\.Core\..*\.Internal\."))
                .WithoutRequiringPositiveResults()));
    // Спрощення: AR-07 забороняє Internal-типи всьому коду поза Internal. Перевірка «свій модуль дозволено»
    // додається разом із першим Internal-простором імен.
}
