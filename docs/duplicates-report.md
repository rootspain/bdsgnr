# Дублирование структур в Blast Designer — отчёт

Дата: 2026-11-13. Анализ только чтением, исходники не менялись. Номера строк — по рабочему дереву на момент анализа
(в нём есть незакоммиченные правки в `Viewport.cs`, `StandardTools.cs`, `Overlays.cs`, `MainWindow.cs` и др., поэтому строки могут сдвинуться).

## Обзор

| Проект | .cs | .axaml | строк C# |
|---|---:|---:|---:|
| BlastDesigner.App | 70 | 1 (`App.axaml`) | 26 605 |
| BlastDesigner.Calc | 13 | — | 3 325 |
| BlastDesigner.Domain | 19 | — | 3 892 |
| BlastDesigner.Engine | 26 | — | 7 057 |
| BlastDesigner.IO | 15 | — | 2 782 |
| BlastDesigner.Recognition | 1 | — | 101 |
| BlastDesigner.Rendering | 8 | — | 4 162 |
| **Итого** | **152** | **1** | **≈ 47 900** |

UI написан на Avalonia почти целиком в коде (XAML — только `App.axaml` со стилями), поэтому «дубли XAML» здесь — это
дубли фабрик контролов в C#.

Что уже сделано хорошо: риббон (`BuildStages`) не дублирует команды, а берёт их из классического меню по пути
(`M("…", "File/Open…")`), командная палитра тоже ходит по `CommandIndex`. Геометрия в основном собрана в `GeoMath`,
расчёты заряда — в `Charging`/`CalcService`. Записи каталога объединены интерфейсом `ICatalogueRecord`. Ниже — то, что
осталось размноженным.

### Топ-5 рекомендаций

1. **Единый реестр команд с id** (меню, контекстные меню, риббон, рейка, тулбары, горячие клавиши). Сейчас контекстное
   меню объектов, меню инструментов, легаси-тулбар, рейка и `OnKeyDown` объявляют те же команды заново — с расхождениями
   подписей, условий доступности и **конфликтами клавиш** (Ctrl+E, Ctrl+P, T). → находки 1, 2, 3.
2. **Реестр флагов вида (`ViewState`)**: один и тот же флаг показа (сетка, связи, заряды, «только устья») переключается
   из 6–7 мест с тремя разными подписями. → находка 4.
3. **Один класс настроек линии времени**: 9 полей `TimingLine*` копируются руками в 5 местах, одно место уже разошлось
   (не копирует `Front`). → находка 5.
4. **Довести `FormKit` до единственного источника** форм: `Footer/Section/Header/Labeled/Fields/Number`, кнопка «Отмена»,
   `N()/Parse()/L()`, кисти `Warn/Ok` повторены в 8–10 окнах, и уже визуально разъехались. → находки 7, 8, 9.
5. **Спецификации стандартных слоёв и мелкие общие хелперы**: (имя, цвет, группа) слоя повторены ~30 раз; «выделенные
   или все скважины» — 7 раз; расстояние точка-отрезок на экране — 5 раз; азимут через `Atan2` — 7 раз. → находки 6, 11, 12, 13.

---

## Находки (по убыванию ценности/риска)

### 1. Горячие клавиши объявлены в пяти местах — уже есть конфликты

**Где:**
- Жесты в главном меню: `BlastDesigner.App/ViewModels/MainViewModel.Menus.cs:55-120` (`"Ctrl+N"`, `"Ctrl+E"`, `"Ctrl+P"`, `"Q"`, …),
  собираются в `_shortcuts` в `BlastDesigner.App/Views/MainWindow.cs:621` (`CollectShortcuts`).
- Жёсткий `switch` по клавишам: `BlastDesigner.App/Views/MainWindow.cs:637-735` (`OnKeyDown`: Ctrl+K/G/T/E/M/F/L/U, Ctrl+Shift+V/Q/L/S/X/D/O/Y/H/R, одиночные V/H/Q/Z/O/N/M/W/S/G/D/R/P/L/T/I/E/X/F7/F3).
- Подписи клавиш на кнопках риббона: `BlastDesigner.App/ViewModels/MainViewModel.Shell.cs:85-180` (`"Ctrl N"`, `"D"`, `"Ctrl T"`, `"Ctrl⇧Q"`, `"Esc"`…).
- Клавиши рейки: `BlastDesigner.App/ViewModels/MainViewModel.Shell.cs:186-201` (`BuildRail`: V, Q, H, Z, Home, ⇧Z, O, N, M, W, S, G, F7).
- Клавиши быстрых фильтров: `BlastDesigner.App/ViewModels/MainViewModel.Shell.cs:231-236` (`QuickFilters`: `"Ctrl+E"`, `"Ctrl+U"`).

**Насколько совпадает / расхождения (это уже баги или их предвестники):**
- **Ctrl+E**: в меню — «Edit selected…» (`Menus.cs:104`); в `OnKeyDown` — `FindHole("empty")` (`MainWindow.cs:661`), и
  `switch` проверяется раньше `_shortcuts`, так что пункт меню по клавише недостижим; быстрый фильтр «Empty» подписан
  `Ctrl+E` (`Shell.cs:234`), но делает другое — выделяет все пустые (`SelectHoles`), а не ищет одну.
