# Реєстр архітектурного технічного боргу

> Стан на v0.3.0 (`bb3ae2c`), аналіз від 21.11.2026. Пріоритети: **P0** — блокує roadmap або ризик втрати даних;
> **P1** — виправити до відповідної фази; **P2** — попутно. Автоматично відстежувані пункти мають рядки в
> `build/architecture-baseline.txt`. Закриваючи пункт, приберіть його рядки з baseline і позначте тут.

## Метрики baseline

| Показник | v0.3.0 | Ціль |
|---|---|---|
| Файли C# / рядки | 30 / 7 589; медіана 158; 24 з 30 ≤ 400 | — |
| Файли > 400 рядків | FilePanel 1 067 · MainWindow 925 · RowSources 602 · ViewerWindow 575 · FileOperation 562 · TextCompareWindow 424 | не ростуть; ≤ 400 |
| Сильно зв'язані компоненти (файли) | 2 (8 + 2 файли) | 0 після G1 |
| Звернення до System.IO з UI-класів | 93 | 0 після G5 |
| Статичний mutable-стан | `FilePanel.ShowHidden`, `FilePanel.dragSource`, `ThemeManager.Current/IsDark/Changed`, `ShellIcons.Cache` | лише `ThemeManager` як фасад WPF |
| Порожні `catch {}` / усі `catch` | 9 / 88 | порожні лише з поясненням |
| `async void` / ад-хок `Task.Run` | 11 / 12 | `async void` лише в обробниках подій |
| `OrdinalIgnoreCase` для імен | 42 | `NameComparer` провайдера |
| `Process.Start` зі склеюванням аргументів | 7 | один `IShellLauncher` з `ArgumentList` |
| Попередження аналізаторів, покриття | невідомо | baseline з першого прогону CI |

## Порушення

| ID | Порушення | Пріоритет | Доказ | Крок |
|---|---|---|---|---|
| AV-01 | Тривалі файлові операції в UI-потоці (Navigate, Refresh, Init, меню дисків/закладок, UpdateFree, `CalcSize` без скасування) | P0 | `Controls/FilePanel.xaml.cs:114,159,225,493`, `MainWindow.xaml.cs:868` | G4 |
| AV-02 | `Util` — універсальний клас із 6 підсистем (формат, шляхи, маски, порівняння файлів, атомарний запис, Shell, WPF-фабрика) | P1 | `Util.cs` | G1 |
| AV-03 | `MainWindow` — God class (команди, операції, конфлікти, буфер, командний рядок, закладки, теми, порівняння) | P1 | 925 рядків, > 90 методів | G2 |
| AV-04 | `FilePanel` — God class (UI + диск + watcher + вкладки + історія + сортування + фільтр + drag&drop) | P0 | 1 067 рядків | G2 → G4 |
| AV-05 | Цикли: SCC-1 (MainWindow, FilePanel, Dialogs, SearchWindow, ViewerWindow, обидва вікна порівняння, OperationRunner), SCC-2 (Util ↔ NativeMethods) | P1 | `Dialogs/SearchWindow.cs:28`, `Services/OperationRunner.cs:25,47`, `Services/NativeMethods.cs:118`, `Util.cs:168` | G1 |
| AV-06 | Сервіс залежить від UI: `OperationRunner` → `ProgressWindow`, `Dialogs.Error` | P1 | `Services/OperationRunner.cs:25,47,52` | G1 |
| AV-07 | Глобальний mutable-стан: `ShowHidden`, `dragSource`, статична подія `ThemeManager.Changed`, `ShellIcons.Cache` без синхронізації | P1 | `Controls/FilePanel.xaml.cs:16,47`, `Services/ThemeManager.cs:22–26`, `Services/ShellIcons.cs:12` | G2, G3 |
| AV-08 | Немає абстракції ФС: 200 звернень до System.IO у 17 файлах | P0 | ADR-0001 | G5 |
| AV-09 | Платформний код у доменній логіці (`StrCmpLogicalW`, `HardLinkCount`, `IsSameFile`, `ImageSource` у моделі, роздільник `\`) | P1 | `Compare/FolderComparer.cs:107`, `Util.cs:168`, `Viewer/RowSources.cs:45`, `Models/FileItem.cs:16` | G3 |
| AV-10 | Бізнес-логіка в code-behind (пошук, збереження документа, планування копіювання з порівняння — причина F-01, валідація імені — причина F-06) | P1 | `Dialogs/SearchWindow.cs:178,234`, `Dialogs/ViewerWindow.cs:491`, `Compare/FolderCompareWindow.xaml.cs:245–275`, `MainWindow.xaml.cs:469` | G2 |
| AV-11 | Обхід рушія операцій: Rename, MakeDir, NewFile, кошик, вставка з буфера | P1 | `MainWindow.xaml.cs:433–548` | G2 → G6 |
| AV-12 | Дублювання: політика конфліктів, запуск процесів, порівняння шляхів, ад-хок `Task.Run` | P2 | grep | G1–G2 |
| AV-13 | Немає composition root і DI | P2 | `App.xaml.cs` | G3 (ADR-0018) |
| AV-14 | Один проєкт: межу UI/логіка не перевіряє компілятор; тести залежать від exe | P1 | `FileManager.csproj` | G3 (ADR-0013) |
| AV-15 | Неоднорідна модель помилок; немає журналу | P1 | `App.xaml.cs:17`, `MainWindow.xaml.cs:530` | G2–G3 (ADR-0016, ADR-0012) |
| AV-16 | Реєстр команд без `Id` і `RequiredCapabilities`, WPF `Key` у моделі | P2 | `Commands.cs` | G2 |
| AV-17 | Редактор без моделі документа | P2 | `Dialogs/ViewerWindow.cs:206–520` | G2 |
| AV-18 | Немає архітектурного врядування (правила, ADR, CI-перевірки) | P0 | — | G0 (цей PR) |

## Знахідки безпеки й надійності з аудиту

Повний опис — в артефакті аудиту від 21.11.2026. Архітектурні причини вказано в колонці «AV».

| ID | Знахідка | Серйозність | AV |
|---|---|---|---|
| F-01 | Копіювання з порівняння каталогів перезаписує новіші файли призначення | Критична | AV-10 |
| F-02 | `settings.json` пишеться неатомарно, при пошкодженні тихо скидається | Висока | — |
| F-03 | Видалення назавжди без захищених шляхів і порогу | Висока | AV-11 |
| F-04 | ZIP без лімітів (кількість, розмір, коефіцієнт) | Середня | — |
| F-05 | ZIP: ім'я з `:` стає ADS на NTFS | Середня | AV-10 |
| F-06 | Перейменування з `\` або `..` переносить об'єкт | Середня | AV-10 |
| F-07 | Винятки поглинаються, журналу немає | Висока | AV-15 |
| F-08 | Кошик через `SHFileOperation` (MAX_PATH, без власника вікна) | Середня | AV-09 |
| F-09 | Копіювання втрачає ADS (Mark of the Web) і ACL | Середня | — |
| F-10 | Сторонні shell-розширення в процесі | Низька | — |
| F-11 | Синхронна мережа в UI-потоці | Висока | AV-01 |
| F-12 | Релізи без підпису, actions не закріплені, без SBOM | Середня | — |
| F-13 | Пошук за вмістом може завантажувати хмарні файли-заглушки | Низька | — |
