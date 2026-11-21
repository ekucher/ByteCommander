# Правила залежностей

> Рядок залежить від стовпця. ✓ дозволено · ✗ заборонено · П — лише через порт (інтерфейс Core) · — сам модуль.

| ↓ залежить від → | Common | Vfs | Vfs.Local | Text | Settings | Navigation | Operations | Compare | Search | Platform.Win | Themes | FeatureUI | Panels | Shell |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Common** | — | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |
| **Vfs** | ✓ | — | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |
| **Vfs.Local** | ✓ | ✓ | — | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | П | ✗ | ✗ | ✗ | ✗ |
| **Text** | ✓ | ✗ | ✗ | — | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |
| **Settings** | ✓ | ✗ | ✗ | ✗ | — | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |
| **Navigation** | ✓ | ✓ | ✗ | ✗ | ✗ | — | ✗ | ✗ | ✗ | П | ✗ | ✗ | ✗ | ✗ |
| **Operations** | ✓ | ✓ | ✗ | ✓ | ✗ | ✗ | — | ✗ | ✗ | П | ✗ | ✗ | ✗ | ✗ |
| **Compare** | ✓ | ✓ | ✗ | ✓ | ✗ | ✗ | ✓ | — | ✗ | П | ✗ | ✗ | ✗ | ✗ |
| **Search** | ✓ | ✓ | ✗ | ✓ | ✗ | ✗ | ✗ | ✗ | — | ✗ | ✗ | ✗ | ✗ | ✗ |
| **Platform.Win** | ✓ | ✓ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | — | ✗ | ✗ | ✗ | ✗ |
| **Themes** | ✓ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✓ | — | ✗ | ✗ | ✗ |
| **FeatureUI** | ✓ | ✓ | ✗ | ✓ | ✗ | ✗ | ✓ | ✓ | ✓ | ✓ | ✓ | — | ✗ | ✗ |
| **Panels** | ✓ | ✓ | ✗ | ✗ | ✗ | ✓ | ✓ | ✗ | ✗ | ✓ | ✓ | ✗ | — | ✗ |
| **Shell** | ✓ | ✓ | ✗ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — |

Composition root (`App.xaml.cs`) може посилатися на будь-який модуль, зокрема `Vfs.Local` і `Platform.Win`.

## Модулі ↔ простори імен

| Модуль | Простір імен / шлях |
|---|---|
| Common | `ByteCommander.Core.Common` |
| Vfs | `ByteCommander.Core.Vfs` |
| Vfs.Local | `ByteCommander.Core.Vfs.Local` |
| Text | `ByteCommander.Core.Text` |
| Settings | `ByteCommander.Core.Settings` |
| Navigation | `ByteCommander.Core.Navigation` |
| Operations | `ByteCommander.Core.Operations` |
| Compare | `ByteCommander.Core.Compare` |
| Search | `ByteCommander.Core.Search` |
| Platform.Win | `ByteCommander.Platform.Windows` (папка `Platform/Windows` у застосунку) |
| Themes | `ByteCommander.Themes` |
| FeatureUI | `ByteCommander.Viewer`, `ByteCommander.Compare` (вікна), `ByteCommander.Dialogs`, `ByteCommander.Search` |
| Panels | `ByteCommander.Controls` |
| Shell | `ByteCommander` (`MainWindow`, `Commands`, `App`) |

## Загальні заборони

- Цикли між будь-якими модулями — заборонено (перевіряється зрізами за простором імен).
- `*.Internal.*` іншого модуля — заборонено.
- Core → `System.Windows*`, `Microsoft.Win32*`, P/Invoke, COM, `System.Diagnostics.Process` — заборонено.
- Presentation (Shell, Panels, FeatureUI, Themes) → `System.IO.File`, `Directory`, `FileInfo`, `DirectoryInfo`,
  `DriveInfo`, `FileStream`, `FileSystemWatcher` — заборонено (baseline для наявного коду).

## Як змінити матрицю

Лише через ADR з обґрунтуванням, альтернативами й планом міграції; PR змінює цей файл і правила
в `tests/ByteCommander.ArchitectureTests` одночасно.