- **Ctrl+P**: в меню — «Reports…» (`Menus.cs:82`), на риббоне подпись `Ctrl P` стоит у «Print plan» (`Shell.cs:90` и `:173`).
- **Ctrl+U**: предикат «не запрограммирована» продублирован: `Shell.cs:235` (по `ShownHoles(p)`) и `MainWindow.cs:665`
  (по `Project.Holes`) — фильтр и клавиша считают разные множества, когда часть скважин скрыта.
- **T**: клавиша создаёт `new TieTool(_vm)` (`MainWindow.cs:704`), а кнопка риббона «Ties» (через `Tb("…","Tie")` →
  тулбар `TieToolWithPalette`, `Menus.cs:360`) — с палитрой. Одна «команда», два поведения.
- Рейка и одиночные клавиши: действия `SetTool(new PanTool(..))`, `new ZoomBoxTool`, `new RotateTool(.., false/true)`,
  `ToggleSnap`, `ToggleView(ShowGrid)` написаны лямбдами и в `BuildRail` (`Shell.cs:188-200`), и в `OnKeyDown` (`MainWindow.cs:689-699`),
  и в легаси-тулбаре (`Menus.cs:337-343`). Подписи клавиш на рейке — просто строки, не связанные с обработчиком.
- `Ctrl+Shift+…` в `OnKeyDown` уже вызывают команды по пути (`Cmd("Calculations/Visualize timing")`) — правильный
  подход, но пути и клавиши риббона (`"Ctrl⇧V"` в `Shell.cs:152/157`) заданы независимо.

**Предложение:** завести одну таблицу сочетаний, привязанную к командам (см. п. 2/3):
`record Shortcut(string Gesture, string CommandId, Func<bool>? When = null)` в `ViewModels/MainViewModel.Shortcuts.cs`.
`OnKeyDown` превращается в поиск по этой таблице (+ контекстные условия вроде «активен TieTool»); подписи на риббоне,
рейке, в меню и в подсказках фильтров берутся из неё же (`ShortcutFor(commandId)`). UI-смоук-тест (он уже проверяет
`MissingCommands`) может заодно проверять уникальность жестов.

---

### 2. Контекстные меню повторяют пункты главного меню заново

**Где:**
- Контекстное меню объектов: `BlastDesigner.App/ViewModels/MainViewModel.Shell.cs:278-356` (`ObjectContextMenu`, ~65 пунктов).
- Главное меню: `BlastDesigner.App/ViewModels/MainViewModel.Menus.cs:52-300` и дополнения `MainViewModel.Version6.cs:17-105`.
- Меню вида: `BlastDesigner.App/ViewModels/MainViewModel.View.cs:150-161` (`ViewContextMenu`: Zoom all/in/out, Undo zoom, Plan view, 3D view…).
- Меню инструментов: `BlastDesigner.App/Tools/StandardTools.cs:476-483` (TieTool), `:521-529` (TimeSetTool),
  `Tools/RowDrawTool.cs:102-109`, `ViewModels/MainViewModel.Tools.cs:183, 206`.

**Насколько совпадает:** почти идентично — тот же заголовок и тот же обработчик, например
`Item("Fill selected polygon…", Run(FillSelectedPolygon), …)`, `Item("Volume…", Run(VolumeTool))`,
`Item("Create points at collar", () => CreatePointsAt("collar"), …)`, весь блок «Loading / Electronic timing / Rows / Tracks / Pattern grid»
для скважин, «Simplify/Expand/Contours» для поверхностей и т. д. Отличия:
- условие доступности: в главном меню `P`/`S`/`W` (`HasProject`/`HasSelection`/не только чтение), в контекстном — свой
  локальный `W()` (`Shell.cs:281`), который не проверяет `HasProject`;
- **дрейф подписей** (ломает ключи перевода `Loc.T` и поиск в палитре):
  «Transform coordinates..» (`Shell.cs:354`) vs «Transform coordinates…» (`Menus.cs:254`);
  «Connect multiple holes» (`StandardTools.cs:480`) vs «Connect selected holes in order» (`Menus.cs:282`);
  «Preferences..» (`StandardTools.cs:523`) vs «Preferences…» (`Menus.cs:202`);
  «Initiation catalogue» (TieTool, риббон) vs «Show palette» (меню);
  «Set row properties..», «Hole properties..», «Set pattern properties..» — все три вызывают один `EditDesignData`, а в меню это «Design data…»;
- в `ViewContextMenu` и `ObjectContextMenu` «Zoom all»/«View everything», «Undo zoom»/«Previous view» — одна команда под разными именами.

**Предложение:** собирать контекстные меню так же, как риббон: по пути команды из `CommandIndex`.
Добавить в `MainViewModel.Shell.cs` хелпер `MenuEntry C(string path, Func<bool>? enabled = null, string? header = null)`,
который берёт `MenuEntry` из `CommandIndex` (с опциональной заменой условия и подписи) и пишет промахи в `MissingCommands`.
Тогда `ObjectContextMenu`, `ViewContextMenu` и меню инструментов перечисляют только пути
(`C("Tools/Holes/Fill selected polygon…")`), а подписи и обработчики живут в одном месте (`Menus.cs`/`Version6.cs`).

