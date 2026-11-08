using System.Text.Json.Serialization;

namespace BlastDesigner.Calc;

/// <summary>Тип значения свойства. Enum — перечисление модели (вид продукта), в формулах сравнивается со строкой.</summary>
public enum CalcType { Number, Bool, String, Enum, Ref, List }

/// <summary>Вид свойства: поле модели, вычисляемое по формуле, или ручное значение поверх формулы.</summary>
public enum PropertyKind { Input, Computed, Override }

/// <summary>
/// Свойство контекста: поле модели (input, регистрируется автоматически), вычисляемое по формуле профиля (computed)
/// или связь (тип Ref или List с <see cref="Target"/>).
/// </summary>
public sealed class PropertyDef
{
    /// <summary>Полное имя: <c>hole.chargeMass</c>.</summary>
    public string Id { get; set; } = "";
    [JsonIgnore] public string Context => Id[..Id.IndexOf('.')];
    [JsonIgnore] public string Name => Id[(Id.IndexOf('.') + 1)..];
    public string NameRu { get; set; } = "";
    public string NameEn { get; set; } = "";
    public CalcType Type { get; set; } = CalcType.Number;
    /// <summary>Единица (<see cref="Calc.Unit.Parse"/>): "kg", "m3", "?" — без проверки.</summary>
    public string Unit { get; set; } = "?";
    public PropertyKind Kind { get; set; } = PropertyKind.Computed;
    public string Group { get; set; } = "";
    /// <summary>Формат вывода .NET ("0.00").</summary>
    public string Format { get; set; } = "";
    public double? Min { get; set; }
    public double? Max { get; set; }
    /// <summary>Встроенное (из модели или встроенного профиля); иначе добавлено пользователем.</summary>
    public bool BuiltIn { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Контекст, куда ведёт связь (для Ref и List).</summary>
    public string? Target { get; set; }
    /// <summary>Имя обратной стороны связи (<c>product.decks</c> для <c>deck.product</c>), для справки и графа.</summary>
    public string? Inverse { get; set; }

    /// <summary>Чтение поля модели: Func&lt;CalcScope, object, T&gt; с T по <see cref="Type"/> (double, bool, string, int, object, IReadOnlyList&lt;object&gt;).</summary>
    [JsonIgnore] public Delegate? Getter { get; set; }
    /// <summary>Перечисление для <see cref="CalcType.Enum"/>.</summary>
    [JsonIgnore] public Type? EnumType { get; set; }
    /// <summary>Поля модели, от которых зависит значение (для связи <c>deck.product</c> — <c>ProductId</c>).</summary>
    [JsonIgnore] public string[] Fields { get; set; } = Array.Empty<string>();

    [JsonIgnore] public bool IsRelation => Type is CalcType.Ref or CalcType.List;

    /// <summary>Значение вне разумного диапазона свойства (<see cref="Min"/>…<see cref="Max"/>): считается, но с предупреждением.</summary>
    public bool OutOfRange(double value) => !double.IsNaN(value) && ((Min is { } a && value < a) || (Max is { } b && value > b));

    public PropertyDef Clone() => (PropertyDef)MemberwiseClone();
    public override string ToString() => Id;
}

/// <summary>Формула свойства. Несколько формул одного свойства — варианты: берётся первая по приоритету с выполненным условием.</summary>
public sealed class FormulaDef
{
    public string Property { get; set; } = "";
    public string Expression { get; set; } = "";
    /// <summary>Условие применения (логическое выражение в контексте свойства); пусто — всегда.</summary>
    public string? Condition { get; set; }
    /// <summary>Меньше — раньше.</summary>
    public int Priority { get; set; }
    /// <summary>Значение, когда формула не вычислилась (нет продукта, деление на ноль); null — ошибка.</summary>
    public double? OnError { get; set; }
    public string Comment { get; set; } = "";
    /// <summary>Откуда формула: «Charging.DeckMass», статья, автор.</summary>
    public string Source { get; set; } = "";

