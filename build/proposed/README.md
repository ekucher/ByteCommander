# Запропоновані налаштування збірки (ще не діють)

Ці файли змінюють збірку всього репозиторію, тому їх не ввімкнено разом із документами врядування.
Вмикаються окремим PR (крок G0b у `docs/architecture/refactoring-plan.md`) після першого прогону
`Architecture Gates`, коли відомо кількість попереджень аналізаторів.

| Файл | Куди перенести | Що робить |
|---|---|---|
| `Directory.Build.props` | корінь репозиторію | `AnalysisLevel=latest-recommended`, NuGetAudit (High/Critical — помилка), заборонені API й PublicApiAnalyzers для майбутнього `ByteCommander.Core` |
| `global.json` | корінь | закріплює SDK 10.0 (`rollForward: latestFeature`) |
| `editorconfig.fragment` | `.editorconfig` у корені | базовий стиль і план поступового посилення правил |

Перед перенесенням: перевірити, що `dotnet build` і `dotnet test` на `windows-latest` проходять без нових помилок
(NU1903/NU1904 можуть з'явитися, якщо тестові пакети мають відомі вразливості).