---

### 3. Легаси-тулбар, рейка и список инструментов рисования описывают одни и те же инструменты

**Где:**
- `BlastDesigner.App/ViewModels/MainViewModel.Menus.cs:326-376` (`BuildToolbars`: 7 групп, глиф, подсказка, действие, имя инструмента).
- `BlastDesigner.App/ViewModels/MainViewModel.Shell.cs:186-201` (`BuildRail`).
- `BlastDesigner.App/ViewModels/MainViewModel.Shell.cs:204-216` (`DrawingToolList` — 10 инструментов рисования).
- `BlastDesigner.App/Views/MainWindow.cs:689-707` (запуск тех же инструментов по клавишам).

**Насколько совпадает:** группа «Drawing tools» тулбара (`Menus.cs:369-375`) и `DrawingToolList` — **те же 10 инструментов**
с теми же лямбдами (`DrawPointTool(PointKind.General)`, `DrawPolylineTool(false)`, `DrawSectionLineTool`, … `DrawPointTool(PointKind.NorthPoint)`),
отличаются только иконкой (эмодзи vs имя иконки). Группа «View tools» = рейка (Select/Zoom/Pan/Rotate/North/Zoom all).
Группа «Tools» (New/Open/Save/Undo/Redo/Cut/Copy/Paste/Delete/Reports/Plan) повторяет пункты меню.
Сам тулбар нигде не рисуется: `BuildToolbars` используется только как `ToolbarIndex` для `Tb(...)` в риббоне
(`Shell.cs:54-55, 65-69`), глифы — мёртвые данные. Индекс строится по подписи (`GroupBy(b => b.Tip)`), и «Polyline»
в тулбаре встречается дважды (Baseline и Polyline, `Menus.cs:353, 372`) — берётся первый.

**Предложение:** заменить `BuildToolbars` реестром инструментов
`record ToolCommand(string Id, string Header, string Icon, Action Start, string? Key)` (в `MainViewModel.Tools.cs` или новом
`MainViewModel.ToolRegistry.cs`). Из него строятся: `Tb(...)` риббона (по `Id`, а не по подсказке), рейка, `DrawingToolList`,
одиночные клавиши из п. 1. Пункты «New/Open/Save/…» из тулбара просто удалить — они уже есть в меню.

---

### 4. Флаги отображения `ViewState` переключаются из 6–7 мест под разными именами

**Где (пример — сетка, связи, заряды, «только устья»):**
- Главное меню View: `MainViewModel.Menus.cs:123, 128, 144, 150-160`.
- Контекстное меню пустого места: `MainViewModel.Shell.cs:295-296`.
- Инспектор (вкладка видимости): `BlastDesigner.App/Views/Shell.cs:826-846` (`Check("Ties", v => v.ShowTies, …)`).
- Чипы над планом: `BlastDesigner.App/Views/MainWindow.cs:566-569`.
- Рейка и клавиша G: `MainViewModel.Shell.cs:199`, `MainWindow.cs:699`.
- Таблица подписей флагов для статистики: `BlastDesigner.App/Views/UsageWindow.cs:176-185` (`FlagLabels`).
- Подписи меток скважин: `BlastDesigner.App/Views/Shell.cs:895-912` (`LabelInfo`) и снова `UsageWindow.cs:183-185` (`"HoleLabels.*"`).

**Насколько совпадает:** логика одинаковая (`ToggleView(v => v.X = !v.X)`), а подписи и даже смысл разъехались:
- `CollarsOnly`: «Collars only» (меню, галочка = включено) / «Hole track» (инспектор, инвертировано) / «Depths» (чип, инвертировано);
- `ShowLoading`: «Loading» (меню) / «Charge track» (инспектор, Usage) / «Charge 3D» (чип) / «Charge in 3D» (риббон);
- `ShowText`: «Text» (меню) / «Labels» (Usage);
- `ShowGrid`: «Grid» / «Coordinate grid» (рейка).
Названия меток скважин («Hole number», «Explosive mass», …) записаны в `LabelInfo` и ещё раз в `FlagLabels`.

**Предложение:** один реестр в `MainViewModel.View.cs` (или в `Domain/Model` рядом с `ViewState`, если нужен и рендеру):
`record ViewFlag(string Id, string Caption, string? Tip, Func<ViewState,bool> Get, Action<ViewState,bool> Set, bool Inverted = false)`
и `IReadOnlyList<ViewFlag> ViewFlags`. Меню, контекстное меню, инспектор, чипы, рейка, клавиши и `UsageWindow.FlagName`
берут флаг по `Id`. Для меток — сделать `LabelInfo` публичным и использовать его в `UsageWindow` вместо `"HoleLabels.*"`.

---

### 5. Параметры линии времени копируются поле-в-поле в пяти местах (одно уже разошлось)