    public FormulaDef Clone() => (FormulaDef)MemberwiseClone();
    public override string ToString() => $"{Property} = {Expression}";
}

/// <summary>Точка таблицы параметра: ключ → значение, между точками линейная интерполяция.</summary>
public sealed record TablePoint(double X, double Y);

/// <summary>Уровни, на которых параметр можно переопределить (снизу вверх ищется первое заданное).</summary>
[Flags]
public enum ParameterLevel { Profile = 1, Site = 2, Blast = 4, HoleType = 8, Hole = 16, All = 31 }

/// <summary>
/// Параметр: <c>site.vib.K</c>. Значение ищется снизу вверх: скважина → тип скважины → взрыв → рудник → профиль.
/// Параметр, привязанный к полю модели (<see cref="Binding"/>), на уровне взрыва берёт значение поля.
/// </summary>
public sealed class ParameterDef
{
    public string Id { get; set; } = "";
    public string NameRu { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string Unit { get; set; } = "?";
    public double Value { get; set; }
    /// <summary>Таблица для <c>lookup(id, x)</c>; при ней <see cref="Value"/> не используется.</summary>
    public List<TablePoint>? Table { get; set; }
    public ParameterLevel Levels { get; set; } = ParameterLevel.All;
    public string Description { get; set; } = "";
    public bool BuiltIn { get; set; }
    /// <summary>Поле модели, дающее значение уровня взрыва (<c>Vibration.SiteK</c>); заполняет реестр.</summary>
    public string? Bind { get; set; }

    public ParameterDef Clone()
    {
        var c = (ParameterDef)MemberwiseClone();
        c.Table = Table?.ToList();
        return c;
    }
}

/// <summary>Правило: условие-выражение и действие (шаблон колонки, продукт, тег, запись значения). Исполнение — этап 4.</summary>
public sealed class RuleDef
{
    public string Set { get; set; } = "";
    public string Name { get; set; } = "";
    public int Priority { get; set; }
    public string Condition { get; set; } = "";
    /// <summary>"column", "product", "tag", "set".</summary>
    public string Action { get; set; } = "";
    public string Argument { get; set; } = "";
    public bool FirstMatch { get; set; } = true;
}

/// <summary>Область встроенного алгоритма: один расчёт на взрыв или по сущности.</summary>
public enum BuiltinScope { Blast, Entity }

/// <summary>
/// Алгоритм в коде, доступный в формулах как функция (<c>cellArea()</c>): объявленные входы дают рёбра графа, расчёт
/// на взрыв (<see cref="Prepare"/>) выполняется один раз, пока не устарел хоть один вход.
/// </summary>
public sealed class BuiltinDef
{
    /// <summary>Имя функции в формулах.</summary>
    public string Name { get; init; } = "";
    public string NameRu { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Контексты, где функция доступна; пусто — везде.</summary>
    public string[] Contexts { get; init; } = Array.Empty<string>();
    public BuiltinScope Scope { get; init; } = BuiltinScope.Blast;
    /// <summary>Входы: свойства (<c>hole.x</c>, <c>hole.chargeMass</c>) и параметры (<c>param:site.vib.window</c>).</summary>
    public string[] Inputs { get; init; } = Array.Empty<string>();
    /// <summary>Выходы, для справки редактора.</summary>
    public string[] Outputs { get; init; } = Array.Empty<string>();
    public string Unit { get; init; } = "?";
    /// <summary>Число аргументов-констант (строки или числа), например <c>distToLine('crest')</c>.</summary>
    public int ArgCount { get; init; }
    /// <summary>Настраиваемые аргументы (ключи <see cref="CalcScope.BuiltinArgs"/>) для окна.</summary>
    public string[] Settings { get; init; } = Array.Empty<string>();
    /// <summary>Расчёт на весь взрыв; результат передаётся в <see cref="Value"/>.</summary>
    public Func<CalcSession, object?>? Prepare { get; init; }
    /// <summary>Значение для сущности: (сеанс, результат Prepare, сущность, аргументы).</summary>
    public Func<CalcSession, object?, object, object[], double> Value { get; init; } = (_, _, _, _) => double.NaN;
}

/// <summary>Чистая функция без состояния: <c>studentT(p, df)</c>.</summary>
public sealed class FunctionDef
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public int ArgCount { get; init; }
    public string Unit { get; init; } = "?";
    public Func<double[], double> Invoke { get; init; } = _ => double.NaN;
}

/// <summary>Контрольный пример профиля: свойство или выражение на эталонном проекте и ожидаемое значение.</summary>
public sealed class TestCaseDef
{
    public string Name { get; set; } = "";
    /// <summary>Выражение в контексте blast: <c>powderFactor</c>, <c>sum(holes, chargeMass)</c>.</summary>
    public string Expression { get; set; } = "";
    public double Expected { get; set; }
    public double Tolerance { get; set; } = 1e-6;
}
