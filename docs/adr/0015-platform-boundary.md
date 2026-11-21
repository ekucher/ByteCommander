# ADR-0015. Платформна межа й кросплатформне Core

- Статус: Proposed
- Дата: 2026-11-21
- Зачіпає: Core, `Platform/Windows`   Доповнює: ADR-0010

## Контекст
Ціль — довгострокова кросплатформність. Застосунок — WPF (лише Windows). Доменна логіка вже містить платформні виклики:
`FolderComparer` → `StrCmpLogicalW`, `Util.WriteViaTemp` → `HardLinkCount`, `RowSources` → `IsSameFile`,
`FileItem` → WPF `ImageSource`, захардкожений роздільник `\`, 42 × `OrdinalIgnoreCase`.

## Розглянуті альтернативи
| Варіант | Плюси | Мінуси |
|---|---|---|
| A. Переписати UI на Avalonia | справжній кросплатформний GUI | великий обсяг, немає підтвердженого запиту (YAGNI) |
| B. `OperatingSystem.IsWindows()` усередині Core | мало коду | межа розмивається, Core тягне P/Invoke |
| C. Core `net10.0` без WPF/Win32; платформа через порти, реалізовані в застосунку | Core збирається й тестується на Linux; основа для CLI | кілька маленьких інтерфейсів |

## Рішення
C. Порти: `INaturalStringComparer`, `IFileIdentity` (hardlink count, same file), `IRecycleBin`, `IShellLauncher`.
Реалізації — у `ByteCommander/Platform/Windows`. Кросплатформний UI не планується без окремого рішення власника.

## Наслідки
CI-job `core-linux` збирає й тестує Core на ubuntu-latest; Windows-специфічні тести позначаються `[Trait("os","windows")]`.

## Ризики
| Ризик | Імовірність | Вплив | Пом'якшення |
|---|---|---|---|
| Різна поведінка ФС у тестах (регістр, ADS, довгі шляхи) | висока | низький | трейти ОС, contract-тести з урахуванням `NameComparer` |

## План міграції
Крок G3 (створення Core) і G5 (провайдер).

## Перевірка
AR-02, BannedApiAnalyzers у Core, job `core-linux`.