**Где:**
- `BlastDesigner.Domain/Model/TimingLine.cs:93-111` — `TimingLineEntity` (Style, StartOffsetMs, UseProgressive, ProgressRate, ReverseOrder, BurdenReliefMsPerM, Angle1Deg, Angle2Deg, Front).
- `BlastDesigner.Engine/EbsTimingLine.cs:31-50` — `TimingLineOptions`: те же 9 свойств с теми же значениями по умолчанию + `KeepManualTimes`.
- Копирование: `EbsTimingLine.cs:215-228` (`CreateEntity`, options → entity), `EbsTimingLine.cs:247-258` (`OptionsOf`, entity → options),
  `BlastDesigner.App/Views/TimingLineWindow.cs:36-41` (клон options), `BlastDesigner.App/ViewModels/MainViewModel.Tools.cs:914-921`
  (options → существующая entity), `TimingLine.cs:123-131` (`WithPoints`).

**Насколько совпадает:** набор свойств идентичен. Расхождения:
- `MainViewModel.Tools.cs:919-920` **не копирует `Front`** — при правке линии форма фронта из окна не попадает в проект;
- `KeepManualTimes` есть только в options и в проекте не хранится — при повторной правке берётся значение по умолчанию.
Каждое новое поле нужно не забыть в 5 местах.

**Предложение:** вынести 9 полей в класс `TimingLineSettings` в `Domain/Model/TimingLine.cs` (с `Clone()`), хранить его
в `TimingLineEntity.Settings` (миграция JSON: старые плоские поля читаются через `[JsonExtensionData]` или
свойства-прокси с `[JsonIgnore]` на запись), а `TimingLineOptions` наследует/содержит его плюс `KeepManualTimes`.
Пять мест копирования сводятся к `settings.Clone()` / `entity.Settings = o.Settings.Clone()`.

---

### 6. Стандартные слои: (имя, цвет, группа) повторены ~30 раз

**Где:** вызовы `GetOrCreateLayer(StandardLayers.X, 0xFF…, "…")`:
- Holes `0xFF1E3A8A, "Design"`: `Domain/Model/BlastProject.cs:94`, `App/ViewModels/MainViewModel.cs:345` (`HoleLayer`),
  `MainViewModel.File.cs:349`, `MainViewModel.Patterns.cs:31`, `Engine/HoleDesign.Patterns.cs:331`;
- Ties `0xFFDC2626, "Design"`: `BlastProject.cs:95`, `Engine/Timing.cs:155`, `MainViewModel.Tools.cs:685`;
- Boundary `0xFF16A34A`: `BlastProject.cs:96`, `MainViewModel.Tools.cs:83, 272, 1300`;
- Survey `0xFF7C3AED, Topography`: `BlastProject.cs:97`, `MainViewModel.Tools.cs:111, 332, 1365`, `Views/SurveyRecognitionWindow.cs:53`;
- Surfaces `0xFF64748B, Topography`: `BlastProject.cs:98`, `MainViewModel.Tools.cs:1178, 1211, 1221` (`SurfLayer`), `SurveyRecognitionWindow.cs:434`;
- Analysis `0xFFDC2626, "Analysis"`: `Views/AnalysisWindowsNew.cs:342, 509, 551`;
- TimingLines `0xFF008000`: `MainViewModel.Tools.cs:782, 829`.

**Насколько совпадает:** идентично, но хелперы `HoleLayer`/`SurfLayer` есть и используются не везде; `StandardLayers`
(`BlastProject.cs:124-136`) хранит только имена. Если поменять цвет/группу слоя в `CreateNew`, то слой, создаваемый
позже по требованию, получит старый цвет.

**Предложение:** в `StandardLayers` добавить спецификации
`public static readonly LayerSpec Holes = new("Holes", 0xFF1E3A8A, "Design")` и метод
`BlastProject.StandardLayer(LayerSpec s) => GetOrCreateLayer(s.Name, s.Color, s.Group)`. `CreateNew`, Engine и App
вызывают только его; `HoleLayer`/`SurfLayer` становятся однострочниками или исчезают.

---

### 7. `FormKit` существует, но окна держат свои копии его методов

**Где:** `BlastDesigner.App/Views/FormKit.cs` (Header, Footer, Primary, Secondary, Tool, Section, Labeled, Fields, Text) и локальные версии:
- `Footer()` — 9 файлов: `CadImportWindow.cs:184`, `HoleEditWindow.cs:163`, `HoleLoadingWindow.cs:665`, `NewBlastWindow.cs:185`,
  `ProductsWindow.cs:755`, `ReportPreviewWindow.cs:266`, `SettingsWindow.cs:536`, `TimingLineWindow.cs:250` (+ сам FormKit);
