# Кнопки и значки по §4.5: размеры значков и зоны нажатия

Статус: открыто

Из аудита `docs/design/AUDIT.md` (задание 0022).

## Что не так

- Значки Segoe Fluent Icons чёткие в размерах 16, 20, 24, 32… ([Segoe Fluent Icons](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font));
  в кнопках шапок (`CollectionHeader.AddButton`, `AddToggle`, `AddMenu`), карточке лучшего результата поиска и ряде
  других мест — 14.
- Зона нажатия по Windows — около 7,5 мм, 40×40 epx ([Targeting](https://learn.microsoft.com/windows/apps/develop/input/guidelines-for-targeting));
  кнопки строки (♡, «…») и часть кнопок панели воспроизведения — 32 по ширине. Замерить на снимках (0026) и довести
  часто нажимаемые до 40, не раздувая строку.

## Проверка

- Поиск `FontSize = 14` у `FontIcon` — пусто или каждое место объяснено.
- Замер на снимке: ♡, «…», ▶ и соседние кнопки панели — не меньше 40 по высоте зоны нажатия.
