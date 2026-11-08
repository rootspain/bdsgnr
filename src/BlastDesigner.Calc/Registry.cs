namespace BlastDesigner.Calc;

/// <summary>Контекст — тип сущности (<c>hole</c>, <c>deck</c>): его свойства, связи и способ перечислить все объекты.</summary>
public sealed class ContextDef
{
    public string Name { get; init; } = "";
    public string NameRu { get; init; } = "";
    public Type ClrType { get; init; } = typeof(object);
    /// <summary>Все объекты контекста в проекте сеанса.</summary>
    public Func<CalcScope, IEnumerable<object>> All { get; init; } = _ => Array.Empty<object>();
    /// <summary>Принадлежит ли объект контексту (точки наблюдения — только точки вида Monitoring).</summary>
    public Func<object, bool>? Accepts { get; init; }
    public Dictionary<string, PropertyDef> Properties { get; } = new(StringComparer.Ordinal);

    public PropertyDef? Find(string name) => Properties.GetValueOrDefault(name);
    public override string ToString() => Name;
}

/// <summary>
/// Реестр контекстов, свойств модели, встроенных алгоритмов, функций и параметров по умолчанию. Поля модели
/// регистрирует <see cref="ModelContexts"/>; встроенные алгоритмы (ячейки, время, МВЗ) — сборка Engine.
/// </summary>
public sealed class CalcRegistry
{
    private static readonly Lazy<CalcRegistry> _default = new(() =>
    {
        var r = new CalcRegistry();
        ModelContexts.Register(r);
        return r;
    });

    /// <summary>Реестр с полями модели; Engine добавляет в него свои алгоритмы при первом обращении к расчётам.</summary>
    public static CalcRegistry Default => _default.Value;

    public Dictionary<string, ContextDef> Contexts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, BuiltinDef> Builtins { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FunctionDef> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Параметры, привязанные к полям модели, с геттером значения уровня взрыва.</summary>
    public Dictionary<string, Func<CalcScope, double>> ParameterBindings { get; } = new(StringComparer.Ordinal);

    /// <summary>Увеличивается при каждой регистрации: скомпилированные модели по старому реестру пересобираются.</summary>
    public int Revision { get; private set; }

    public ContextDef? Context(string name) => Contexts.GetValueOrDefault(name);

    public ContextDef AddContext(ContextDef c)
    {
        Contexts[c.Name] = c;
        Revision++;
        return c;
    }

    public void AddBuiltin(BuiltinDef b) { Builtins[b.Name] = b; Revision++; }
    public void AddFunction(FunctionDef f) { Functions[f.Name] = f; Revision++; }
    public void BindParameter(string id, Func<CalcScope, double> getter) { ParameterBindings[id] = getter; Revision++; }

    /// <summary>Свойство по полному имени (<c>hole.length</c>), из реестра или профиля.</summary>
    public PropertyDef? Property(string id)
    {
        var dot = id.IndexOf('.');
        return dot > 0 && Contexts.TryGetValue(id[..dot], out var c) ? c.Find(id[(dot + 1)..]) : null;
    }

    /// <summary>Контексты, которым принадлежит объект (тип скважины и материал — один класс в двух контекстах).</summary>
    public IEnumerable<ContextDef> ContextsOf(object o)
    {
        var t = o.GetType();
        foreach (var c in Contexts.Values)
            if (c.ClrType.IsAssignableFrom(t) && (c.Accepts == null || c.Accepts(o))) yield return c;
    }

    /// <summary>Добавляет поле модели как input-свойство.</summary>
    public PropertyDef Input<TObj, TValue>(string id, Func<CalcScope, TObj, TValue> get, string unit = "?", string ru = "", string en = "", params string[] fields)
    {
        var (type, del, enumType) = Wrap(get);
        var def = new PropertyDef
        {
            Id = id, Kind = PropertyKind.Input, Type = type, Unit = unit, NameRu = ru, NameEn = en, BuiltIn = true,
            Getter = del, EnumType = enumType, Fields = fields
        };
        return Put(def);
    }

    /// <summary>Добавляет связь N:1 (<c>deck.product</c>).</summary>
    public PropertyDef Ref<TObj>(string id, string target, Func<CalcScope, TObj, object?> get, string? inverse = null, string ru = "", params string[] fields) =>
        Put(new PropertyDef
        {
            Id = id, Kind = PropertyKind.Input, Type = CalcType.Ref, Target = target, Inverse = inverse, NameRu = ru, BuiltIn = true,
            Getter = new Func<CalcScope, object, object?>((s, o) => get(s, (TObj)o)), Fields = fields
        });

    /// <summary>Добавляет связь 1:N (<c>hole.decks</c>), упорядоченную.</summary>
    public PropertyDef Many<TObj>(string id, string target, Func<CalcScope, TObj, IReadOnlyList<object>> get, string? inverse = null, string ru = "", params string[] fields) =>
        Put(new PropertyDef
        {
            Id = id, Kind = PropertyKind.Input, Type = CalcType.List, Target = target, Inverse = inverse, NameRu = ru, BuiltIn = true,
            Getter = new Func<CalcScope, object, IReadOnlyList<object>>((s, o) => get(s, (TObj)o)), Fields = fields
        });

    private PropertyDef Put(PropertyDef def)
    {
        var ctx = Context(def.Context) ?? throw new InvalidOperationException($"Нет контекста {def.Context}");
        ctx.Properties[def.Name] = def;
        Revision++;
        return def;
    }

    private static (CalcType, Delegate, Type?) Wrap<TObj, TValue>(Func<CalcScope, TObj, TValue> get)
    {
        var t = typeof(TValue);
        if (t == typeof(double)) return (CalcType.Number, new Func<CalcScope, object, double>((s, o) => (double)(object)get(s, (TObj)o)!), null);
        if (t == typeof(double?)) return (CalcType.Number, new Func<CalcScope, object, double>((s, o) => ((double?)(object?)get(s, (TObj)o)) ?? double.NaN), null);
        if (t == typeof(int)) return (CalcType.Number, new Func<CalcScope, object, double>((s, o) => (int)(object)get(s, (TObj)o)!), null);
        if (t == typeof(int?)) return (CalcType.Number, new Func<CalcScope, object, double>((s, o) => ((int?)(object?)get(s, (TObj)o)) ?? double.NaN), null);
        if (t == typeof(bool)) return (CalcType.Bool, new Func<CalcScope, object, bool>((s, o) => (bool)(object)get(s, (TObj)o)!), null);
        if (t == typeof(string)) return (CalcType.String, new Func<CalcScope, object, string?>((s, o) => (string?)(object?)get(s, (TObj)o)), null);
        if (t.IsEnum) return (CalcType.Enum, new Func<CalcScope, object, int>((s, o) => Convert.ToInt32(get(s, (TObj)o))), t);
        var nt = Nullable.GetUnderlyingType(t);
        if (nt is { IsEnum: true })
            return (CalcType.Enum, new Func<CalcScope, object, int>((s, o) => get(s, (TObj)o) is { } v ? Convert.ToInt32(v) : -1), nt);
        throw new NotSupportedException($"Тип поля {t.Name} не поддерживается");
    }
}
