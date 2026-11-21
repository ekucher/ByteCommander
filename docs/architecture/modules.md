# Контракти модулів

> Назви типів — цільові; поточні джерела вказано в «Звідки».
> Спільні правила для всіх модулів: `README.md`, розділ 4. Дозволені залежності: `dependency-rules.md`.

## Спільна модель помилок

```csharp
public enum FileErrorCode { NotFound, AccessDenied, AlreadyExists, InvalidName, NotSupported,
    ChangedSinceRead, DiskFull, Locked, PathTooLong, Transient, Corrupt, LimitExceeded, Unknown }

public sealed class FileProviderException(FileErrorCode code, string message, string? path = null,
    ProviderCapability? missing = null, Exception? inner = null) : IOException(message, inner);

public sealed record ItemError(string Path, FileErrorCode Code, string Message);
public sealed record OperationResult(int Done, int Skipped, IReadOnlyList<ItemError> Errors, bool Canceled);
```

- Очікуваний збій одного елемента в пакетній операції → `ItemError`, операція продовжується (як зараз `FileOperation.Errors`).
- Збій, після якого продовжувати немає сенсу (корінь недоступний, диск повний) → `FileProviderException`.
- Скасування → `OperationCanceledException`, ніколи не `ItemError`.
- Помилка програміста (null, недопустимий стан) → `ArgumentException`/`InvalidOperationException`, не перехоплюється.
- `catch {}` без коментаря «чому безпечно» заборонено; перехоплене логуються через `ILogger`.

## Спільна модель асинхронності

- I/O-методи: `…Async(…, CancellationToken ct)`; `ct` — останній параметр, без значення за замовчуванням у публічному API Core.
- Потокові результати — `IAsyncEnumerable<T>` (перелік каталогу, пошук), щоб UI показував перші рядки до кінця обходу.
- Прогрес — `IProgress<T>` або спостережуваний стан задачі; Core не викликає `Dispatcher`.
- Запит до користувача з робочого потоку (конфлікт, підтвердження) — через делегат/інтерфейс, переданий викликачем (`IConflictResolver`).
- Core не створює власних потоків, крім рушія операцій (черга з обмеженою паралельністю).

---

## Common

| Поле | Контракт |
|---|---|
| Відповідальність | Найменші спільні типи без I/O: форматування розміру, плюралізація, маски імен, `ItemError`, `OperationResult`, порти платформи (`INaturalStringComparer`, `IClock`). |
| Межі | Не містить I/O, WPF, бізнес-правил конкретних модулів. Новий тип сюди — лише якщо ним користуються ≥ 2 модулі. |
| Звідки | `Util.FormatSize/Plural/Count/Describe/MatchAny`, частина `Util.PathEquals` |
| Публічний API | `SizeFormat`, `Plural`, `NameMask`, порти, типи помилок |
| Залежності | лише BCL |
| Помилки | без винятків, крім `ArgumentException` |
| Асинхронність | синхронний, чистий |
| Безпека | — |
| Тести | unit (наявні тести форматування й плюралізації переносяться) |
| Версіонування | разом із Core |

## Vfs (Provider API)

| Поле | Контракт |
|---|---|
| Відповідальність | Абстракція джерела файлів: `VfsPath`, `IFileProvider`, `IFileEntry`, `ProviderCapabilities`, `IWriteSession`, узгодження можливостей. ADR-1, ADR-17. |
| Межі | Не знає про панелі, операції, UI. Не містить реалізацій, крім `InMemoryFileProvider` для тестів. |
| Публічний API | `IFileProvider { Id; ApiVersion; NameComparer; NegotiateAsync(root, requested, ct); ListAsync; StatAsync; OpenReadAsync; OpenWriteAsync; CreateDirectoryAsync; DeleteAsync; RenameAsync }`; розширення `IFileMetadataProvider`, `IChecksumProvider`, `IFileSearchProvider`, `IWatchableProvider` (ADR-1) |
| Можливості | `ProviderCapability` (прапорці): `Read, Write, Delete, Rename, AtomicRename, ServerCopy, Resume, Seek, Posix, Acl, Symlinks, Checksums, Recycle, Watch, Locking, CaseSensitive`. **Узгодження:** `NegotiateAsync` повертає `MountCapabilities` для конкретного кореня/сесії (статичні можливості типу ∩ виявлені на сервері, напр. `posix-rename@openssh.com`). Споживач перевіряє `caps.Has(x)` до планування; виклик методу без можливості → `FileProviderException(NotSupported, missing: x)` до будь-яких змін. UI фільтрує команди за `RequiredCapabilities`. |
| Обмеження | Ім'я сегмента без роздільників; `VfsPath` не виходить за `Root` (`..` нормалізується, вихід — `InvalidName`). |
| Помилки | `FileProviderException` з `FileErrorCode`; мапінг `IOException.HResult` (0x80070070 → DiskFull, 0x20 → Locked) у провайдері. |
| Асинхронність | усі методи async з `ct`; `ListAsync` — потоковий; провайдер потокобезпечний для паралельних читань. |
| Безпека | Провайдер не обходить reparse points при видаленні/переміщенні без явного прапорця; шляхи з ADS (`:`) і зарезервовані імена відхиляються локальним провайдером. |
| Тести | **Contract-набір** `FileProviderContractTests<T>`: однакові сценарії для InMemory, Local, (пізніше) SFTP у контейнері; сценарії, що вимагають можливості, пропускаються або перевіряють `NotSupported`. |
| Версіонування | `IFileProvider.ApiVersion` (ціле, зараз 1). Додавання методу з типовою реалізацією — мінорне; зміна сигнатури — ADR + нова версія інтерфейсу. Стабільність гарантується з моменту Plugin SDK (ADR-19). |