- `Section()` — `HoleEditWindow.cs:206`, `FragmentationWindow.cs:413`, `ProductsWindow.cs:414`, `TimingLineWindow.cs:276`, `PropertyGrid.cs`;
- `Labeled()` — `CalcEditorWindow.cs`, `CatalogueEditor.cs`, `FragmentationWindow.cs:443`, `HoleEditWindow.cs:233`, `ProductsWindow.cs:422`, `TimingLineWindow.cs:284`;
- `Fields()` — `HoleEditWindow.cs:240`, `FragmentationWindow.cs:446`, `ProductsWindow.cs:428`;
- `Header()` — `HoleEditWindow.cs:128`, `FragmentationWindow.cs:307`, `HoleLoadingWindow.cs:538`, `TimingLineWindow.cs:204`, `StartupWindow.cs`;
- ручная кнопка «Cancel» (`new Button { Content = Loc.T("Cancel"), Height = 32, Padding = new Thickness(14, 0), CornerRadius = 6, BorderBrush = Look.LineStrong… }`) —
  `CadImportWindow.cs:188`, `HoleEditWindow.cs:167`, `NewBlastWindow.cs:189`, `ProductsWindow.cs:759`, `SaveRuleWindow.cs:113`,
  `SettingsWindow.cs:542`, `TimingLineWindow.cs:254`, `SurveyRecognitionWindow.cs:243`; плюс подтверждение удаления
  («Delete»/«Cancel», красная кнопка `0xFFE5484D`) — `CatalogueEditor.cs:335-336` и `ProductsWindow.cs:677-678` почти один в один.

**Насколько совпадает:** near-identical, но уже видны визуальные расхождения:
- заголовок секции: `FormKit` и `FragmentationWindow` — `Look.Blue`, `Spacing = 8`; `ProductsWindow`/`TimingLineWindow` — `Look.Faint`, `Spacing = 10`;
- `Fields`: `FormKit` — `ColumnSpacing = 12, RowSpacing = 10`; окна — колонки-распорки `"*,12,*"` / `"*,14,*"` и строки-распорки 10 px;
- `Labeled`: в окнах подпись проходит через `L()` (перевод внутри), в `FormKit` — уже переведённая строка.

**Предложение:** `FormKit` — единственный источник. Добавить в него `Cancel(Action)`, `ConfirmDelete(...)`,
`Section(title, params Control[])`, параметр `spacing`; окна удаляют локальные методы и вызывают `FormKit.*`
(перевод — на стороне вызывающего, как в `FormKit` сейчас). Локальными оставить только `Header()` с особым
содержимым (чипы), но строить их поверх `FormKit.Header(title, subtitle, aside)`.

---

### 8. Числовые поля и `N()/Parse()/L()` скопированы в 8–9 окнах

**Где:**
- `N(double, format)` — `FragmentationWindow.cs:300`, `HoleEditWindow.cs:111`, `HoleLoadingWindow.cs:524`, `OpeningPointWindow.cs:25`,
  `ProductsWindow.cs:155`, `SaveRuleWindow.cs:160`, `SettingsWindow.cs:125`, `TimingLineWindow.cs:170` (отличается только формат по умолчанию: `0.0##`, `0.0`, `0.##`, `0.###`);
- `Parse(string?)` — `FragmentationWindow.cs:301`, `HoleEditWindow.cs:112`, `HoleLoadingWindow.cs:1118`, `ProductsWindow.cs:156`,
  `SettingsWindow.cs:126`, `TimingLineWindow.cs:171`; вариации — `SaveRuleWindow.cs:200` (`Num(TextBox, fallback)`),
  `OpeningPointWindow.cs:67` (инлайн), `CalcEditorWindow.cs:376`, `MainViewModel.Shell.cs:267-268` (`EnterCoordinates`);
- `L(string) => Loc.Ui(english)` — 9 файлов (`CatalogueEditor`, `FragmentationWindow`, `HoleEditWindow`, `HoleLoadingWindow`,
  `OpeningPointWindow`, `ProductsWindow`, `SaveRuleWindow`, `SettingsWindow`, `TimingLineWindow`);
- `Number(...)` — поле с единицей внутри (`InnerRightContent`), commit по `LostFocus`/Enter: `FragmentationWindow.cs:470`,
  `HoleEditWindow.cs:275`, `ProductsWindow.cs:457`, `SettingsWindow.cs:211`, `TimingLineWindow.cs:294`; похожие `Field/TextField/PatternLevel`
  в `Views/Shell.cs:643-735` и `NumericUpDown`-вариант `AnalysisWindowsNew.cs:129`.

**Насколько совпадает:** `Parse` near-identical: часть версий удаляет пробелы (`.Replace(" ", "")` — Fragmentation, HoleEdit, Products),
часть нет (HoleLoading, Settings, TimingLine, SaveRule) — «1 234,5» в одном окне примется, в другом нет.
`Number` — один каркас (TextBox 32 px, моноширинный, единица справа, commit), разные надстройки (live-обновление, шаг колесом,
«смешанное» значение, проверка диапазона).

**Предложение:** `Services/NumberText.cs` со `Format(double, string fmt)` и `TryParse(string?, out double)` (единое правило:
trim, убрать пробелы и NBSP, запятая → точка, Invariant) — и все окна на него. В `FormKit` — `NumberField` с опциями
`unit, min, max, step, live, format`, построенный на существующем `Services/NumberStep.cs`. `L()` убрать, звать `Loc.Ui` напрямую
(или статический `using static ...Loc`).

