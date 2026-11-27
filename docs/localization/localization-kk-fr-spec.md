# Казахский и французский интерфейс: техническое задание

Решения владельца (25.11.2026):
1. Фоллбек казахского: kk → ru → en. Французского: fr → en.
2. Формульный язык и имена параметров расчёта: на языке интерфейса (ru, kk, fr), у каждого свой, с переключением на английский (английский/кодовые имена — база).
3. Французский: европейский вариант (tir, boutefeu, plan de tir; не квебекский dynamitage/sautage).
4. Вычитка: сбор отзывов пользователей. Нужен механизм пользовательских правок перевода.
5. Git-коммиты НЕ делать. Изменения остаются в рабочей папке.

## 1. Механизм языков (BlastDesigner.App/Services)
- Loc.Ru.cs → таблицы по коду языка: ru.tsv, kk.tsv, fr.tsv (EmbeddedResource в BlastDesigner.App.csproj).
- Loc.Raw: цепочка поиска по языку: kk → kk, ru; fr → fr; ru → ru; en → ключ. Нет перевода → английский ключ.
- Префиксы ui:/name:/short: работают как сейчас, по той же цепочке (ui:X в kk → ui:X в ru → X).
- Loc.Languages: en English, ru Русский, kk Қазақша, fr Français.
- HasTranslation(english, language = текущий).
- Пользовательские правки: после встроенной таблицы загружать файл <папка данных программы>/Translations/<код>.user.tsv (тот же формат, строки перекрывают встроенные). Путь взять из того же места, где ProgramSettings хранит настройки. Пункт в настройках рядом с языком: «Open translation corrections folder» (кнопка открывает папку). Это канал для отзывов: пользователь правит строку, присылает файл, исправление переносится в kk.tsv/fr.tsv.
- По желанию: CurrentUICulture = kk-KZ / fr-FR при выборе языка. CurrentCulture (формат чисел) не трогать.

## 2. Места с жёстким русским
- MainViewModel.cs:120,162 `CalcService.RussianNames = Loc.Language == "ru"` → язык формул (см. п.3).
- IO/Changelog.cs Load: ru и kk → русский журнал, иначе английский.
- Domain/Model/StageViews.cs:187-190 стандартные имена слоёв на русском → английские ключи через Loc.Name / name: (с сохранением совместимости с уже сохранёнными проектами, где имена русские: при чтении сопоставлять старые русские имена).
- WhatsNewWindow.cs:123 — распознавание секций; добавить kk/fr варианты не нужно, если журнал kk=ru, fr=en.