## Vfs.Local

| Поле | Контракт |
|---|---|
| Відповідальність | `LocalFileProvider` над `System.IO` (NTFS/ReFS/SMB-UNC на Windows; ext4/APFS — для Core-тестів на Linux). |
| Звідки | `FilePanel.LoadItems`, частини `FileOperation`, `Util.WriteViaTemp/OpenShared/ScanFiles`, `NativeMethods.HardLinkCount` (через порт `IFileIdentity`) |
| Залежності | Common, Vfs; платформа — через порти `IFileIdentity`, `IRecycleBin` |
| Можливості | Read, Write, Delete, Rename, AtomicRename, Seek, Resume, Symlinks, Watch; Recycle — якщо зареєстровано `IRecycleBin`; CaseSensitive — за ФС кореня |
| Безпека | Атомарний запис через temp + rename (`IWriteSession`), збереження hardlink/атрибутів як у `WriteViaTemp`; `FileShare` як зараз. |
| Тести | contract-набір + наявні інтеграційні тести `FileOperationTests`, `ReviewFixTests`, міжтомні через `subst` (лише Windows, `[Trait("os","windows")]`) |
| Версіонування | разом із Core |

## Navigation

| Поле | Контракт |
|---|---|
| Відповідальність | Модель панелі без WPF: поточний каталог, вкладки, історія назад/вперед, сортування, фільтр, позначення, швидкий пошук як предикат, асинхронне завантаження каталогу з таймаутом і скасуванням попереднього. |
| Межі | Не малює, не знає про `ListView`, іконки, drag&drop, контекстне меню Провідника. |
| Звідки | `FilePanel.Navigate/LoadItems/Refresh/Compare/HistoryStep/*Tab*/Mark*/SetSort`, `PanelTab` |
| Публічний API | `PanelModel { CurrentPath; Items; Tabs; NavigateAsync(path, select, ct); RefreshAsync; GoBack/GoForward/GoUp; SetSort; SetFilter; Mark… }`, `PanelEntry` (без `ImageSource`) |
| Залежності | Common, Vfs |
| Помилки | недоступний каталог → `NavigationResult.Failed(code)`; модель лишається на попередньому каталозі |
| Асинхронність | новий `NavigateAsync` скасовує попередній; таймаут читання (типово 10 с, налаштовується); результат застосовується лише якщо запит актуальний |
| Безпека | — |
| Тести | unit з `InMemoryFileProvider`: історія, сортування (натуральне через порт), фільтр, скасування, таймаут, недоступний каталог |
| Версіонування | внутрішній для застосунку |

## Operations

| Поле | Контракт |
|---|---|
| Відповідальність | Планування й виконання файлових операцій: копіювання, переміщення, видалення (кошик/назавжди), перейменування, створення каталогів, ZIP; конфлікти; `DeletePolicy`; `NameValidator`; черга (ADR-2). |
| Межі | Єдиний шлях зміни файлів. Rename/MkDir/NewFile/Recycle теж ідуть сюди (зараз в обхід). |
| Звідки | `FileOperation`, `ProgressState`, `ConflictChoice`, `OperationRunner` (без UI-частини), `MainWindow.Rename/MakeDir/Delete` |
| Публічний API | `IOperationService { PlanAsync(request, ct) → OperationPlan; ExecuteAsync(plan, IConflictResolver, IProgress<OperationProgress>, ct) → OperationResult }`; `DeletePolicy.Check(plan)`; `NameValidator.Validate(name, provider)` |
| Залежності | Common, Vfs, Text (кодування імен ZIP) |
| Можливості | План обирає стратегію за `MountCapabilities`: ServerCopy → серверне копіювання; AtomicRename → temp+rename, інакше попередження «перезапис не атомарний»; Recycle відсутній → лише явне «назавжди». |
| Помилки | `ItemError` на елемент; фатальні — `FileProviderException`; план із порушенням `DeletePolicy` не виконується без підтвердження |
| Асинхронність | виконання у фоні з `ct`; скасування під час заміни лишає або старий, або новий файл (ніколи частковий) |
| Безпека | захищені шляхи й поріг масового видалення (F-03); валідація імен (F-05, F-06); ліміти ZIP (F-04); повторна перевірка перед заміною (TOCTOU); посилання не обходяться |
| Тести | unit для планувальника й політик; integration з реальною ФС; failure injection (locked, disk full через малий том, скасування на кожній фазі); регресійні для F-xx |
| Версіонування | внутрішній; з Plugin SDK — лише `IOperationJob` як публічний контракт |