---

### 9. Цвета: статусные кисти и палитры размножены, часть не следует теме

**Где:**
- `Warn = 0xFFF0C13A` — `CalcEditorWindow.cs:48`, `CatalogueEditor.cs:19`, `HoleEditWindow.cs:59`, `HoleLoadingWindow.cs:439`,
  `OpeningPointWindow.cs:27`, `ProductsWindow.cs:51`, `StartupWindow.cs:63`, `TimingLineWindow.cs:95`, `Panels.cs:488, 785`;
- `Ok = 0xFF5BC08A` — `CalcEditorWindow.cs:46`, `HoleLoadingWindow.cs:438`, `OpeningPointWindow.cs:26`, `TimingLineWindow.cs:94`;
- «опасный» красный `0xFFE5484D` — `FragmentationWindow.cs:185` (**там он называется `Warn`**), `HoleLoadingWindow.cs:151, 446`, кнопки удаления в `CatalogueEditor.cs:335`, `ProductsWindow.cs:677`;
- палитры серий/типов: `Domain/Model/BlastProject.cs:185-186` (`TypeDefinition.Palette`), `Rendering/Overlays.cs:536` (`Colours`),
  `Views/AnalysisWindows.cs:479`, `SettingsWindow.cs:57-62`, `TimingLineWindow.cs:96`;
- красный `0xFFDC2626` — 21 вхождение (слои, превью, легенды, рендер), синий превью `0xFF2563EB` — `Tools/Tool.cs:162`, `Overlays.cs:29, 787`, `Reports.cs:373`.

**Насколько совпадает:** одинаковые литералы; но `Look` перекрашивает свои кисти при смене темы (`Look.ApplyTheme`),
а локальные `Warn/Ok` — нет, поэтому в светлой теме они остаются «тёмными». Имя `Warn` в одном окне значит жёлтый, в другом — красный.

**Предложение:** добавить в `Services/Look.cs` `Warn`, `Ok`, `Danger` (со светлыми вариантами в `ApplyTheme`) и убрать локальные поля.
Для цветов модели/рендера — `Domain/Model/Palette.cs` (`Red = 0xFFDC2626`, `Series[]`, …), на который ссылаются `TypeDefinition`,
`Overlays.Colours`, спецификации слоёв из п. 6 и `ProgramSettings.Default*`.

---

### 10. `HoleParameters` ≡ `PatternHoleDefaults`, и значения по умолчанию проекта — в четырёх классах

**Где:**
- `BlastDesigner.Engine/HoleDesign.cs:7-29` — `HoleParameters` (Diameter=102, Angle, Bearing, BenchHeight=10, Subdrill=1, HoleType=1, Dummy, + CollarSurface/GradeSurface);
- `BlastDesigner.Domain/Model/BlastProject.cs:255-264` — `PatternHoleDefaults` (те же 7 свойств с теми же умолчаниями);
- `BlastDesigner.Domain/Model/BlastProject.cs:157-174` — `DesignData` (Burden=4, Spacing=4.5, BenchHeight=10, Subdrill=1, Diameter=102 …);
- `BlastDesigner.Engine/HoleDesign.cs:31-44` — `PatternParameters` (Burden=4, Spacing=4.5, Pattern=Staggered, EdgeOffset, LabelPrefix, StartNumber, RotateOrientation);
- `HolePattern` (`BlastProject.cs:214-249`) повторяет поля `PatternParameters`.
- Копирование туда-обратно: `Engine/HoleDesign.Patterns.cs:149-152` (HoleParameters → PatternHoleDefaults) и `:326-330` (обратно).

**Насколько совпадает:** `PatternHoleDefaults` = `HoleParameters` без двух поверхностей — идентичный набор и умолчания.
Числа 4 / 4.5 / 10 / 1 / 102 записаны в 4 классах.

**Предложение:** `HoleParameters` содержит `PatternHoleDefaults Hole { get; init; }` (или наследует общую базу
`HoleDefaults` из Domain), тогда `HoleDesign.Patterns` присваивает клон вместо поштучного копирования. Умолчания —
константы `DesignDefaults` в Domain (`Burden = 4, Spacing = 4.5, …`), на них ссылаются все четыре класса.
Аналогично `HolePattern` может хранить `PatternLayout` (Burden, Spacing, Type, EdgeOffset, LabelPrefix, StartNumber, RotateOrientation),
общий с `PatternParameters`.

---

### 11. «Выделенные скважины или все» — 7 копий

**Где:**
- `BlastDesigner.App/ViewModels/MainViewModel.File.cs:371` (`ExportHolesSet`);
- `BlastDesigner.App/ViewModels/MainViewModel.Tools.cs:12` (`SelectedOrAllHoles`);
- `BlastDesigner.App/Views/AnalysisWindows.cs:631` (`SelectedOrAll`);
- инлайн: `MainViewModel.Version6.cs:131, 195, 204` (в виде множества id), `AnalysisWindowsNew.cs:224, 545`, похожее `AnalysisWindows.cs:396`.

**Насколько совпадает:** идентично (различие — `IEnumerable`/`List`/`HashSet<Guid>`).

