# Выжимка руководств: Windows

Ядро клиента по `docs/DESIGN-DOCTRINE.md` §2: WinUI 3, Fluent 2. Руководства: [Fluent 2](https://fluent2.microsoft.design), [Windows app design](https://learn.microsoft.com/windows/apps/design/).

Сверено: 2026-10-07 — Windows 11 (сборка 26300), Windows App SDK 2.5.1 (WinUI 1.8), CommunityToolkit.WinUI 8.2. Страницы
Microsoft Learn — по их версиям на эту дату (дата обновления страницы указана у ссылки, где она важна).

Каждый раздел: что говорит руководство (коротко, своими словами, со ссылкой на страницу) и как это сделано у нас.

## Компоненты по умолчанию

Руководство: «используйте стандартные элементы, которые пользователь уже знает» ([Navigation basics](https://learn.microsoft.com/windows/apps/design/basics/navigation-basics));
движение и состояния — из самих элементов WinUI, свои анимации только когда готовых нет ([Motion](https://learn.microsoft.com/windows/apps/design/signature-experiences/motion)).

| Элемент | Системный компонент | У нас |
|---|---|---|
| Список треков | `ListView` (выделение Extended, перетаскивание) | `Controls/MusicListView` — `ListView` со строкой `MusicRow`; в несколько колонок — `ItemsWrapGrid` (`UseColumns`) |
| Сетка карточек | `GridView` | `ShelfView.CardGrid` |
| Ряд карточек (полка) | `ListView` с горизонтальной `ItemsStackPanel` | `ShelfView.CardRow` — исключение «Полки» |
| Меню | `MenuFlyout` (контекстное — `ContextRequested`, Shift+F10) | `TrackActions.BuildMenu`, `CollectionMenu`, `ShelfView.MenuFor` |
| Поиск | `AutoSuggestBox` | `SearchBox` в `TitleBar` (`MainWindow.xaml`) |
| Фильтр и разделы выдачи | `SelectorBar` | `SearchPage` («Всё · Музыка · YouTube» и фильтры) |
| Лист, диалог | `ContentDialog` | `DescriptionDialog`, `ArtistAboutDialog`, `TrackDetailsDialog`, `LinkDeviceDialog`, `ShortcutsDialog` … |
| Сообщение о состоянии экрана | `InfoBar` | «Нет сети — данные от…», заметки поиска (`StateView`, `SearchPage`) |
| Всплывающее сообщение с «Отменить» | — в WinUI нет | свой `Snackbar` — исключение |
| Панель действий над выделенным | `CommandBar` | `Controls/SelectionBar` |
| Главная навигация | `NavigationView` слева + `Frame` на раздел, `TitleBar` с «Назад» | `MainWindow.xaml` |
| Настройки | `SettingsCard` / `SettingsExpander` (Community Toolkit — вид «Параметров» Windows) | `SettingsPage.xaml` |
| Кнопки | `Button`, `AccentButtonStyle` для главного действия, `ToggleButton`, `HyperlinkButton` | везде; круглые кнопки шапки исполнителя — исключение |
| Загрузка | `ProgressRing` | `StateView` (кольцо через 300 мс) |
| Перелистывание страниц | `FlipView` + `PipsPager` | `YearRecapPage` |

## Отступы и сетка

[Content layout and spacing](https://learn.microsoft.com/windows/apps/design/basics/content-basics) (обновлено 2026-09-11) и
[Screen sizes and breakpoints](https://learn.microsoft.com/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design):

- Все размеры и отступы кратны **4 epx** — тогда при масштабах 125–400 % края попадают в целые пиксели (текста не касается).
- 8 — между кнопками, между кнопкой и её всплывающим, между полем и его заголовком; 12 — между полем и подписью и между
  областями содержимого; 16 — от края поверхности до текста; элементы внутри `Expander` — с отступом 48.
- Ширина окна: маленькое — до 640, среднее — 641–1007, большое — от 1008 (считается окно, а не экран).

У нас: поля страницы 36, в узком окне (уже 600) — 16 (`MusicListView`, `ArtistPage`, `ArtistHero`, `CollectionHeader`);
колонка страниц с текстом не шире заданной (`PageColumn`); популярные треки исполнителя — 2 колонки от 640, 3 от 1040.
Граница «узко» у нас 600, у Windows 640 — разница в аудите не считается расхождением: 600 выбрано под боковую панель
навигации, которая в этом диапазоне сворачивается.

## Типографика

[Typography in Windows](https://learn.microsoft.com/windows/apps/design/signature-experiences/typography):

- Шрифт — Segoe UI Variable (кириллица в нём есть), один на всё приложение; вес Regular для текста, Semibold для
  заголовков; Bold и курсив в шкалу не входят.
- Шкала (epx, размер/интерлиньяж): Caption 12/16, Body 14/20, Body Strong 14/20, Body Large 18/24, Subtitle 20/28,
  Title 28/36, Title Large 40/52, Display 68/92 — в XAML это стили `CaptionTextBlockStyle` … `DisplayTextBlockStyle`.
- Минимум — 12 Regular и 14 Semibold; регистр — как в предложении; выравнивание влево; не вмещается — многоточие
  (обрезка без многоточия — редко).
- Строка текста — 50–60 знаков.

У нас: только стили шкалы (`PageTitleStyle` — на основе Title); шапки — Title / Title Large; строки списков — Body и
Caption. Отступления — в исключениях: «Сейчас играет», текст песни и картинка «Итогов года» (размеры под картинку 1080×1920).

## Цвет и материалы

[Color](https://learn.microsoft.com/windows/apps/design/signature-experiences/color),
[Materials](https://learn.microsoft.com/windows/apps/design/signature-experiences/materials),
[Geometry](https://learn.microsoft.com/windows/apps/design/signature-experiences/geometry):

- Тема — светлая или тёмная по Windows; цвета — из системных кистей темы (`TextFillColorPrimaryBrush`,
  `CardBackgroundFillColorDefaultBrush` …), акцентный цвет пользователя — скупо: главное действие и состояние.
- Mica — основа окна; Acrylic — только временные поверхности (меню, всплывающие); Smoke — затемнение под диалогом.
- Скругления: 8 — окна, всплывающие, диалоги (`OverlayCornerRadius`); 4 — элементы внутри страницы: кнопки, строки,
  карточки (`ControlCornerRadius`); 0 — где прямые края стыкуются.
- Контраст текста — не меньше 4,5 : 1 ([Accessible text](https://learn.microsoft.com/windows/apps/design/accessibility/accessible-text-requirements)).

У нас: Mica у окна, системные кисти, акцентная кнопка — только «Слушать». Свои цвета — только там, где фон задаёт
обложка («Сейчас играет», текст песни) и в шапке исполнителя (тёмное затемнение под белым текстом) — исключения.

## Навигация

[Navigation basics](https://learn.microsoft.com/windows/apps/design/basics/navigation-basics):

- Больше пяти разделов верхнего уровня — `NavigationView` слева; у каждого раздела свой `Frame`; глубже двух уровней —
  подумать о `BreadcrumbBar`.
- «Назад» — кнопка в строке заголовка, Alt+← и боковая кнопка мыши; временное (диалог, режим выделения) в историю не
  попадает, «Назад» его закрывает.
- [Keyboard interactions](https://learn.microsoft.com/windows/apps/develop/input/keyboard-interactions): Esc закрывает
  только временное и не ведёт назад по страницам.

У нас: `NavigationView` слева (Тренды, Новинки, Библиотека и Настройки; `PaneDisplayMode=Auto` — в узком окне
сворачивается) со своим стеком на раздел, `TitleBar` с «Назад», Alt+←, боковая кнопка мыши. Разделов меньше пяти, и
руководство допускает здесь верхнюю навигацию; левая оставлена, как у музыкальных клиентов Windows, — не расхождение. Esc ещё и «Назад» по страницам — по `docs/PROMPT.md`; это расходится с руководством и
записано исключением (см. `EXCEPTIONS.md`).

## Значки

[Segoe Fluent Icons](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font): шрифт значков —
Segoe Fluent Icons (`SymbolThemeFontFamily`), `SymbolIcon` или `FontIcon`; размеры, в которых значки чёткие: 16, 20, 24,
32, 40, 48, 64.

У нас: только Segoe Fluent Icons через `FontIcon`. Расхождение: в кнопках шапок значки 14 (аудит, задание 0027).

## Движение

[Motion](https://learn.microsoft.com/windows/apps/design/signature-experiences/motion): движение быстрое и по делу —
появление 167/250/333 мс с замедлением к концу, уход 167 мс с исчезновением, простое появление 83 мс; готовые переходы
страниц, связанные анимации и `AnimatedIcon` лучше своих. Настройка Windows «Эффекты анимации» выключает движение.

У нас: переходы страниц — системные; своё движение — открытие «Сейчас играет» и прокрутка текста песни (проверяют
`UISettings.AnimationsEnabled`), столбики «играет» (`PlayingBars` — при выключенных эффектах стоят).

## Доступность

[Accessible text](https://learn.microsoft.com/windows/apps/design/accessibility/accessible-text-requirements),
[Targeting](https://learn.microsoft.com/windows/apps/develop/input/guidelines-for-targeting) (обновлено 2026-09-27):

- Контраст 4,5 : 1; статичный текст — `TextBlock`, не поле ввода; текст на картинке — подписью
  `AutomationProperties.Name`.
- «Увеличение текста» Windows — до 225 %: `IsTextScaleFactorEnabled` не выключать; разметка не должна обрезать и
  наезжать.
- Зона нажатия — около 7,5 мм (40×40 epx при 100 %); чаще нажимаемое и опасное — крупнее и дальше от края.
- У всего, что нажимается, — имя для экранного диктора; заголовки — `AutomationProperties.HeadingLevel`.

У нас: подписи — через `x:Uid` и `AutomationProperties.Name`, у кнопок-значков ещё подсказка; заголовки страниц и полок
размечены уровнями; масштаб текста нигде не выключен. Не проверено снимками: 225 % и зоны нажатия строк (задания 0026, 0027).

## Клавиатура, мышь, жесты

[Keyboard interactions](https://learn.microsoft.com/windows/apps/develop/input/keyboard-interactions):

- Tab — по нажимаемому в порядке чтения; стрелки — внутри списка, сетки, меню; Enter — действие строки, пробел —
  выбор; Home/End, PageUp/PageDown — по списку; F6 — между областями окна.
- Сетка при вертикальной прокрутке — по строкам; порядок не должен спорить с направлением прокрутки.
- Сочетания: Ctrl+F — поиск, Ctrl+A — выделить всё, Shift+стрелки — выделение подряд, Ctrl+Z — отменить; сочетания
  видны в меню и подсказках, не в контекстном меню.
- Правый щелчок, Shift+F10 и клавиша меню — одно и то же контекстное меню.

У нас: все сочетания — в окне «Сочетания клавиш» (F1, Ctrl+/); Ctrl+F и «/» — поиск, Esc в поле поиска снимает с него
фокус, пробел — пауза вне полей ввода, Ctrl+A — все треки списка, Delete — убрать (где есть в меню), двойной щелчок и
Enter — играть, боковая кнопка мыши — назад. Нет F6 между областями окна (навигация, страница, панель
воспроизведения, очередь) — задание 0028.
