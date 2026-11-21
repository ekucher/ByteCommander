# CI Architecture Gates

> Пороги не вигадано: кожен виведено з виміряного стану v0.3.0 (`tech-debt.md`) або його запише перший прогін.
> Режими: **report** — лише звіт; **ratchet** — падає, якщо baseline зріс або виправлене не прибрано; **blocking** — будь-яке порушення.
> Workflow: `.github/workflows/architecture.yml`. Зараз усі jobs неблокуючі (`continue-on-error`), доки baseline не звірено (G0b).

| Gate | Інструмент | Baseline | Старт | Посилення |
|---|---|---|---|---|
| Залежності AR-02…AR-07 | TngTech.ArchUnitNET.xUnit 0.13.5 | AR-04: 10 типів; AR-05: 1; AR-06: 1 | ratchet | AR-02/03/07 blocking з G3; AR-04 — після G5 |
| Цикли AR-01 | ArchUnitNET `BeFreeOfCycles` за підпросторами імен | 0 між підпросторами (цикли v0.3.0 проходять через кореневий простір — їх ловлять AR-05/AR-06) | ratchet | blocking після G1 |
| Розмір файлів, стан, catch (AR-10…AR-12) | тести за вихідним кодом | 6 файлів > 400; 5 static mutable; 9 порожніх catch | ratchet | стелі знижуються з кожним кроком G2 |
| Статичний аналіз | .NET analyzers `latest-recommended` → SARIF; `build/Compare-AnalyzerBaseline.ps1` | невідомо, запише перший прогін | report | правило з 0 порушень — одразу `error`; інші — коли дійдуть до 0 |
| Форматування | `dotnet format --verify-no-changes` | невідомо | лише змінені файли | весь код після одного PR «лише форматування» |
| Unit та integration | xUnit (наявний `CI / build-test`) | 102 тести зелені | blocking (як зараз) | + Core.Tests на Windows і Linux |
| Contract-тести провайдерів | `FileProviderContractTests<T>` | — | з G5 | кожен новий провайдер проходить той самий набір |
| Сумісність API | PublicApiAnalyzers; ApiCompat для Plugin SDK | — | з G3 | ApiCompat з фази 6 |
| Вразливості залежностей | `dotnet list package --vulnerable --include-transitive`, NuGetAudit (G0b), Dependabot | невідомо | High/Critical — падає | Moderate — report |
| Секрети | gitleaks + GitHub secret scanning / push protection (налаштування — за дозволом) | повна історія в першому прогоні | неблокуюче до G0b | blocking |
| Кросплатформна збірка | job `core-linux` (ubuntu-latest); компіляція WPF з `EnableWindowsTargeting=true` | Core ще немає | з G3 | macOS — лише з CLI |
| Покриття | coverlet `XPlat Code Coverage` | невідомо | report | «покриття змінених файлів Core не падає» після 2 релізів |

## Ratchet

`build/architecture-baseline.txt` містить рядки `AR-xx | суб'єкт[ | значення]`. Тест падає, якщо з'явився новий рядок
або якщо рядок із baseline більше не відтворюється (борг виправлено — приберіть рядок). Фактичний стан кожного прогону
пишеться в `architecture-baseline.actual.txt` (артефакт `architecture-baseline-actual`).
Для аналізаторів — `build/analyzers-baseline.json` (кількість за ID правила), перший запуск з `-Update` його створює.

## Required checks (після G0b і K-9)

`CI / build-test`, `Architecture Gates / tests`, `analyzers`, `format`, `secrets`, `dependencies`; з G3 — `core-linux`.