**Предложение:** оставить один `MainViewModel.SelectedOrAllHoles()` (+ `SelectedOrAllHoleIds()`), остальные — вызывать его;
`ExportHolesSet` и `AnalysisWindows.SelectedOrAll` удалить.

---

### 12. Расстояние «точка — отрезок» в экранных координатах — 5 копий

**Где:** `BlastDesigner.App/Controls/Viewport.cs:341-347` (`SegmentDistance`) и инлайн-повтор той же формулы в `Viewport.cs:421-424`;
`BlastDesigner.App/Tools/Select/RowPick.cs:80-82`; `BlastDesigner.App/Tools/Select/VertexGrips.cs:140-142`;
`BlastDesigner.App/Views/SurveyRecognitionWindow.cs:118-120`. Похожие проекции — `StandardTools.cs:399-409`.
В Domain уже есть `GeoMath.ClosestOnSegment2D(Vec3, Vec3, Vec3)` (`Domain/Geometry/GeoMath.cs:107`), но на `Vec3`.

**Насколько совпадает:** формула идентична (`t = clamp(((s-a)·d)/|d|², 0, 1)`), отличаются только порог вырожденного отрезка (`> 0` vs `< 1e-9`)
и `Math.Sqrt(cx*cx+cy*cy)` vs `Math.Pow(..., 2)`.

**Предложение:** `GeoMath.SegmentDistance2D(double ax, double ay, double bx, double by, double px, double py, out double t)` в Domain;
`Viewport.SegmentDistance` и инлайны заменить вызовом. `VertexGrips` использует `t` для `Vec3.Lerp` — `out t` это покрывает.

---

### 13. Азимут через `Atan2` пишется заново вместо `GeoMath.Bearing`

**Где:** `GeoMath.Bearing` / `NormalizeBearing` — `Domain/Geometry/GeoMath.cs:18-28`. Инлайн-копии:
`App/Tools/FrontLineTool.cs:328, 362` (`(Atan2(dx,dy)/Deg + 360) % 360`), `Domain/Model/Block.cs:40, 188` и `Engine/BlockAssembly.cs:93`
(`Atan2(x,y) * 180 / Math.PI` — **без нормализации, может быть отрицательным**), `Domain/Model/TimingLine.cs:79`,
`Engine/Timing.cs:465, 468` (`NormalizeBearing(Atan2(..)/Deg)`).

**Насколько совпадает:** та же формула; расхождение — блоки получают азимут в (−180, 180], остальное — в [0, 360).

**Предложение:** добавить `GeoMath.Bearing(double dx, double dy)` (вектор), использовать везде; для блоков решить,
нужна ли нормализация (если нет — явно назвать `SignedBearing`).

---

### 14. Видимость меток времени: одна и та же логика в двух местах; три пересекающихся enum

**Где:**
- `BlastDesigner.App/ViewModels/MainViewModel.View.cs:203-210` (`IsTimingLabelVisible(ViewState, TimingLabelMode)`);
- `BlastDesigner.Domain/Model/BlastProject.cs:510-517` (`HoleLabelLayout.IsShown`, ветка `Timing(l) switch`).
- Enum-ы: `HoleLabel` (`BlastProject.cs:431`), `HoleLabelMode` [Flags] (`:522`), `TimingLabelMode` (`:542`) и таблицы
  соответствия `HoleLabelLayout.Flag/Timing` (`:483-508`).

**Насколько совпадает:** `switch` идентичен построчно (`ShowSurfaceTimeLabel || TimingLabels == SurfaceTime` и т. д.).
`HoleLabel` перечисляет то же, что `HoleLabelMode` ∪ `TimingLabelMode`, имена разные (`Number`↔`Label`, `Elevation`↔`CollarZ`,
`ExplosiveMass`↔`Charge`, `HoleOrder`↔`HoleSequence`, `InholeDelay`↔`DelayNumber`, `HoleTime`↔`InholeTime`).

**Предложение:** вынести `public static bool IsTimingLabelVisible(ViewState, TimingLabelMode)` в `HoleLabelLayout` (Domain) и
вызывать его из `IsShown` и из `MainViewModel.View.cs`. Дальше (дороже, затрагивает формат файла) — оставить `HoleLabel` как
единственный публичный enum меток, а `HoleLabelMode`/`TimingLabelMode`/булевы `Show*Label` — как деталь хранения за `HoleLabelLayout`.

---

### 15. Два статических класса окон анализа: `AnalysisWindows` и `AnalisysWindows`

**Где:** `BlastDesigner.App/Views/AnalysisWindows.cs:68` (`AnalysisWindows`) и `BlastDesigner.App/Views/AnalysisWindowsNew.cs:115`
(`AnalisysWindows` — опечатка в имени). Вызовы из меню — `MainViewModel.Menus.cs` (первый) и `MainViewModel.Version6.cs:55-73` (второй).

