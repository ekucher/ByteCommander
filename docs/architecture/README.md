# Архітектура ByteCommander

> Описує цільовий стан і правила переходу (ADR-0013). Фактичний стан v0.3.0 і борг — `tech-debt.md`, план — `refactoring-plan.md`, CI — `ci-gates.md`.

## 1. Модель

**Модульний моноліт.** Один процес, один інсталяційний пакет. Модулі — це логічні межі з власним
публічним API; збірка (проєкт) створюється лише тоді, коли межу треба перевіряти компілятором
або коли з'являється другий споживач (ADR-13).

**Напрямок залежностей (Clean Architecture):**

```
Presentation (WPF)  ─┐
                     ├─►  Application-модулі Core  ─►  Provider API (Vfs)  ─►  Common
Windows-адаптери  ───┘         ▲
       (реалізують порти Core) ┘
```

Залежності спрямовані лише всередину. Core не посилається на WPF, Win32, COM і на застосунок.

## 2. Проєкти

| Проєкт | TFM | Вміст | Може посилатися на |
|---|---|---|---|
| `ByteCommander` | net10.0-windows, WPF | вікна, панелі, теми, реєстр команд UI, `Platform/Windows` (P/Invoke, Shell, кошик, DWM), composition root | `ByteCommander.Core` |
| `ByteCommander.Core` | net10.0 | модулі Common, Vfs, Vfs.Local, Navigation, Operations, Compare, Search, Text, Settings | лише BCL |
| `ByteCommander.Core.Tests` | net10.0 | unit + contract + інтеграційні з реальною ФС | Core |
| `ByteCommander.Tests` | net10.0-windows | тести застосунку (XAML, адаптери Windows) | застосунок, Core |
| `ByteCommander.ArchitectureTests` | net10.0-windows | правила залежностей з baseline | обидві збірки |

Окремі `ByteCommander.Platform.Windows`, `ByteCommander.Cli`, `ByteCommander.Plugin.Abstractions`
з'являються лише за ADR, коли буде другий споживач (CLI) або Plugin SDK.

## 3. Модулі

Див. `modules.md`: відповідальність, публічний API, дозволені залежності, помилки,
асинхронність, безпека, тести, версіонування для кожного модуля.

## 4. Наскрізні правила

- **Асинхронність:** будь-який I/O — `Task`/`IAsyncEnumerable` з `CancellationToken` останнім параметром.
  Жодного `.Result`/`.Wait()` в UI. Таймаут — відповідальність виклику (Navigation задає таймаут читання каталогу).
- **Помилки:** очікувані збої файлових систем — `FileProviderException` з `FileErrorCode`;
  пакетні операції повертають `OperationResult` з переліком помилок по елементах; скасування — `OperationCanceledException`.
- **Платформа:** Windows-специфіка — лише у `ByteCommander/Platform/Windows` за портами Core
  (`INaturalStringComparer`, `IFileIdentity`, `IRecycleBin`, `IShellLauncher`).
- **Регістр імен:** порівняння імен — через `IFileProvider.NameComparer`, а не `StringComparison.OrdinalIgnoreCase`.
- **Стан:** без статичного mutable-стану; сервіси реєструються в composition root (`App.xaml.cs`).
- **Журнал:** `ILogger<T>` (ADR-12), без секретів.

## 5. Контроль

Правила перевіряються в CI (`.github/workflows/architecture.yml`):
architecture tests з baseline, заборонені API, аналізатори, форматування, тести, contract-тести провайдерів,
сумісність публічного API Core, аудит залежностей, сканування секретів, збірка Core на Linux.
Наявний борг зафіксовано в `build/architecture-baseline.txt` (і, з кроку G0b, `build/analyzers-baseline.json`);
baseline може лише зменшуватися. Деталі — `ci-gates.md`.

## 6. Рішення

`docs/adr/` — ADR-0001…0012 (цільова архітектура, 21.11.2026) і ADR-0013…0020 (врядування). Усі зі статусом Proposed, доки власник не погодить кожен окремо.
