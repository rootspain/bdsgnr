using BlastDesigner.Domain.Model;

namespace BlastDesigner.Calc;

/// <summary>Значение свойства сущности: число, логическое, строка или ошибка.</summary>
public readonly record struct CalcValue(double Number, object? Object, string? Error)
{
    public bool IsError => Error != null;
    public static CalcValue Fail(string error) => new(double.NaN, null, error);
    public override string ToString() => Error != null ? "#" + Error : Object?.ToString() ?? Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Ошибка узла, всплывающая через зависимые формулы.</summary>
public sealed class CalcEvaluationException(string message) : Exception(message);

/// <summary>Сообщение о сущности, у которой формула не вычислилась.</summary>
public sealed record CalcIssue(string Property, object Entity, string Message);

/// <summary>
/// Сеанс расчёта над проектом: кэш значений по сущностям с флагом устаревания, ленивое вычисление узлов по запросу
/// (вытягивание с мемоизацией — порядок обеспечивается зависимостями, циклы отсекает граф модели), инвалидация вниз
/// по рёбрам графа при изменении полей. Короткий сеанс (одно обращение старых методов Charging/Quantities) создаётся
/// и выбрасывается; долгий живёт рядом с документом и синхронизируется <see cref="Sync"/>.
/// </summary>
public sealed class CalcSession
{
    private const byte None = 0, Valid = 1, Computing = 2, Failed = 3;

    private struct Slot
    {
        public byte State;
        public double Number;
        public object? Object;
        public string? Error;
    }

    private readonly Dictionary<object, Slot[]>[] _cache;
    private readonly object?[] _prepared;
    private readonly byte[] _preparedState;
    private readonly string?[] _preparedError;
    private readonly Dictionary<string, Dictionary<object, List<object>>> _inverse = new(StringComparer.Ordinal);
    private Dictionary<(string Entity, string Property), double>? _overrides;
    private HashSet<string>? _overriddenProps;
    private Dictionary<object, object?[]>[]? _snapshot;
    private double[]? _paramSnapshot;

    public CalcSession(CalcModel model, BlastProject project) : this(model, new CalcScope(project)) { }

    public CalcSession(CalcModel model, CalcScope scope)
    {
        Model = model;
        Scope = scope;
        _cache = model.Contexts.Select(_ => new Dictionary<object, Slot[]>(ReferenceEqualityComparer.Instance)).ToArray();
        _prepared = new object?[model.BuiltinNames.Count];
        _preparedState = new byte[model.BuiltinNames.Count];
        _preparedError = new string?[model.BuiltinNames.Count];
    }

    public CalcModel Model { get; }
    public CalcScope Scope { get; }
    public BlastProject Project => Scope.Project;

    /// <summary>Параметры уровня взрыва (из проекта), переопределяют привязку к модели и значение профиля.</summary>
    public Dictionary<string, double> BlastParameters { get; } = new(StringComparer.Ordinal);
    /// <summary>Параметры по типам скважин: номер типа → id → значение.</summary>
    public Dictionary<int, Dictionary<string, double>> HoleTypeParameters { get; } = new();
    /// <summary>Параметры отдельных скважин: Guid → id → значение.</summary>
    public Dictionary<Guid, Dictionary<string, double>> HoleParameters { get; } = new();

    /// <summary>Сколько раз вычислялась формула (для проверки, что пересчёт затрагивает только устаревшее).</summary>
    public int Evaluations { get; private set; }

    // ---- ручные значения ------------------------------------------------

    public void SetOverrides(IEnumerable<ValueOverride> overrides)
    {
        _overrides = new();
        _overriddenProps = new(StringComparer.Ordinal);
        foreach (var o in overrides)
        {
            _overrides[(o.Entity, o.Property)] = o.Value;
            _overriddenProps.Add(o.Property);
        }
        Reset();
    }

    // ---- чтение значений ------------------------------------------------

    public CalcValue Value(string propertyId, object entity)
    {
        var node = Model.NodeOf(propertyId);
        if (node == null)
        {
            if (Model.FindProperty(propertyId) is { Kind: PropertyKind.Input, Getter: { } g } p)
                return p.Type switch
                {
                    CalcType.Number => new CalcValue(((Func<CalcScope, object, double>)g)(Scope, entity), null, null),
                    CalcType.Bool => new CalcValue(double.NaN, ((Func<CalcScope, object, bool>)g)(Scope, entity), null),
                    CalcType.String => new CalcValue(double.NaN, ((Func<CalcScope, object, string?>)g)(Scope, entity), null),
                    _ => new CalcValue(double.NaN, g.DynamicInvoke(Scope, entity), null)
                };
            return CalcValue.Fail($"Нет свойства {propertyId}");
        }
        return Value(node, entity);
    }

    public CalcValue Value(CalcNode node, object entity)
    {
        ref var s = ref Evaluate(node, entity);
        return s.State == Failed ? CalcValue.Fail(s.Error!) : new CalcValue(s.Number, s.Object, null);
    }

    /// <summary>Число свойства; ошибка — NaN.</summary>
    public double Number(string propertyId, object entity) => Value(propertyId, entity) is { IsError: false } v ? v.Number : double.NaN;

    /// <summary>Вычисляет выражение в контексте сущности (предпросмотр, контрольные примеры).</summary>
    public CalcValue Evaluate(CompiledExpression expr, object entity)
    {
        try
        {
            return expr.Type switch
            {
                CalcType.Number => new CalcValue(expr.Number(this, entity), null, null),
                CalcType.Bool => new CalcValue(double.NaN, expr.Bool(this, entity), null),
                _ => new CalcValue(double.NaN, ((Func<CalcSession, object, string?>)expr.Delegate)(this, entity), null)
            };
        }
        catch (Exception e) { return CalcValue.Fail(Message(e)); }
    }

    // ---- вызовы из скомпилированных формул -----------------------------

    public double GetNumber(int node, object entity)
    {
        ref var s = ref Evaluate(Model.Nodes[node], entity);
        if (s.State == Failed) throw new CalcEvaluationException($"{Model.Nodes[node].Id}: {s.Error}");
        return s.Number;
    }

    public bool GetBool(int node, object entity)
    {
        ref var s = ref Evaluate(Model.Nodes[node], entity);
        if (s.State == Failed) throw new CalcEvaluationException($"{Model.Nodes[node].Id}: {s.Error}");
        return s.Object is true;
    }

    public string? GetString(int node, object entity)
    {
        ref var s = ref Evaluate(Model.Nodes[node], entity);
        if (s.State == Failed) throw new CalcEvaluationException($"{Model.Nodes[node].Id}: {s.Error}");
        return s.Object as string;
    }

    /// <summary>Параметр: скважина → тип скважины → взрыв → привязанное поле модели → рудник → профиль.</summary>
    public double Param(int index, object entity)
    {
        var p = Model.Parameters[index];
        var id = p.Def.Id;
        var hole = HoleOf(entity);
        if (hole != null)
        {
            if (p.Def.Levels.HasFlag(ParameterLevel.Hole) && HoleParameters.TryGetValue(hole.Id, out var hp) && hp.TryGetValue(id, out var hv)) return hv;
            if (p.Def.Levels.HasFlag(ParameterLevel.HoleType) && HoleTypeParameters.TryGetValue(hole.HoleType, out var tp) && tp.TryGetValue(id, out var tv)) return tv;
        }
        return BlastLevel(p);
    }

    private double BlastLevel(CompiledParameter p)
    {
        var id = p.Def.Id;
        if (p.Def.Levels.HasFlag(ParameterLevel.Blast))
        {
            if (BlastParameters.TryGetValue(id, out var bv)) return bv;
            if (p.Binding != null) return p.Binding(Scope);
        }
        if (p.Def.Levels.HasFlag(ParameterLevel.Site) && Scope.SiteParameters.TryGetValue(id, out var sv)) return sv;
        return p.Def.Value;
    }

    public double Lookup(int index, double x, object entity)
    {
        var t = Model.Parameters[index].Def.Table!;
        if (double.IsNaN(x)) return double.NaN;
        if (x <= t[0].X) return t[0].Y;
        for (int i = 1; i < t.Count; i++)
            if (x <= t[i].X)
            {
                var a = t[i - 1];
                var b = t[i];
                return b.X - a.X < 1e-12 ? b.Y : a.Y + (b.Y - a.Y) * (x - a.X) / (b.X - a.X);
            }
        return t[^1].Y;
    }

    public double Builtin(int index, object entity, object[] args)
    {
        var def = Model.Registry.Builtins[Model.BuiltinNames[index]];
        if (_preparedState[index] == Computing) throw new CalcEvaluationException($"Цикл через {def.Name}()");
        if (_preparedState[index] == None)
        {
            if (def.Prepare != null)
            {
                _preparedState[index] = Computing;
                try { _prepared[index] = def.Prepare(this); _preparedError[index] = null; }
                catch (Exception e) { _preparedError[index] = Message(e); }
            }
            _preparedState[index] = Valid;
        }
        if (_preparedError[index] is { } err) throw new CalcEvaluationException($"{def.Name}(): {err}");
        return def.Value(this, _prepared[index], entity, args);
    }

    /// <summary>Результат расчёта встроенного алгоритма на взрыв (ячейки, МВЗ), вычисленный при необходимости.</summary>
    public object? Prepared(string builtin)
    {
        var i = Model.BuiltinIndex(builtin);
        if (i < 0) return null;
        if (_preparedState[i] == None) Builtin(i, Project, Array.Empty<object>());
        return _prepared[i];
    }

    private Hole? HoleOf(object entity) => entity switch
    {
        Hole h => h,
        Deck d => Scope.HoleOf(d),
        Primer p => Scope.DeckOf(p) is { } d ? Scope.HoleOf(d) : null,
        _ => null
    };

    // ---- вычисление узла -----------------------------------------------

    private Slot[] SlotsFor(CalcNode node, object entity)
    {
        var dict = _cache[node.ContextIndex];
        if (!dict.TryGetValue(entity, out var slots)) dict[entity] = slots = new Slot[Model.LocalCounts[node.ContextIndex]];
        return slots;
    }

    private ref Slot Evaluate(CalcNode node, object entity)
    {
        var slots = SlotsFor(node, entity);
        ref var s = ref slots[node.Local];
        if (s.State is Valid or Failed) return ref s;
        if (s.State == Computing)
        {
            s.State = Failed;
            s.Error = "Цикл при вычислении " + node.Id;
            return ref s;
        }
        s.State = Computing;
        double num = double.NaN;
        object? obj = null;
        string? error = null;
        try
        {
            if (_overriddenProps != null && _overriddenProps.Contains(node.Id) && Scope.KeyOf(entity) is { } key && _overrides!.TryGetValue((key, node.Id), out var ov))
                num = ov;
            else if (node.Error != null) error = node.Error;
            else (num, obj, error) = Compute(node, entity);
        }
        catch (Exception e) { error = Message(e); }
        // ячейка могла смениться при пересчёте зависимостей? Нет: массив ячеек сущности не пересоздаётся
        s.Number = num;
        s.Object = obj;
        s.Error = error;
        s.State = error != null ? Failed : Valid;
        return ref s;
    }

    private (double, object?, string?) Compute(CalcNode node, object entity)
    {
        foreach (var f in node.Formulas)
        {
            if (f.Value == null) continue;
            try
            {
                if (f.Condition != null && !f.Condition.Bool(this, entity)) continue;
            }
            catch (Exception e)
            {
                if (f.Def.OnError is { } fallback) return (fallback, null, null);
                return (double.NaN, null, "условие: " + Message(e));
            }
            Evaluations++;
            try
            {
                switch (node.Property.Type)
                {
                    case CalcType.Bool: return (double.NaN, f.Value.Bool(this, entity), null);
                    case CalcType.String: return (double.NaN, ((Func<CalcSession, object, string?>)f.Value.Delegate)(this, entity), null);
                }
                var v = f.Value.Number(this, entity);
                if (double.IsNaN(v))
                {
                    if (f.Def.OnError is { } fb) return (fb, null, null);
                    return (v, null, "нет значения (пустая ссылка или 0/0)");
                }
                return (v, null, null);
            }
            catch (Exception e)
            {
                if (f.Def.OnError is { } fallback) return (fallback, null, null);
                return (double.NaN, null, Message(e));
            }
        }
        return (double.NaN, null, "ни одно условие формулы не выполнено");
    }

    private static string Message(Exception e) => e is System.Reflection.TargetInvocationException { InnerException: { } inner } ? Message(inner) : e.Message;

    /// <summary>Сущности с ошибками формул (для предупреждений и тега скважины).</summary>
    public IEnumerable<CalcIssue> Issues()
    {
        foreach (var node in Model.Nodes)
            foreach (var (entity, slots) in _cache[node.ContextIndex])
                if (slots[node.Local].State == Failed) yield return new CalcIssue(node.Id, entity, slots[node.Local].Error!);
    }

    /// <summary>Есть ли у кэшированного значения флаг «вычислено» (для тестов инвалидации).</summary>
    public bool IsCached(string propertyId, object entity)
    {
        var node = Model.NodeOf(propertyId);
        return node != null && _cache[node.ContextIndex].TryGetValue(entity, out var slots) && slots[node.Local].State is Valid or Failed;
    }

    // ---- инвалидация ----------------------------------------------------

    /// <summary>Сбрасывает весь кэш и индексы (изменился состав объектов или связи).</summary>
    public void Reset()
    {
        foreach (var d in _cache) d.Clear();
        Array.Clear(_preparedState);
        Array.Clear(_prepared);
        _inverse.Clear();
        Scope.ResetIndexes();
    }

    /// <summary>
    /// Поле модели сущности изменилось: устаревают узлы, читающие его (через ту же сущность, по ссылке N:1 через
    /// обратную связь, в агрегатах 1:N, через встроенные алгоритмы), и всё, что зависит от них.
    /// </summary>
    public void Invalidate(object entity, string field)
    {
        foreach (var ctx in Model.Registry.ContextsOf(entity))
            foreach (var p in ctx.Properties.Values)
                if (p.Kind == PropertyKind.Input && Matches(p, field))
                    MarkStale(p.Id, entity, new HashSet<(int, object)>());
    }

    /// <summary>Параметр изменился (в профиле, проекте, по типу скважины): устаревает всё, что его читает.</summary>
    public void InvalidateParameter(string id) => MarkStale("param:" + id, Project, new HashSet<(int, object)>());

    private static bool Matches(PropertyDef p, string field) =>
        string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase) || p.Fields.Any(f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase));

    private void MarkStale(string source, object entity, HashSet<(int, object)> seen)
    {
        foreach (var (n, dep) in Model.DependentsOf(source))
        {
            if (n >= Model.Nodes.Count)
            {
                var bi = n - Model.Nodes.Count;
                if (!seen.Add((n, Project))) continue;
                if (_preparedState[bi] == None) continue;
                _preparedState[bi] = None;
                _prepared[bi] = null;
                MarkStale("builtin:" + Model.BuiltinNames[bi], Project, seen);
                continue;
            }
            var node = Model.Nodes[n];
            foreach (var target in Targets(node.Context, dep, entity))
            {
                if (!seen.Add((n, target))) continue;
                if (!_cache[node.ContextIndex].TryGetValue(target, out var slots) || slots[node.Local].State == None) continue;
                slots[node.Local] = default;
                MarkStale(node.Id, target, seen);
            }
        }
    }

    /// <summary>Сущности контекста узла, чей путь связей ведёт к изменённой сущности (обратная сторона пути).</summary>
    private IReadOnlyList<object> Targets(ContextDef ctx, Dependency dep, object entity)
    {
        if (dep.Path.Count == 0) return new[] { entity };
        var key = ctx.Name + ":" + dep.PathText;
        if (!_inverse.TryGetValue(key, out var map))
        {
            map = new Dictionary<object, List<object>>(ReferenceEqualityComparer.Instance);
            foreach (var start in ctx.All(Scope))
                foreach (var reached in Walk(start, dep.Path, 0))
                {
                    if (!map.TryGetValue(reached, out var list)) map[reached] = list = new();
                    if (list.Count == 0 || !ReferenceEquals(list[^1], start)) list.Add(start);
                }
            _inverse[key] = map;
        }
        return map.TryGetValue(entity, out var l) ? l : Array.Empty<object>();
    }

    private IEnumerable<object> Walk(object o, IReadOnlyList<PropertyDef> path, int i)
    {
        if (i == path.Count) { yield return o; yield break; }
        var rel = path[i];
        if (rel.Type == CalcType.Ref)
        {
            if (((Func<CalcScope, object, object?>)rel.Getter!)(Scope, o) is { } next)
                foreach (var r in Walk(next, path, i + 1)) yield return r;
        }
        else if (rel.Type == CalcType.List)
            foreach (var next in ((Func<CalcScope, object, IReadOnlyList<object>>)rel.Getter!)(Scope, o))
                foreach (var r in Walk(next, path, i + 1)) yield return r;
    }

    /// <summary>
    /// Сравнивает поля модели с запомненными при прошлой синхронизации и инвалидирует изменившиеся. Изменение состава
    /// объектов или связей сбрасывает весь кэш (значения пересчитаются при обращении). Возвращает число изменённых полей.
    /// </summary>
    public int Sync()
    {
        var contexts = Model.Contexts;
        // индексы (ряд скважины, продукт по id) могли устареть вместе с моделью
        Scope.ResetIndexes();
        var fresh = new Dictionary<object, object?[]>[contexts.Count];
        var structural = _snapshot == null;
        var changes = new List<(object Entity, PropertyDef Prop)>();
        for (int c = 0; c < contexts.Count; c++)
        {
            var inputs = Inputs(contexts[c]);
            var map = new Dictionary<object, object?[]>(ReferenceEqualityComparer.Instance);
            foreach (var o in contexts[c].All(Scope)) map[o] = inputs.Select(p => Read(p, o)).ToArray();
            fresh[c] = map;
            if (_snapshot == null) continue;
            var old = _snapshot[c];
            if (old.Count != map.Count) { structural = true; continue; }
            foreach (var (o, values) in map)
            {
                if (!old.TryGetValue(o, out var before)) { structural = true; break; }
                for (int i = 0; i < values.Length; i++)
                    if (!Same(before[i], values[i]))
                    {
                        if (inputs[i].IsRelation) structural = true;
                        else changes.Add((o, inputs[i]));
                    }
            }
        }
        var paramValues = Model.Parameters.Select(BlastLevel).ToArray();
        _snapshot = fresh;
        if (structural)
        {
            Reset();
            _paramSnapshot = paramValues;
            return -1;
        }
        foreach (var (o, p) in changes) MarkStale(p.Id, o, new HashSet<(int, object)>());
        for (int i = 0; i < paramValues.Length; i++)
            if (_paramSnapshot != null && !paramValues[i].Equals(_paramSnapshot[i]))
            {
                InvalidateParameter(Model.Parameters[i].Def.Id);
                changes.Add((Project, null!));
            }
        _paramSnapshot = paramValues;
        return changes.Count;
    }

    private readonly Dictionary<string, PropertyDef[]> _inputs = new();

    private PropertyDef[] Inputs(ContextDef c) =>
        _inputs.TryGetValue(c.Name, out var a) ? a : _inputs[c.Name] = c.Properties.Values.Where(p => p.Kind == PropertyKind.Input && p.Getter != null && p.Name != "blast").ToArray();

    private object? Read(PropertyDef p, object o)
    {
        try
        {
            return p.Type switch
            {
                CalcType.Number => ((Func<CalcScope, object, double>)p.Getter!)(Scope, o),
                CalcType.Bool => ((Func<CalcScope, object, bool>)p.Getter!)(Scope, o),
                CalcType.String => ((Func<CalcScope, object, string?>)p.Getter!)(Scope, o),
                CalcType.Enum => ((Func<CalcScope, object, int>)p.Getter!)(Scope, o),
                CalcType.Ref => ((Func<CalcScope, object, object?>)p.Getter!)(Scope, o),
                _ => ((Func<CalcScope, object, IReadOnlyList<object>>)p.Getter!)(Scope, o).ToArray()
            };
        }
        catch (Exception) { return null; }
    }

    private static bool Same(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (double x, double y) => x.Equals(y),
        (object[] x, object[] y) => x.Length == y.Length && x.Zip(y).All(p => ReferenceEquals(p.First, p.Second)),
        ({ } x, { } y) when x.GetType().IsValueType || x is string => x.Equals(y),
        _ => ReferenceEquals(a, b)
    };
}