**Насколько совпадает:** назначение одинаковое; у каждого свои хелперы: `Table` (`AnalysisWindows.cs:70`, и ещё один в `UsageWindow.cs`),
`WithButtons` vs `Stack/Inputs/Note` (`AnalysisWindowsNew.cs:117-137`), форматтеры `F` (`AnalysisWindowsNew.cs:141`) и `Chart.Fmt` (`:111`),
`SelectedOrAll` (п. 11).

**Предложение:** сделать один `public static partial class AnalysisWindows` в двух файлах (переименовать `AnalisysWindows`,
переименовать файл, например, в `AnalysisWindows.Prediction.cs`), общие хелперы — в одной части. `Chart` вынести в свой файл.

---

### 16. Файлы-«конверты» JSON с форматом и версией — два одинаковых класса

**Где:** `BlastDesigner.IO/FragmentationPreset.cs` и `BlastDesigner.IO/ReportDesignFile.cs`.

**Насколько совпадает:** near-identical: вложенный `class File { Format; Version = 1; <Payload> }`, `Save`, `Load` с проверкой `Format`
и одинаковым текстом исключения. Отличия: `FragmentationPreset.Save` пишет через `System.IO.File.WriteAllBytes`, а `ReportDesignFile` — через
`SafeFile.WriteAllText` (атомарная запись); отчёт ещё вызывает `Portable()`.
Рядом `SurveyLabels.cs` создаёт новые `JsonSerializerOptions` при каждом вызове вместо общих `ProjectJson.Options`.

**Предложение:** `IO/TaggedJsonFile.cs`: `static void Save<T>(string path, string format, T payload)` (через `SafeFile`) и
`static T Load<T>(string path, string format, string what)`. Оба класса становятся тонкими обёртками; в `SurveyLabels` — статическое поле опций.

---

### 17. CSV и форматирование чисел в IO

**Где:** экранирование CSV — `BlastDesigner.IO/TextFiles.cs:205` (`Csv(string)`) и инлайн в `BlastDesigner.IO/Interchange.cs:311`;
`F(double)` — `TextFiles.cs:204` и `Interchange.cs:16` (одинаковое `"0.###"`, Invariant); поле `CultureInfo.InvariantCulture` под
именами `Ci`/`Inv` — `CadFiles.cs:47`, `SurveyDxf.cs:19`, `DeviTronic.cs:87`, `Interchange.cs:15`.

**Насколько совпадает:** идентично.

**Предложение:** `IO/TextFormat.cs` (internal static): `Inv`, `F(double, string fmt = "0.###")`, `CsvField(string)`, `CsvLine(IEnumerable<string>)`;
`Interchange.Csv(header, rows)` и `TextFiles` используют его.

---

### 18. Коммит текстового поля по `LostFocus` / Enter

**Где:** `Views/Shell.cs:645-651` (`TextField`), `:686-690` (`PatternLevel`), `:715-733` (`Field`) — три копии в одном классе;
тот же приём в `HoleEditWindow`, `ProductsWindow`, `SettingsWindow`, `FragmentationWindow`, `TimingLineWindow`, `PropertyGrid` (4 раза).

**Насколько совпадает:** каркас идентичен (`box.LostFocus += Commit; box.KeyDown += Enter → Commit; e.Handled = true`).

**Предложение:** расширение `Ui.CommitOn(TextBox box, Action commit)` в `Views/Shell.cs` (класс `Ui`) — одна строка вместо четырёх
в каждом поле; заодно используется в `NumberField` из п. 8.

---

### 19. Мелкие дубли (низкий приоритет)

- Наконечник стрелки: `Rendering/PlanRenderer.cs:796-808` (`DrawArrow`) и `:1401-1404` (связи) — одна формула `head·cos(ang ± 0.4)`.
  → `static (SKPoint, SKPoint) ArrowHead(x1, y1, ang, head)` в `PlanRenderer`.
- `SurveyRecognitionWindow.KindOf` (`Views/SurveyRecognitionWindow.cs:360-367`) повторяет таблицу `KindLayers` (`:38-41`) — брать из таблицы.
- `HoleRow` и `HolePattern` хранят `Burden/Spacing` наряду с `DesignData` — это не дубль по смыслу (значения ряда/сетки), оставить.
- Записи каталога (`CatalogueType/Kind/Group/Family`, `Domain/Model/Catalogue.cs`) повторяют `Id/Name/Deleted/IsNew`, но уже за
  интерфейсом `ICatalogueRecord`; базовый класс сэкономил бы ~10 строк — по желанию.

---

## Порядок работ (предложение)

1. Быстрые и безопасные: п. 11, 12, 13, 14 (первая часть), 16, 17, 19 — чисто внутренние хелперы, без изменения поведения.
2. UI-кит: п. 7, 8, 9, 18 — по одному окну за раз, со скриншотной сверкой (визуальные расхождения из п. 7 надо решить явно).
3. Модели: п. 5, 6, 10 — затрагивают JSON проекта, нужна миграция и тест загрузки старых файлов.
4. Команды: п. 1–4 — самый ценный, но самый широкий рефакторинг; начать с реестра команд/инструментов и проверки в UI-смоук-тесте
   (`MissingCommands` + уникальность жестов), потом перевести контекстные меню, рейку и `OnKeyDown`.
