namespace BlastDesigner.Calc;

/// <summary>Сообщение проверки профиля: ошибка компиляции формулы, цикл, неизвестный тип.</summary>
public sealed record CalcDiagnostic(string Property, string Message, int Position = 0, int Length = 0, bool IsWarning = false)
{
    public override string ToString() => $"{Property}: {Message}";
}

/// <summary>Узел графа: вычисляемое свойство контекста (не каждая сущность) или встроенный алгоритм.</summary>
public sealed class CalcNode
{
    public int Index { get; init; }
    public string Id { get; init; } = "";
    public PropertyDef Property { get; init; } = null!;
    public ContextDef Context { get; init; } = null!;
    /// <summary>Номер среди вычисляемых узлов контекста (ячейка кэша сущности).</summary>
    public int Local { get; init; }
    public int ContextIndex { get; init; }
    public List<CompiledFormula> Formulas { get; } = new();
    /// <summary>Объединение зависимостей всех вариантов формулы.</summary>
    public List<Dependency> Dependencies { get; } = new();
    /// <summary>Ошибка компиляции или цикла: узел всегда даёт её.</summary>
    public string? Error { get; set; }
    /// <summary>Порядок в топологической сортировке (стадия).</summary>
    public int Order { get; set; }
    public override string ToString() => Id;
}

public sealed class CompiledFormula
{
    public FormulaDef Def { get; init; } = null!;
    public CompiledExpression? Value { get; init; }
    public CompiledExpression? Condition { get; init; }
}

public sealed class CompiledParameter
{
    public int Index { get; init; }
    public ParameterDef Def { get; init; } = null!;
    public Func<CalcScope, double>? Binding { get; init; }
}

/// <summary>
/// Скомпилированный профиль: узлы (свойство × контекст), формулы-делегаты, параметры, граф зависимостей с
/// топологическим порядком (Кан). Неизменяем; один на профиль, сеансы (<see cref="CalcSession"/>) создаются на проект.
/// </summary>
public sealed class CalcModel
{
    private readonly Dictionary<string, PropertyDef> _computed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CalcNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompiledParameter> _params = new(StringComparer.Ordinal);
    private readonly List<string> _builtinNames = new();
    private readonly Dictionary<string, List<(int Node, Dependency Dep)>> _dependents = new(StringComparer.Ordinal);

    public CalcRegistry Registry { get; }
    public CalcProfile Profile { get; }
    public int RegistryRevision { get; }
    public IReadOnlyList<CalcNode> Nodes { get; }
    public IReadOnlyList<CompiledParameter> Parameters { get; }
    public IReadOnlyList<CalcDiagnostic> Diagnostics { get; }
    /// <summary>Узлы встроенных алгоритмов: индекс → имя.</summary>
    public IReadOnlyList<string> BuiltinNames => _builtinNames;
    /// <summary>Зависимости встроенного алгоритма (входы с путём от взрыва).</summary>
    public IReadOnlyList<IReadOnlyList<Dependency>> BuiltinDependencies { get; }
    /// <summary>Узлы в порядке пересчёта (входы → геометрия → массы → итоги → прогнозы).</summary>
    public IReadOnlyList<CalcNode> Order { get; }
    public IReadOnlyList<ContextDef> Contexts { get; }

    public bool HasErrors => Diagnostics.Any(d => !d.IsWarning);

