# Architecture Decision Records

Кожне суттєве архітектурне рішення — окремий файл `NNNN-коротка-назва.md` за шаблоном `0000-template.md`.

**Суттєве рішення:** новий модуль/збірка; нова NuGet-залежність; зміна Provider API, рушія операцій,
формату налаштувань, публічного API Core, матриці залежностей; виняток із правил AGENTS.md (розділ 3).

**Процес:** ADR зі статусом Proposed подається окремим PR (або разом із першим кодом, якщо зміна мала) →
власник погоджує → статус Accepted. Прийнятий ADR не редагується по суті: нове рішення створює новий ADR
зі статусом «Supersedes ADR-N», а старий отримує «Superseded by».

| № | Назва | Статус |
|---|---|---|
| 1 | File Provider Architecture | Proposed (21.11.2026), уточнюється ADR-17 |
| 2 | Operation Engine | Proposed |
| 3 | Synchronization State Model | Proposed (K-3) |
| 4 | SSH/SFTP Library | Proposed |
| 5 | Credential Storage | Proposed |
| 6 | Plugin Isolation | Proposed |
| 7 | Archive Engine | Proposed |
| 8 | Git Integration | Proposed |
| 9 | Remote Editing | Proposed |
| 10 | Windows Server Compatibility | Proposed |
| 11 | Settings Migration | Proposed |
| 12 | Logging & Telemetry | Proposed |
| 13 | Modular Monolith і межі модулів | Proposed |
| 14 | Архітектурні тести з baseline (ratchet) | Proposed |
| 15 | Платформна межа й кросплатформне Core | Proposed |
| 16 | Модель помилок, асинхронності й скасування | Proposed |
| 17 | Узгодження можливостей провайдерів | Proposed, уточнює ADR-1 |
| 18 | Composition root і впровадження залежностей | Proposed |
| 19 | Публічний API і версіонування | Proposed |
| 20 | Правила для AI-агентів як частина архітектури | Proposed |