## 3. Формулы и имена расчёта на языке интерфейса
Сейчас: Calc/Aliases.cs — русские псевдонимы функций, слов, единиц; FormulaText.ToRussian/ToCode (bool russian); CalcService.RussianNames; профиль .bdcalc хранит nameRu/nameEn; CalcEditorWindow имеет переключатель _codeNames.
Нужно:
- Хранение формул остаётся в кодовой записи (ToCode) — язык-нейтрально. Проверить, что старые проекты/профили с русской записью продолжают читаться.
- Aliases: таблицы по языку (ru, kk, fr) для функций, ключевых слов, констант и символов единиц (kk — те же кириллические единицы, что ru: м, кг, мс; fr — латиница m, kg, ms). Парсер принимает кодовые имена и псевдонимы ЛЮБОГО языка (чтобы формулу, набранную по-русски, понимал и французский интерфейс). Неоднозначности между языками — проверить тестом.
- Имена свойств/параметров/контекстов: к nameRu/nameEn добавить nameKk/nameFr (в .bdcalc, PropertyDef, ParameterDef, BuiltinDef; JSON-поля опциональны). Отображаемое имя: язык формул → nameXx, иначе фоллбек (kk → ru → en; fr → en). Группы ("group": "Заряжание") тоже локализовать (через Loc.T английского ключа группы или поле groupEn/…).
- CalcService.RussianNames (bool) → CalcService.FormulaLanguage (string: "en" | "ru" | "kk" | "fr").
- FormulaText.ToRussian → ToLanguage(model, context, text, language); ToCode без изменений.
- Переключатель «на английский»: в редакторе расчётов уже есть _codeNames — сделать его «English / code names» против языка интерфейса; плюс настройка программы «Formulas in English» (ProgramSettings), по умолчанию выключена.
- Сообщения Aliases.RussianUnit → Unit(symbol, language).
- Переводы псевдонимов (функции):
  ru (как сейчас). kk: sum=қосынды, avg=орташа, min=мин, max=макс, count=саны, first=бірінші, if=егер, case=таңдау, coalesce=біріншісі, exists=бар, clamp=шектеу, round=дөңгелектеу, floor=төмен, ceil=жоғары, abs=модуль, sqrt=түбір, cbrt=түбір3, pi=пи, param=параметр, lookup=кесте, to=қа; слова: and=және, or=немесе, not=емес, where=мұнда, true=ақиқат, false=жалған, null=бос, inf=шексіздік.
  fr: sum=somme, avg=moyenne, min=min, max=max, count=nombre, first=premier, if=si, case=selon, coalesce=premier_de, exists=existe, clamp=borner, round=arrondi, floor=plancher, ceil=plafond, abs=abs, sqrt=racine, cbrt=racine3, pi=pi, param=paramètre, lookup=table, to=en; слова: and=et, or=ou, not=non, where=où, true=vrai, false=faux, null=nul, inf=infini.
  Специальные функции (cellArea, patternBurden, patternSpacing, mic, firingTime, distToLine, nearestChargeDistance, predictPpv, studentT) — kk/fr имена с подчёркиванием по аналогии с ru, термины по глоссариям. Внимание: "to"="в" в ru; для kk суффикс неудобен — допустимо оставить "to" в kk, если "қа" конфликтует.
- Тесты: по образцу CalcRussianTests — круговой перевод code → kk → code и code → fr → code для всех формул Formulas.bdcalc; парсинг русской записи при FormulaLanguage = fr.

## 4. Переводы kk.tsv и fr.tsv
- Источник ключей: ru.tsv (3 928 уникальных ключей; дубликаты — берётся последняя строка). Перевести ВСЕ ключи. Формат строки: `EN<TAB>перевод`. Заголовок-комментарий как в ru.tsv.
- kk: переводить с русского значения, сверяясь с английским ключом. Термины строго по docs/localization/glossary_kk.tsv, далее по docs/localization/ru_kk_dictionary.tsv (русско-казахский словарь «Горное дело и металлургия», 11 456 статей) и dictionary_terms_found_in_ui.tsv. Из нескольких синонимов словаря выбрать один и держать единообразно. Сокращения: ЖЗ (ВВ), ДП (ДШ), ЕҚКС (ЛНС) — допустимы в коротких ui:-подписях.
- fr: переводить с английского ключа (европейский французский). Термины строго по docs/localization/glossary_fr.tsv (banquette = burden; площадка = gradin/berme).
- Сохранять плейсхолдеры {0}, {1:0.0}, единицы в конце подписи (", m", ", кг/м³" — MeasureText.Caption их распознаёт; для fr использовать латинские символы как в английском ключе), символы ▸ … · и т.п.
- ui:-ключи: коротко, по возможности не длиннее русского значения.
- Тест: для каждого языка все плейсхолдеры совпадают с ключом, нет пустых значений; предупреждение, если ui:-перевод длиннее EN в 1,3 раза.

## 5. Проверка
- dotnet build, dotnet test (BlastDesigner.Tests). UiSmoke при возможности на kk и fr.
- Проверить, что шрифт Inter показывает ӘәҒғҚқҢңӨөҰұҮүҺһІі (в т.ч. SemiBold).