    /// <summary>Компилирует профиль (уже разложенный, <see cref="CalcProfile.Flatten"/>). Ошибки не бросаются, а копятся в <see cref="Diagnostics"/>.</summary>
    public CalcModel(CalcRegistry registry, CalcProfile profile)
    {
        Registry = registry;
        Profile = profile;
        RegistryRevision = registry.Revision;
        var diags = new List<CalcDiagnostic>();
        Contexts = registry.Contexts.Values.ToList();
        var ctxIndex = Contexts.Select((c, i) => (c, i)).ToDictionary(x => x.c.Name, x => x.i);

        foreach (var p in profile.Properties)
        {
            if (registry.Context(p.Context) == null) { diags.Add(new(p.Id, $"Нет контекста «{p.Context}»")); continue; }
            if (registry.Property(p.Id) is { Kind: PropertyKind.Input }) { diags.Add(new(p.Id, "Это поле модели, его нельзя вычислять формулой")); continue; }
            var def = p.Clone();
            def.Kind = PropertyKind.Computed;
            if (def.Type is not (CalcType.Number or CalcType.Bool or CalcType.String)) { diags.Add(new(p.Id, "Вычисляемое свойство — число, логическое или строка")); def.Type = CalcType.Number; }
            _computed[def.Id] = def;
        }
        foreach (var f in profile.Formulas)
        {
            if (_computed.ContainsKey(f.Property) || f.Property.IndexOf('.') <= 0) continue;
            if (registry.Context(f.Property[..f.Property.IndexOf('.')]) == null) { diags.Add(new(f.Property, "Нет такого контекста")); continue; }
            if (registry.Property(f.Property) is { Kind: PropertyKind.Input }) { diags.Add(new(f.Property, "Это поле модели, его нельзя вычислять формулой")); continue; }
            _computed[f.Property] = new PropertyDef { Id = f.Property, Kind = PropertyKind.Computed, Type = CalcType.Number, Unit = "?" };
        }

        var nodes = new List<CalcNode>();
        var localCount = new Dictionary<string, int>();
        foreach (var p in _computed.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var ctx = registry.Context(p.Context)!;
            var local = localCount.GetValueOrDefault(ctx.Name);
            localCount[ctx.Name] = local + 1;
            var n = new CalcNode { Index = nodes.Count, Id = p.Id, Property = p, Context = ctx, Local = local, ContextIndex = ctxIndex[ctx.Name] };
            nodes.Add(n);
            _nodes[p.Id] = n;
        }
        Nodes = nodes;
        LocalCounts = Contexts.Select(c => localCount.GetValueOrDefault(c.Name)).ToArray();

        var parameters = new List<CompiledParameter>();
        foreach (var p in profile.Parameters.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var cp = new CompiledParameter { Index = parameters.Count, Def = p, Binding = registry.ParameterBindings.GetValueOrDefault(p.Id) };
            parameters.Add(cp);
            _params[p.Id] = cp;
        }
        Parameters = parameters;
        _builtinNames.AddRange(registry.Builtins.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        // зависимости встроенных алгоритмов: входы с путём от взрыва
        var builtinDeps = new List<IReadOnlyList<Dependency>>();
        foreach (var name in _builtinNames)
        {
            var b = registry.Builtins[name];
            var deps = new List<Dependency>();
            foreach (var input in b.Inputs)
            {
                if (input.StartsWith("param:", StringComparison.Ordinal)) { deps.Add(new Dependency(input, Array.Empty<PropertyDef>())); continue; }
                var ctx = input[..Math.Max(0, input.IndexOf('.'))];
                if (FindProperty(ctx, input[(input.IndexOf('.') + 1)..]) == null) { diags.Add(new("builtin:" + name, $"Неизвестный вход {input}", IsWarning: true)); continue; }
                if (ctx == "blast") deps.Add(new Dependency(input, Array.Empty<PropertyDef>()));
                else if (registry.Context("blast")!.Properties.Values.FirstOrDefault(r => r.Type == CalcType.List && r.Target == ctx) is { } rel)
                    deps.Add(new Dependency(input, new[] { rel }));
            }
            builtinDeps.Add(deps);
        }
        BuiltinDependencies = builtinDeps;

        foreach (var g in profile.Formulas.GroupBy(f => f.Property))
        {
            if (!_nodes.TryGetValue(g.Key, out var node)) continue;
            foreach (var f in g.OrderBy(f => f.Priority))
            {
                CompiledExpression? value = null, cond = null;
                try
                {
                    value = Compiler.Compile(this, node.Context.Name, f.Expression, node.Property.Type);
                    CheckUnit(node, value, diags);
                    if (!string.IsNullOrWhiteSpace(f.Condition)) cond = Compiler.Compile(this, node.Context.Name, f.Condition!, CalcType.Bool);
                }
                catch (CalcSyntaxException e)
                {
                    diags.Add(new(node.Id, e.Message, e.Position, e.Length));
                    node.Error ??= e.Message;
                }
                node.Formulas.Add(new CompiledFormula { Def = f, Value = value, Condition = cond });
                if (value != null) node.Dependencies.AddRange(value.Dependencies);
                if (cond != null) node.Dependencies.AddRange(cond.Dependencies);
            }
        }
        foreach (var n in nodes.Where(n => n.Formulas.Count == 0))
        {
            n.Error = "Нет формулы";
            diags.Add(new(n.Id, "Нет формулы", IsWarning: true));
        }

        Order = Sort(diags);
        foreach (var n in nodes)
            foreach (var d in n.Dependencies) AddDependent(d.Source, n.Index, d);
        for (int i = 0; i < _builtinNames.Count; i++)
            foreach (var d in builtinDeps[i]) AddDependent(d.Source, BuiltinNodeIndex(i), d);
        Diagnostics = diags;
    }

    /// <summary>Число вычисляемых узлов по контекстам (размер ячеек кэша сущности).</summary>
    internal int[] LocalCounts { get; }

    /// <summary>Индекс узла встроенного алгоритма в общей нумерации зависимых: за вычисляемыми узлами.</summary>
    internal int BuiltinNodeIndex(int builtin) => Nodes.Count + builtin;

    private void AddDependent(string source, int node, Dependency d)
    {
        if (!_dependents.TryGetValue(source, out var list)) _dependents[source] = list = new();
        list.Add((node, d));
    }

    /// <summary>Узлы, читающие источник, с путём от их сущности к источнику.</summary>
    public IReadOnlyList<(int Node, Dependency Dep)> DependentsOf(string source) =>
        _dependents.TryGetValue(source, out var l) ? l : Array.Empty<(int, Dependency)>();

    private static void CheckUnit(CalcNode node, CompiledExpression value, List<CalcDiagnostic> diags)
    {
        if (node.Property.Type != CalcType.Number) return;
        if (!Unit.TryParse(node.Property.Unit, out var want)) { diags.Add(new(node.Id, $"Неизвестная единица «{node.Property.Unit}»", IsWarning: true)); return; }
        if (!want.SameDimension(value.Unit))
        {
            var msg = $"Формула даёт {value.Unit.DimensionText} ({value.Unit}), а свойство в {want} ({want.DimensionText})";
            diags.Add(new(node.Id, msg));
            node.Error ??= msg;
        }
    }

    /// <summary>Топологическая сортировка Кана по вычисляемым узлам и встроенным алгоритмам; цикл — ошибка с цепочкой.</summary>
    private List<CalcNode> Sort(List<CalcDiagnostic> diags)
    {
        int n = Nodes.Count + _builtinNames.Count;
        var edges = new List<int>[n];
        for (int i = 0; i < n; i++) edges[i] = new();
        var indeg = new int[n];
        int? SourceIndex(string s) =>
            _nodes.TryGetValue(s, out var node) ? node.Index
            : s.StartsWith("builtin:", StringComparison.Ordinal) && _builtinNames.FindIndex(b => string.Equals(b, s[8..], StringComparison.OrdinalIgnoreCase)) is var bi and >= 0 ? BuiltinNodeIndex(bi)
            : null;
        void Edge(string source, int to)
        {
            if (SourceIndex(source) is not { } from || edges[from].Contains(to)) return;
            edges[from].Add(to);
            indeg[to]++;
        }
        foreach (var node in Nodes)
            foreach (var d in node.Dependencies) Edge(d.Source, node.Index);
        for (int i = 0; i < _builtinNames.Count; i++)
            foreach (var d in BuiltinDependencies[i]) Edge(d.Source, BuiltinNodeIndex(i));

        var queue = new Queue<int>(Enumerable.Range(0, n).Where(i => indeg[i] == 0));
        var order = new List<CalcNode>();
        var level = new int[n];
        int done = 0;
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            done++;
            if (i < Nodes.Count) { Nodes[i].Order = level[i]; order.Add(Nodes[i]); }
            foreach (var j in edges[i])
            {
                level[j] = Math.Max(level[j], level[i] + 1);
                if (--indeg[j] == 0) queue.Enqueue(j);
            }
        }
        if (done < n)
        {
            var rest = Enumerable.Range(0, n).Where(i => indeg[i] > 0).ToHashSet();
            var chain = FindCycle(rest, edges);
            var text = string.Join(" → ", chain.Select(NameOf));
            foreach (var i in rest.Where(i => i < Nodes.Count))
            {
                Nodes[i].Error = "Цикл: " + text;
                order.Add(Nodes[i]);
            }
            diags.Add(new(NameOf(chain[0]), "Цикл зависимостей: " + text));
        }
        return order;
    }

