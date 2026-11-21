# ADR-0019. Публічний API і версіонування

- Статус: Proposed
- Дата: 2026-11-21   Пов'язано: ADR-0006, ADR-0011, ADR-0017

## Контекст
Наступні фази (провайдери, Plugin SDK, CLI) потребують стабільних контрактів. Зараз публічного API немає:
усе — один exe, тести використовують `InternalsVisibleTo`.

## Розглянуті альтернативи
| Варіант | Плюси | Мінуси |
|---|---|---|
| A. Не відстежувати до Plugin SDK | без шуму | ламаємо контракти непомітно |
| B. PublicApiAnalyzers з G3, ApiCompat з Plugin SDK | зміни видно в диффі; строга перевірка там, де є зовнішні споживачі | файли `PublicAPI.*.txt` у PR |

## Рішення
B.

| Що | Схема | Інструмент | Коли |
|---|---|---|---|
| Застосунок | SemVer тегу `vX.Y.Z`; `Version` у csproj звіряється з тегом | release.yml | одразу |
| Публічний API Core | `PublicAPI.Shipped.txt` / `Unshipped.txt` | Microsoft.CodeAnalysis.PublicApiAnalyzers | з G3 |
| Provider API | `IFileProvider.ApiVersion`; зміна сигнатури — нова версія + ADR | contract-тести | з G5 |
| Plugin SDK | окремий пакет SemVer | Package validation / ApiCompat | фаза 6 |
| settings.json | `SchemaVersion` + міграції | ADR-0011 | v0.4 |
| Гарячі клавіші, CLI | без змін без запису в CHANGELOG і погодження | тест реєстру | одразу |

## Наслідки
`internal` за замовчуванням у Core; новий `public` тип — свідоме рішення.

## Ризики
| Ризик | Імовірність | Вплив | Пом'якшення |
|---|---|---|---|
| Шум від PublicAPI-файлів | середня | низький | автофікс аналізатора |

## План міграції
G3, G5, фаза 6 roadmap.

## Перевірка
RS0016/RS0017 як error у Core.