## Compare

| Поле | Контракт |
|---|---|
| Відповідальність | Порівняння текстів (Майєрс) і каталогів; планувальник копіювання з результату порівняння (без перезапису новіших — F-01). |
| Звідки | `TextDiff`, `FolderComparer`, `DiffArguments`, логіка `FolderCompareWindow.CopyAsync`, `TextCompareWindow.Side.Load` |
| Публічний API | `TextDiff.Compare`, `FolderComparer.CompareAsync(left, right, options, ct)`, `CompareCopyPlanner.Plan(tree, direction) → (OperationRequest, NewerOnTarget[])` |
| Залежності | Common, Vfs, Text, Operations (лише типи запиту) |
| Помилки | недоступна сторона → результат з позначкою, не виняток |
| Асинхронність | `ct` у всіх довгих викликах (вже є) |
| Безпека | новіші файли призначення — лише з явним переліком і підтвердженням |
| Тести | наявні 36 тестів + регресія F-01 |
| Версіонування | внутрішній |

## Search

| Поле | Контракт |
|---|---|
| Відповідальність | Обхід дерева, фільтри (маски, текст; пізніше дати, розмір, regex), потокові результати. |
| Звідки | `SearchWindow.Worker`, `SearchWindow.FileContains` |
| Публічний API | `ISearchEngine.SearchAsync(root, SearchQuery, ct) → IAsyncEnumerable<SearchHit>` |
| Залежності | Common, Vfs, Text |
| Обмеження | ліміт розміру файла для пошуку вмісту (зараз 256 МБ) — параметр запиту |
| Безпека | пропускати файли-заглушки хмарних сховищ (F-13) за замовчуванням; не слідувати symlink-циклам |
| Тести | unit з InMemory; наявні тести `FileContains` переносяться |
| Версіонування | внутрішній |

## Text

| Поле | Контракт |
|---|---|
| Відповідальність | Визначення й перетворення кодувань, закінчення рядків, індексація великих файлів (джерела рядків для переглядача). |
| Звідки | `TextEncodings`, `TextLines`, `Viewer/RowSources` (без `NativeMethods.IsSameFile` → порт `IFileIdentity`) |
| Залежності | Common |
| Обмеження | читання потоками; ліміти розміру для повного завантаження (8 МБ для редактора — параметр) |
| Тести | наявні тести кодувань, переглядача й follow |
| Версіонування | внутрішній |

## Settings

| Поле | Контракт |
|---|---|
| Відповідальність | Модель налаштувань, атомарне збереження з `.bak`, `SchemaVersion` і міграції (ADR-11). |
| Звідки | `AppSettings`, `PanelState`, `WindowBounds` |
| Публічний API | `ISettingsStore { Load() → SettingsLoadResult; SaveAsync(settings, ct) }`; міграції — чисті функції |
| Залежності | Common |
| Помилки | пошкоджений файл → `SettingsLoadResult.Corrupt` з варіантами для UI, без тихого скиду (F-02) |
| Безпека | секрети ніколи не зберігаються в JSON (ADR-5) |
| Тести | сумісність (наявний тест), пошкоджений, порожній, майбутня версія схеми |
| Версіонування | `SchemaVersion`; невідомі поля зберігаються |

## Presentation (застосунок)

| Поле | Контракт |
|---|---|
| Відповідальність | Shell (`MainWindow`, реєстр команд), Panels (`FilePanel`), FeatureUI (переглядач, порівняння, пошук, діалоги), Themes; composition root. |
| Правила | без прямого I/O; без бізнес-правил; один клас — один екран/контрол; команди мають стабільний `Id` і `RequiredCapabilities` |
| Тести | XAML-тести (наявні), FlaUI — нічний необов'язковий job |

## Platform.Windows (папка застосунку)

| Поле | Контракт |
|---|---|
| Відповідальність | P/Invoke, COM, Shell: іконки, контекстне меню, кошик (`IFileOperation`, F-08), DWM, `StrCmpLogicalW`, hardlink count, запуск процесів з `ArgumentList`. Реалізує порти Core. |
| Залежності | Common, Vfs (порти) |
| Безпека | аргументи процесів лише через `ArgumentList`; COM-ресурси звільняються у `finally` (як зараз) |
| Тести | адаптерні тести на Windows |
| Версіонування | внутрішній |

## Майбутні модулі (створюються лише за ADR відповідної фази)

Security (CredentialStore, KnownHosts, LogRedactor — ADR-5), Sync (SyncPlanner, стан — ADR-3),
Providers.Sftp (ADR-4), Archive provider (ADR-7), Plugin.Abstractions + хост (ADR-6), Git (ADR-8).
Кожен отримує картку за цим шаблоном до першого PR з кодом.