    private string NameOf(int i) => i < Nodes.Count ? Nodes[i].Id : "builtin:" + _builtinNames[i - Nodes.Count];

    private static List<int> FindCycle(HashSet<int> rest, List<int>[] edges)
    {
        // идём по рёбрам внутри остатка, пока не встретим уже пройденный узел
        var start = rest.Min();
        var path = new List<int>();
        var pos = new Dictionary<int, int>();
        var cur = start;
        while (!pos.ContainsKey(cur))
        {
            pos[cur] = path.Count;
            path.Add(cur);
            cur = edges[cur].FirstOrDefault(rest.Contains, -1);
            if (cur < 0) return path;
        }
        var cycle = path.Skip(pos[cur]).ToList();
        cycle.Add(cur);
        return cycle;
    }

    // ---- поиск ----------------------------------------------------------

    public CalcNode? NodeOf(string id) => _nodes.GetValueOrDefault(id);
    public CompiledParameter? ParameterOf(string id) => _params.GetValueOrDefault(id);
    public int BuiltinIndex(string name) => _builtinNames.FindIndex(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Свойство контекста: вычисляемое профиля или поле модели.</summary>
    public PropertyDef? FindProperty(string context, string name) =>
        _computed.GetValueOrDefault(context + "." + name) ?? Registry.Context(context)?.Find(name);

    public PropertyDef? FindProperty(string id) => _computed.GetValueOrDefault(id) ?? Registry.Property(id);

    /// <summary>Все свойства контекста: поля модели, связи и вычисляемые.</summary>
    public IEnumerable<PropertyDef> PropertiesOf(string context)
    {
        var ctx = Registry.Context(context);
        if (ctx == null) yield break;
        foreach (var p in ctx.Properties.Values) yield return p;
        foreach (var p in _computed.Values) if (p.Context == context) yield return p;
    }

    // ---- русские имена ----------------------------------------------------

    private readonly Dictionary<string, Dictionary<string, PropertyDef>> _aliases = new(StringComparer.Ordinal);

    private static string Key(string alias) => alias.Trim().ToLowerInvariant().Replace('ё', 'е');

    /// <summary>Свойства контекста по русскому названию; названия, которые носят два свойства, не годятся в имя.</summary>
    private Dictionary<string, PropertyDef> AliasMap(string context)
    {
        lock (_aliases)
        {
            if (_aliases.TryGetValue(context, out var map)) return map;
            map = new Dictionary<string, PropertyDef>(StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in PropertiesOf(context).GroupBy(p => p.Id).Select(g => g.Last()))
            {
                if (p.NameRu.Length == 0) continue;
                var k = Key(p.NameRu);
                if (!taken.Add(k)) { map.Remove(k); continue; }
                map[k] = p;
            }
            _aliases[context] = map;
            return map;
        }
    }

    /// <summary>Свойство по русскому названию (<c>Масса ВВ</c>); null — нет такого или название не однозначно.</summary>
    public PropertyDef? FindAlias(string context, string alias) => AliasMap(context).GetValueOrDefault(Key(alias));

    /// <summary>Свойство по кодовому имени или русскому названию.</summary>
    public PropertyDef? Resolve(string context, string name) => FindProperty(context, name) ?? FindAlias(context, name);

    /// <summary>Русское имя свойства для формулы: его название, если оно однозначно в контексте, иначе кодовое имя.</summary>
    public string AliasOf(PropertyDef p) => p.NameRu.Length > 0 && FindAlias(p.Context, p.NameRu) == p ? p.NameRu : p.Name;

    /// <summary>Параметр по id или русскому названию.</summary>
    public CompiledParameter? ParameterByName(string name)
    {
        if (ParameterOf(name) is { } p) return p;
        var named = Parameters.Where(x => Key(x.Def.NameRu) == Key(name)).Take(2).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    /// <summary>Русское имя параметра для формулы: название, если однозначно, иначе id.</summary>
    public string AliasOf(ParameterDef p) => p.NameRu.Length > 0 && ParameterByName(p.NameRu)?.Def == p ? p.NameRu : p.Id;

    /// <summary>Компилирует выражение по свойствам этой модели (предпросмотр, контрольные примеры, проверка при наборе).</summary>
    public CompiledExpression CompileExpression(string context, string text, CalcType? expected = null) => Compiler.Compile(this, context, text, expected);

    /// <summary>Узлы, которые (транзитивно) зависят от данного: «Используют» в редакторе и «Затронет при публикации».</summary>
    public IReadOnlyList<CalcNode> Downstream(string source)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<string>();
        stack.Push(source);
        while (stack.Count > 0)
            foreach (var (n, _) in DependentsOf(stack.Pop()))
                if (seen.Add(n)) stack.Push(n < Nodes.Count ? Nodes[n].Id : "builtin:" + _builtinNames[n - Nodes.Count]);
        return seen.Where(i => i < Nodes.Count).Select(i => Nodes[i]).OrderBy(n => n.Order).ToList();
    }
}
