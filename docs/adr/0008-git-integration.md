# ADR-0008. Git Integration

- Статус: Proposed
- Дата: 2026-11-21
- Джерело: цільова архітектура, частина 2 аудиту (артефакт від 21.11.2026)

## Рішення

Виклик системного git.exe (status --porcelain=v2 -z, log, diff) через ArgumentList; diff показувати в наявному TextCompareWindow. Ніяких фонових fetch/commit; hooks виконуються лише при явних діях (commit), з попередженням для чужих репозиторіїв `PROPOSED`.

## Розглянуті альтернативи

LibGit2Sharp — відстає від git і тягне нативну бібліотеку.

## Перевірка (тести)

Великі репо (таймаут/скасування), detached HEAD, worktrees, submodules, Unicode-імена (core.quotepath).

## Примітка

У вихідному ADR не розписано: Контекст, Ризики, План міграції. Доповнити за шаблоном `0000-template.md` перед зміною статусу на Accepted.
