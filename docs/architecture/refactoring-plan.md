# План рефакторингу без переписування

> Strangler, а не переписування: кожен крок — кілька PR по 100–400 змінених рядків; рефакторинг і зміна поведінки —
> окремі PR; 102 наявні тести не видаляються; після кожного PR застосунок працює як раніше.
> Оцінки — робочі дні одного розробника, достовірність середня.

```mermaid
flowchart LR
  G0[G0 Врядування + CI report] --> G1[G1 Розрив циклів]
  H[Hotfix v0.3.1 F-01..F-06] -.-> G2
  G1 --> G2[G2 Логіка з UI]
  G2 --> G3[G3 Проєкт Core + DI]
  G3 --> G4[G4 Async-навігація]
  G4 --> G5[G5 Provider API + Local]
  G5 --> G6[G6 Черга операцій]
```

| Крок | Зміст | Критерії приймання | Відкат | Оцінка / версія |
|---|---|---|---|---|
| **G0** Врядування | AGENTS.md, CLAUDE.md, `docs/architecture`, ADR-0001…0020, architecture tests + baseline, workflow у неблокуючому режимі, шаблон PR, Dependabot | Код застосунку не змінено; CI зелений; workflow `Architecture Gates` публікує звіт і `architecture-baseline.actual.txt`; власник погодив AGENTS.md | revert PR | 2–3 д, паралельно з v0.3.1 |
| **G0b** Посилення | Звірити baseline з `actual`; увімкнути `build/proposed/` (Directory.Build.props, global.json, .editorconfig) окремим PR; записати `analyzers-baseline.json`; зробити gates required (K-9) | Baseline збігається з першим прогоном; збірка без нових помилок; required checks увімкнено власником | revert PR | 1 д |
| **G1** Розрив циклів | Розділити `Util`; `OperationRunner` → виконавець + `IOperationPresenter`; `SearchWindow` з делегатом замість `MainWindow`; `NativeMethods` без `Util`; винести `Dialogs`, `ProgressWindow`, `Util` з кореневого простору імен | 0 SCC на рівні файлів; AR-05, AR-06 з порожнім baseline; 102 тести зелені; ручний чекліст (копіювання, пошук → перейти до файла, помилка операції) | revert кожного PR | 3–5 д, v0.4 |
| **G2** Логіка з UI | У папку `Core/` того ж проєкту: `SearchEngine`, `CompareCopyPlanner` (з F-01), `NameValidator` (з F-05/F-06), `ViewerDocument`, `PanelModel` (`ShowHidden` → модель), Rename/MkDir/NewFile/Recycle через `OperationService`, `CommandRegistry` з `Id` | Нові класи без `System.Windows`; unit-тести для кожного; стелі AR-10 для `FilePanel` і `MainWindow` нижчі щонайменше на 30%; AR-11 без `ShowHidden`; поведінка не змінилась | revert PR | 6–9 д, v0.4 |
| **G3** Проєкт Core | `src/ByteCommander.Core` (net10.0) і перенос `Core/`; порти платформи з адаптерами; `Core.Tests`; composition root (M.E.DI); PublicApi- і BannedApiAnalyzers | Core збирається й тестується на ubuntu-latest; AR-02/03/07 blocking; `InternalsVisibleTo` лише для тестів; self-contained пакет не більший на > 5% | revert | 3–5 д, v0.4 |
| **G4** Async-навігація | `PanelModel.NavigateAsync` з таймаутом і скасуванням; `FilePanel` лише відображає; асинхронні меню дисків і закладок; `CalcSize` зі скасуванням | UI не блокується > 100 мс на недоступній шарі; фокус і вибір збережено; прапорець `Features.AsyncNavigation` | прапорець | 4–6 д, v0.4 |
| **G5** Provider API | ADR-0001 + ADR-0017: `VfsPath`, `IFileProvider`, InMemory, Local, contract-набір; міграція Navigation → Operations → Search → Compare | Contract-набір зелений для InMemory і Local; AR-04 з порожнім baseline і blocking; 0 `OrdinalIgnoreCase` у Core | прапорець; старий шлях до v0.6 | 10–16 д, v0.5 |
| **G6** Черга операцій | ADR-0002 за roadmap | Критерії фази 2 roadmap | за roadmap | v0.5–v0.6 |

Hotfix v0.3.1 не чекає на G1–G2: його виправлення одразу пишуться як чисті класи (`CompareCopyPlanner`,
`NameValidator`, `DeletePolicy`), і G2 лише переносить їх у `Core/`.
