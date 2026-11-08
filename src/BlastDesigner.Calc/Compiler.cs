using System.Linq.Expressions;
using System.Reflection;
using BlastDesigner.Domain.Geometry;
using E = System.Linq.Expressions.Expression;

namespace BlastDesigner.Calc;

/// <summary>Зависимость узла: источник (<c>hole.length</c>, <c>param:site.vib.K</c>, <c>builtin:cellArea</c>) и путь связей от сущности узла к нему.</summary>
public sealed record Dependency(string Source, IReadOnlyList<PropertyDef> Path)
{
    public string PathText => Path.Count == 0 ? "" : string.Join(".", Path.Select(p => p.Name));
    public override string ToString() => Path.Count == 0 ? Source : $"{Source} ← {PathText}";
}

/// <summary>Скомпилированное выражение: делегат (сеанс, сущность) → значение, тип, единица и зависимости.</summary>
public sealed class CompiledExpression
{
    public string Text { get; init; } = "";
    public string Context { get; init; } = "";
    public CalcType Type { get; init; }
    public Unit Unit { get; init; } = Unit.Unknown;
    public IReadOnlyList<Dependency> Dependencies { get; init; } = Array.Empty<Dependency>();
    /// <summary>Func&lt;CalcSession, object, double | bool | string?&gt;.</summary>
    public Delegate Delegate { get; init; } = null!;

    public Func<CalcSession, object, double> Number => (Func<CalcSession, object, double>)Delegate;
    public Func<CalcSession, object, bool> Bool => (Func<CalcSession, object, bool>)Delegate;
}

/// <summary>
/// Компилятор формул: разбор, разрешение имён по контексту и связям, проверка типов и единиц, построение дерева
/// <see cref="System.Linq.Expressions"/> и <c>Compile()</c>. Семантика пустых значений: свойство несуществующего
/// объекта (интервал без продукта) — NaN для чисел, false, пустая строка, пустой список; NaN в итоге узла — ошибка
/// (или «значение при ошибке» формулы). Единицы: проверяется размерность сложения, сравнения, ветвей и итога;
/// числовые константы подстраиваются под единицу соседнего операнда; степень с нецелым или непостоянным показателем
/// у размерной величины даёт неизвестную единицу (эмпирические зависимости).
/// </summary>
internal sealed class Compiler
{
    private readonly CalcModel _model;
    private readonly ParameterExpression _session = E.Parameter(typeof(CalcSession), "s");
    private readonly ParameterExpression _self = E.Parameter(typeof(object), "o");
    private readonly List<Dependency> _deps = new();
    private ContextDef _root = null!;

    private Compiler(CalcModel model) => _model = model;

    private sealed record Frame(ContextDef Context, E Object, bool MayBeNull, IReadOnlyList<PropertyDef> Path);

    private sealed record Bound(E Expr, CalcType Type, Unit Unit, string? Target = null, Type? EnumType = null, double? Const = null,
        string? Literal = null, IReadOnlyList<PropertyDef>? Path = null, bool IsNull = false);

    public static CompiledExpression Compile(CalcModel model, string context, string text, CalcType? expected = null)
    {
        var c = new Compiler(model);
        var ctx = model.Registry.Context(context) ?? throw new CalcSyntaxException($"Нет контекста «{context}»", 0);
        c._root = ctx;
        var ast = Parser.Parse(text);
        var b = c.Bind(ast, new Frame(ctx, c._self, false, Array.Empty<PropertyDef>()));
        if (expected is { } want && b.Type != want)
        {
            if (want == CalcType.Number && b.Type == CalcType.Bool) b = b with { Expr = E.Condition(b.Expr, E.Constant(1.0), E.Constant(0.0)), Type = CalcType.Number, Unit = Unit.One };
            else if (want == CalcType.String && b.Type == CalcType.Enum) b = c.EnumToString(b);
            else throw new CalcSyntaxException($"Формула даёт {TypeName(b.Type)}, ожидается {TypeName(want)}", ast.Position, ast.Length);
        }
        var body = b.Type switch
        {
            CalcType.Enum => c.EnumToString(b).Expr,
            CalcType.Ref or CalcType.List => throw new CalcSyntaxException("Формула должна давать число, логическое значение или строку", ast.Position, ast.Length),
            _ => b.Expr
        };
        var type = b.Type == CalcType.Enum ? CalcType.String : b.Type;
        var delegateType = typeof(Func<,,>).MakeGenericType(typeof(CalcSession), typeof(object), body.Type);
        var lambda = E.Lambda(delegateType, body, c._session, c._self);
        return new CompiledExpression
        {
            Text = text, Context = context, Type = type, Unit = b.Unit, Delegate = lambda.Compile(),
            Dependencies = c._deps.Distinct(DependencyComparer.Instance).ToList()
        };
    }

    private sealed class DependencyComparer : IEqualityComparer<Dependency>
    {
        public static readonly DependencyComparer Instance = new();
        public bool Equals(Dependency? a, Dependency? b) => a!.Source == b!.Source && a.PathText == b.PathText;
        public int GetHashCode(Dependency d) => HashCode.Combine(d.Source, d.PathText);
    }

    private static string TypeName(CalcType t) => t switch
    {
        CalcType.Number => "число", CalcType.Bool => "логическое значение", CalcType.String => "строку", CalcType.Enum => "вид",
        CalcType.Ref => "ссылку на объект", _ => "список"
    };

    private E Scope => E.Property(_session, nameof(CalcSession.Scope));

    // ---- разбор узлов ---------------------------------------------------

    private Bound Bind(Ast ast, Frame f) => ast switch
    {
        NumberAst n => new Bound(E.Constant(n.Value), CalcType.Number, Unit.One, Const: n.Value),
        StringAst s => new Bound(E.Constant(s.Value, typeof(string)), CalcType.String, Unit.One, Literal: s.Value),
        NameAst n => BindName(n, f),
        UnaryAst u => BindUnary(u, f),
        BinaryAst b => BindBinary(b, f),
        CallAst c => BindCall(c, f),
        WhereAst w => throw Error("«where» допускается только в первом аргументе агрегата (sum, count…)", w),
        _ => throw Error("Неизвестная конструкция", ast)
    };

    private static CalcSyntaxException Error(string message, Ast at) => new(message, at.Position, at.Length);

    private Bound BindName(NameAst n, Frame f)
    {
        if (n.Path.Count == 1)
        {
            switch (n.Path[0].ToLowerInvariant())
            {
                case "pi": return new Bound(E.Constant(Math.PI), CalcType.Number, Unit.One, Const: Math.PI);
                case "inf": return new Bound(E.Constant(double.PositiveInfinity), CalcType.Number, Unit.One, Const: double.PositiveInfinity);
                case "true": return new Bound(E.Constant(true), CalcType.Bool, Unit.One);
                case "false": return new Bound(E.Constant(false), CalcType.Bool, Unit.One);
                case "null": return new Bound(E.Constant(null, typeof(object)), CalcType.Ref, Unit.One, IsNull: true);
            }
        }
        var ctx = f.Context;
        E obj = f.Object;
        bool mayBeNull = f.MayBeNull;
        var path = f.Path.ToList();
        Bound? result = null;
        for (int i = 0; i < n.Path.Count; i++)
        {
            var name = n.Path[i];
            if (result != null)
            {
                if (result.Type == CalcType.List) throw Error($"«{n.Path[i - 1]}» — список; используйте sum, count, first…", n);
                if (result.Type != CalcType.Ref) throw Error($"«{n.Path[i - 1]}» — не объект, у него нет свойства «{name}»", n);
                ctx = _model.Registry.Context(result.Target!)!;
                obj = result.Expr;
                mayBeNull = true;
                path.Add(LastRelation!);
            }
            var prop = _model.FindProperty(ctx.Name, name)
                       ?? throw Error($"Нет свойства «{name}» у «{ctx.Name}»{Suggest(ctx, name)}", n);
            result = Access(prop, obj, mayBeNull, path);
            LastRelation = prop;
        }
        return result! with { Path = result!.Type is CalcType.List or CalcType.Ref ? path.Append(LastRelation!).ToList() : null };
    }

    private PropertyDef? LastRelation;

    private string Suggest(ContextDef ctx, string name)
    {
        var all = _model.PropertiesOf(ctx.Name).Select(p => p.Name).ToList();
        var best = all.Where(a => a.StartsWith(name, StringComparison.OrdinalIgnoreCase) || a.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
        return best.Count > 0 ? $" (может быть: {string.Join(", ", best)})" : "";
    }

    /// <summary>Чтение свойства объекта с учётом пустой ссылки; записывает зависимость.</summary>
    private Bound Access(PropertyDef p, E obj, bool mayBeNull, List<PropertyDef> path)
    {
        _deps.Add(new Dependency(p.Id, path.ToList()));
        var unit = Unit.TryParse(p.Unit, out var u) ? u : Unit.Unknown;
        E Read(E o)
        {
            if (p.Kind == PropertyKind.Input)
            {
                var getter = p.Getter ?? throw new InvalidOperationException($"У поля {p.Id} нет чтения");
                return E.Invoke(E.Constant(getter), Scope, o);
            }
            var node = _model.NodeOf(p.Id) ?? throw new InvalidOperationException($"Нет узла {p.Id}");
            var method = p.Type switch
            {
                CalcType.Bool => nameof(CalcSession.GetBool),
                CalcType.String => nameof(CalcSession.GetString),
                _ => nameof(CalcSession.GetNumber)
            };
            return E.Call(_session, typeof(CalcSession).GetMethod(method)!, E.Constant(node.Index), o);
        }
        E expr;
        if (!mayBeNull) expr = Read(obj);
        else
        {
            var t = E.Variable(typeof(object), "t");
            var read = Read(t);
            expr = E.Block(read.Type, new[] { t }, E.Assign(t, obj), E.Condition(E.ReferenceEqual(t, E.Constant(null)), Default(p.Type, read.Type), read));
        }
        return new Bound(expr, p.Type, unit, p.Target, p.EnumType);
    }

    private static E Default(CalcType t, Type clr) => t switch
    {
        CalcType.Number => E.Constant(double.NaN),
        CalcType.Bool => E.Constant(false),
        CalcType.Enum => E.Constant(-1),
        CalcType.List => E.Constant(Array.Empty<object>(), clr),
        _ => E.Constant(null, clr)
    };

    private Bound BindUnary(UnaryAst u, Frame f)
    {
        var x = Bind(u.Operand, f);
        if (u.Op == "not")
        {
            RequireBool(x, u.Operand);
            return new Bound(E.Not(x.Expr), CalcType.Bool, Unit.One);
        }
        RequireNumber(x, u.Operand);
        return x with { Expr = E.Negate(x.Expr), Const = -x.Const };
    }

    private void RequireNumber(Bound b, Ast at)
    {
        if (b.Type != CalcType.Number) throw Error($"Ожидается число, а здесь {TypeName(b.Type)}", at);
    }

    private void RequireBool(Bound b, Ast at)
    {
        if (b.Type != CalcType.Bool) throw Error($"Ожидается условие (логическое значение), а здесь {TypeName(b.Type)}", at);
    }

    private Bound BindBinary(BinaryAst b, Frame f)
    {
        if (b.Op is "and" or "or")
        {
            var l = Bind(b.Left, f);
            var r = Bind(b.Right, f);
            RequireBool(l, b.Left);
            RequireBool(r, b.Right);
            return new Bound(b.Op == "and" ? E.AndAlso(l.Expr, r.Expr) : E.OrElse(l.Expr, r.Expr), CalcType.Bool, Unit.One);
        }
        var left = Bind(b.Left, f);
        var right = Bind(b.Right, f);
        if (b.Op is "=" or "<>" or "<" or "<=" or ">" or ">=") return Compare(b, left, right);
        if (b.Op == "+" && left.Type == CalcType.String && right.Type == CalcType.String)
            return new Bound(E.Call(typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string) })!, left.Expr, right.Expr), CalcType.String, Unit.One);
        RequireNumber(left, b.Left);
        RequireNumber(right, b.Right);
        switch (b.Op)
        {
            case "+":
            case "-":
            {
                var unit = Additive(left, right, b);
                var e = b.Op == "+" ? E.Add(left.Expr, right.Expr) : E.Subtract(left.Expr, right.Expr);
                return new Bound(e, CalcType.Number, unit, Const: left.Const is { } lc && right.Const is { } rc ? (b.Op == "+" ? lc + rc : lc - rc) : null);
            }
            case "*":
                return new Bound(E.Multiply(left.Expr, right.Expr), CalcType.Number, left.Unit.Multiply(right.Unit), Const: left.Const * right.Const);
            case "/":
                return new Bound(E.Divide(left.Expr, right.Expr), CalcType.Number, left.Unit.Divide(right.Unit), Const: left.Const / right.Const);
            case "%":
                return new Bound(E.Modulo(left.Expr, right.Expr), CalcType.Number, Additive(left, right, b));
            case "^":
            {
                Unit unit;
                if (left.Unit.IsDimensionless || left.Unit.IsUnknown) unit = left.Unit;
                else if (right.Const is { } n && Math.Abs(n - Math.Round(n)) < 1e-12) unit = left.Unit.Pow(Math.Round(n));
                else unit = Unit.Unknown;
                if (!right.Unit.IsDimensionless && !right.Unit.IsUnknown) throw Error($"Показатель степени должен быть безразмерным, а он в {right.Unit}", b.Right);
                E e = right.Const == 2 ? Square(left.Expr) : E.Call(typeof(Math).GetMethod(nameof(Math.Pow))!, left.Expr, right.Expr);
                return new Bound(e, CalcType.Number, unit, Const: left.Const is { } bc && right.Const is { } ec ? Math.Pow(bc, ec) : null);
            }
        }
        throw Error($"Неизвестная операция «{b.Op}»", b);
    }

    /// <summary>x^2 как Math.Pow(x, 2): Pow(x, 2) в .NET точно равен x·x, вызов оставлен для единообразия.</summary>
    private static E Square(E x) => E.Call(typeof(Math).GetMethod(nameof(Math.Pow))!, x, E.Constant(2.0));

    /// <summary>Единица суммы: одинаковая размерность; константа подстраивается под другой операнд; разный масштаб — ошибка (нужен to()).</summary>
    private static Unit Additive(Bound l, Bound r, Ast at)
    {
        if (l.Const != null && l.Unit.IsDimensionless) return r.Unit;
        if (r.Const != null && r.Unit.IsDimensionless) return l.Unit;
        if (l.Unit.IsUnknown) return r.Unit;
        if (r.Unit.IsUnknown) return l.Unit;
        if (!l.Unit.SameDimension(r.Unit))
            throw new CalcSyntaxException($"Несовместимые единицы: {l.Unit} и {r.Unit}", at.Position, at.Length);
        if (Math.Abs(l.Unit.Scale - r.Unit.Scale) > 1e-12 * Math.Abs(l.Unit.Scale))
            throw new CalcSyntaxException($"Разные единицы {l.Unit} и {r.Unit}: приведите явно, например to({l.Unit.Symbol}, …)", at.Position, at.Length);
        return l.Unit;
    }

    private Bound Compare(BinaryAst b, Bound l, Bound r)
    {
        E Op(E a, E c) => b.Op switch
        {
            "=" => E.Equal(a, c), "<>" => E.NotEqual(a, c), "<" => E.LessThan(a, c), "<=" => E.LessThanOrEqual(a, c),
            ">" => E.GreaterThan(a, c), _ => E.GreaterThanOrEqual(a, c)
        };
        bool equality = b.Op is "=" or "<>";
        // вид продукта со строкой: 'GasBag' проверяется при компиляции и сравнивается как число
        if (l.Type == CalcType.Enum && r.Literal != null) return EnumCompare(b, l, r.Literal, b.Right);
        if (r.Type == CalcType.Enum && l.Literal != null) return EnumCompare(b, r, l.Literal, b.Left);
        if (l.Type == CalcType.Enum && r.Type == CalcType.Enum && equality) return new Bound(Op(l.Expr, r.Expr), CalcType.Bool, Unit.One);
        if (l.Type == CalcType.Number && r.Type == CalcType.Number)
        {
            Additive(l, r, b);
            return new Bound(Op(l.Expr, r.Expr), CalcType.Bool, Unit.One);
        }
        if (!equality) throw Error($"Сравнение «{b.Op}» — только для чисел", b);
        if (l.Type == CalcType.Enum) l = EnumToString(l);
        if (r.Type == CalcType.Enum) r = EnumToString(r);
        if (l.Type == CalcType.String && r.Type == CalcType.String)
        {
            var eq = E.Call(typeof(string).GetMethod(nameof(string.Equals), new[] { typeof(string), typeof(string), typeof(StringComparison) })!,
                l.Expr, r.Expr, E.Constant(StringComparison.OrdinalIgnoreCase));
            return new Bound(b.Op == "=" ? eq : E.Not(eq), CalcType.Bool, Unit.One);
        }
        if (l.Type == CalcType.Bool && r.Type == CalcType.Bool) return new Bound(Op(l.Expr, r.Expr), CalcType.Bool, Unit.One);
        if (l.Type == CalcType.Ref && r.Type == CalcType.Ref)
        {
            var eq = E.ReferenceEqual(l.Expr, r.Expr);
            return new Bound(b.Op == "=" ? eq : E.Not(eq), CalcType.Bool, Unit.One);
        }
        throw Error($"Нельзя сравнить {TypeName(l.Type)} и {TypeName(r.Type)}", b);
    }

    private Bound EnumCompare(BinaryAst b, Bound e, string literal, Ast at)
    {
        if (b.Op is not ("=" or "<>")) throw Error("Вид сравнивается только на равенство", b);
        if (!Enum.TryParse(e.EnumType!, literal, true, out var value) || !Enum.IsDefined(e.EnumType!, value!))
            throw Error($"Нет значения «{literal}»; возможны: {string.Join(", ", Enum.GetNames(e.EnumType!))}", at);
        var c = E.Constant(Convert.ToInt32(value));
        return new Bound(b.Op == "=" ? E.Equal(e.Expr, c) : E.NotEqual(e.Expr, c), CalcType.Bool, Unit.One);
    }

    private Bound EnumToString(Bound e)
    {
        var names = Enum.GetValues(e.EnumType!).Cast<object>().ToDictionary(v => Convert.ToInt32(v), v => v.ToString()!);
        Func<int, string?> f = i => names.GetValueOrDefault(i);
        return new Bound(E.Invoke(E.Constant(f), e.Expr), CalcType.String, Unit.One);
    }

    // ---- функции --------------------------------------------------------

    private static readonly HashSet<string> Aggregates = new(StringComparer.OrdinalIgnoreCase) { "sum", "avg", "count", "first" };

    private Bound BindCall(CallAst c, Frame f)
    {
        var name = c.Name.ToLowerInvariant();
        var args = c.Args;
        if (Aggregates.Contains(name) || ((name is "min" or "max") && args.Count is 1 or 2 && IsListArgument(args[0], f)))
            return BindAggregate(name, c, f);
        switch (name)
        {
            case "if":
            {
                Arity(c, 3);
                var cond = Bind(args[0], f);
                RequireBool(cond, args[0]);
                var (a, b, unit) = Unify(Bind(args[1], f), Bind(args[2], f), c);
                return new Bound(E.Condition(cond.Expr, a.Expr, b.Expr), a.Type, unit, a.Target ?? b.Target, a.EnumType ?? b.EnumType);
            }
            case "case":
            {
                if (args.Count < 3 || args.Count % 2 == 0) throw Error("case(условие1, значение1, …, иначе): нечётное число аргументов, не меньше трёх", c);
                var result = Bind(args[^1], f);
                for (int i = args.Count - 3; i >= 0; i -= 2)
                {
                    var cond = Bind(args[i], f);
                    RequireBool(cond, args[i]);
                    var (a, b, unit) = Unify(Bind(args[i + 1], f), result, c);
                    result = new Bound(E.Condition(cond.Expr, a.Expr, b.Expr), a.Type, unit, a.Target ?? b.Target, a.EnumType ?? b.EnumType);
                }
                return result;
            }
            case "coalesce":
            {
                if (args.Count < 2) throw Error("coalesce(a, b, …): не меньше двух аргументов", c);
                var result = Bind(args[^1], f);
                for (int i = args.Count - 2; i >= 0; i--)
                {
                    var (a, b, unit) = Unify(Bind(args[i], f), result, c);
                    var t = E.Variable(a.Expr.Type, "c");
                    E test = a.Type == CalcType.Number ? E.Call(typeof(double).GetMethod(nameof(double.IsNaN), new[] { typeof(double) })!, t)
                        : a.Type == CalcType.Enum ? E.Equal(t, E.Constant(-1))
                        : a.Type == CalcType.Bool ? E.Constant(false)
                        : E.ReferenceEqual(t, E.Constant(null));
                    result = new Bound(E.Block(a.Expr.Type, new[] { t }, E.Assign(t, a.Expr), E.Condition(test, b.Expr, t)), a.Type, unit, a.Target, a.EnumType);
                }
                return result;
            }
            case "exists":
            {
                Arity(c, 1);
                var x = Bind(args[0], f);
                E e = x.Type switch
                {
                    CalcType.Number => E.Not(E.Call(typeof(double).GetMethod(nameof(double.IsNaN), new[] { typeof(double) })!, x.Expr)),
                    CalcType.List => E.GreaterThan(E.Property(x.Expr, typeof(IReadOnlyCollection<object>).GetProperty("Count")!), E.Constant(0)),
                    CalcType.Enum => E.NotEqual(x.Expr, E.Constant(-1)),
                    CalcType.Bool => E.Constant(true),
                    _ => E.Not(E.ReferenceEqual(x.Expr, E.Constant(null)))
                };
                return new Bound(e, CalcType.Bool, Unit.One);
            }
            case "min":
            case "max":
            {
                if (args.Count < 2) throw Error($"{name}(a, b, …) или {name}(список, выражение)", c);
                var acc = Bind(args[0], f);
                RequireNumber(acc, args[0]);
                for (int i = 1; i < args.Count; i++)
                {
                    var x = Bind(args[i], f);
                    RequireNumber(x, args[i]);
                    var unit = Additive(acc, x, c);
                    acc = new Bound(E.Call(typeof(Math).GetMethod(name == "min" ? nameof(Math.Min) : nameof(Math.Max), new[] { typeof(double), typeof(double) })!, acc.Expr, x.Expr),
                        CalcType.Number, unit, Const: acc.Const is { } a && x.Const is { } b ? (name == "min" ? Math.Min(a, b) : Math.Max(a, b)) : null);
                }
                return acc;
            }
            case "clamp":
            {
                Arity(c, 3);
                var x = Number(args[0], f);
                var lo = Number(args[1], f);
                var hi = Number(args[2], f);
                var unit = Additive(x, lo, c);
                Additive(x, hi, c);
                return new Bound(E.Call(typeof(Math).GetMethod(nameof(Math.Clamp), new[] { typeof(double), typeof(double), typeof(double) })!, x.Expr, lo.Expr, hi.Expr), CalcType.Number, unit);
            }
            case "round":
            {
                if (args.Count is < 1 or > 2) throw Error("round(x) или round(x, знаков)", c);
                var x = Number(args[0], f);
                E digits = args.Count == 2 ? E.Convert(Number(args[1], f).Expr, typeof(int)) : E.Constant(0);
                return x with { Expr = E.Call(typeof(Math).GetMethod(nameof(Math.Round), new[] { typeof(double), typeof(int), typeof(MidpointRounding) })!, x.Expr, digits, E.Constant(MidpointRounding.AwayFromZero)), Const = null };
            }
            case "floor": return Math1(c, f, nameof(Math.Floor), keepUnit: true);
            case "ceil": return Math1(c, f, nameof(Math.Ceiling), keepUnit: true);
            case "abs": return Math1(c, f, nameof(Math.Abs), keepUnit: true);
            case "sqrt": { var b = Math1(c, f, nameof(Math.Sqrt), keepUnit: true); return b with { Unit = Bind(args[0], f).Unit.Pow(0.5) }; }
            case "cbrt": { var b = Math1(c, f, nameof(Math.Cbrt), keepUnit: true); return b with { Unit = Bind(args[0], f).Unit.Pow(1.0 / 3) }; }
            case "log10": return Math1(c, f, nameof(Math.Log10));
            case "ln": return Math1(c, f, nameof(Math.Log));
            case "exp": return Math1(c, f, nameof(Math.Exp));
            case "sin": return Trig(c, f, nameof(Math.Sin));
            case "cos": return Trig(c, f, nameof(Math.Cos));
            case "tan": return Trig(c, f, nameof(Math.Tan));
            case "pi": Arity(c, 0); return new Bound(E.Constant(Math.PI), CalcType.Number, Unit.One, Const: Math.PI);
            case "to": return BindTo(c, f);
            case "param": return BindParam(c, f, lookup: false);
            case "lookup": return BindParam(c, f, lookup: true);
        }
        if (_model.Registry.Builtins.TryGetValue(c.Name, out var builtin)) return BindBuiltin(builtin, c, f);
        if (_model.Registry.Functions.TryGetValue(c.Name, out var fn))
        {
            Arity(c, fn.ArgCount);
            var arr = E.NewArrayInit(typeof(double), args.Select(a => Number(a, f).Expr));
            return new Bound(E.Invoke(E.Constant(fn.Invoke), arr), CalcType.Number, Unit.TryParse(fn.Unit, out var u) ? u : Unit.Unknown);
        }
        throw Error($"Неизвестная функция «{c.Name}»", c);
    }

    private static void Arity(CallAst c, int n)
    {
        if (c.Args.Count != n) throw Error($"{c.Name}: нужно аргументов — {n}, а дано {c.Args.Count}", c);
    }

    private Bound Number(Ast a, Frame f)
    {
        var b = Bind(a, f);
        RequireNumber(b, a);
        return b;
    }

    private Bound Math1(CallAst c, Frame f, string method, bool keepUnit = false)
    {
        Arity(c, 1);
        var x = Number(c.Args[0], f);
        if (!keepUnit && !x.Unit.IsDimensionless && !x.Unit.IsUnknown) throw Error($"{c.Name}: аргумент должен быть безразмерным, а он в {x.Unit}", c.Args[0]);
        var m = typeof(Math).GetMethod(method, new[] { typeof(double) })!;
        return new Bound(E.Call(m, x.Expr), CalcType.Number, keepUnit ? x.Unit : Unit.One, Const: x.Const is { } v ? (double?)m.Invoke(null, new object[] { v }) : null);
    }

    /// <summary>Тригонометрия в градусах: cos(angle) = Math.Cos(angle·π/180), как в GeoMath.</summary>
    private Bound Trig(CallAst c, Frame f, string method)
    {
        Arity(c, 1);
        var x = Number(c.Args[0], f);
        if (!x.Unit.IsDimensionless && !x.Unit.IsUnknown) throw Error($"{c.Name}: аргумент — угол в градусах, а он в {x.Unit}", c.Args[0]);
        var m = typeof(Math).GetMethod(method, new[] { typeof(double) })!;
        return new Bound(E.Call(m, E.Multiply(x.Expr, E.Constant(GeoMath.Deg))), CalcType.Number, Unit.One);
    }

    private (Bound, Bound, Unit) Unify(Bound a, Bound b, Ast at)
    {
        if (a.Type == CalcType.Number && b.Type == CalcType.Number) return (a, b, Additive(a, b, at));
        if (a.Type == CalcType.Enum && b.Type == CalcType.String) a = EnumToString(a);
        if (b.Type == CalcType.Enum && a.Type == CalcType.String) b = EnumToString(b);
        if (a.Type == CalcType.Ref && b.IsNull) return (a, b with { Expr = E.Constant(null, a.Expr.Type) }, Unit.One);
        if (b.Type == CalcType.Ref && a.IsNull) return (a with { Expr = E.Constant(null, b.Expr.Type) }, b, Unit.One);
        if (a.Type != b.Type) throw Error($"Ветви дают разные типы: {TypeName(a.Type)} и {TypeName(b.Type)}", at);
        if (a.Type == CalcType.Ref && a.Target != b.Target) throw Error($"Ветви ведут к разным объектам: {a.Target} и {b.Target}", at);
        if (a.Expr.Type != b.Expr.Type) b = b with { Expr = E.Convert(b.Expr, a.Expr.Type) };
        return (a, b, Unit.One);
    }

    private Bound BindTo(CallAst c, Frame f)
    {
        Arity(c, 2);
        var unitText = c.Args[0] switch
        {
            NameAst n => n.Text,
            StringAst s => s.Value,
            _ => throw Error("to(единица, выражение): единица — имя или строка, например to(m, diameter)", c.Args[0])
        };
        if (!Unit.TryParse(unitText, out var target) || target.IsUnknown) throw Error($"Неизвестная единица «{unitText}»", c.Args[0]);
        var x = Number(c.Args[1], f);
        if (x.Unit.IsUnknown) throw Error("to(): у выражения неизвестная единица, переводить не из чего", c.Args[1]);
        if (!x.Unit.SameDimension(target)) throw Error($"Нельзя перевести {x.Unit} в {target}: разная размерность", c);
        var factor = x.Unit.FactorTo(target);
        E e = x.Expr;
        if (Math.Abs(factor - 1) > 1e-15)
        {
            // мм → м делением на 1000, а не умножением на 0.001: так совпадает с кодом (Diameter / 2000.0)
            var inverse = 1 / factor;
            e = factor < 1 && Math.Abs(inverse - Math.Round(inverse)) < 1e-9 * inverse ? E.Divide(x.Expr, E.Constant(Math.Round(inverse))) : E.Multiply(x.Expr, E.Constant(factor));
        }
        return new Bound(e, CalcType.Number, target, Const: x.Const * factor);
    }

    private Bound BindParam(CallAst c, Frame f, bool lookup)
    {
        if (lookup ? c.Args.Count != 2 : c.Args.Count != 1) throw Error(lookup ? "lookup(таблица, x)" : "param(имя), например param(site.vib.K)", c);
        var id = c.Args[0] switch
        {
            NameAst n => n.Text,
            StringAst s => s.Value,
            _ => throw Error("Имя параметра — через точку или в кавычках", c.Args[0])
        };
        var p = _model.ParameterOf(id) ?? throw Error($"Нет параметра «{id}»", c.Args[0]);
        if (lookup && p.Def.Table is not { Count: > 0 }) throw Error($"У параметра «{id}» нет таблицы", c.Args[0]);
        if (!lookup && p.Def.Table is { Count: > 0 }) throw Error($"«{id}» — таблица, используйте lookup({id}, x)", c.Args[0]);
        _deps.Add(new Dependency("param:" + p.Def.Id, BlastPath()));
        var unit = Unit.TryParse(p.Def.Unit, out var u) ? u : Unit.Unknown;
        if (lookup)
        {
            var x = Number(c.Args[1], f);
            return new Bound(E.Call(_session, typeof(CalcSession).GetMethod(nameof(CalcSession.Lookup))!, E.Constant(p.Index), x.Expr, f.Object), CalcType.Number, unit);
        }
        return new Bound(E.Call(_session, typeof(CalcSession).GetMethod(nameof(CalcSession.Param))!, E.Constant(p.Index), f.Object), CalcType.Number, unit);
    }

    /// <summary>Путь от корневой сущности формулы к взрыву (источники уровня взрыва: параметры, встроенные алгоритмы).</summary>
    private IReadOnlyList<PropertyDef> BlastPath() =>
        _root.Name == "blast" ? Array.Empty<PropertyDef>() : new[] { _model.Registry.Property(_root.Name + ".blast")! };

    private Bound BindBuiltin(BuiltinDef b, CallAst c, Frame f)
    {
        if (b.Contexts.Length > 0 && !b.Contexts.Contains(f.Context.Name))
            throw Error($"{b.Name}() доступна в контекстах: {string.Join(", ", b.Contexts)}", c);
        Arity(c, b.ArgCount);
        var args = c.Args.Select(a => a switch
        {
            NameAst n when n.Path.Count == 1 => (object)n.Path[0],
            StringAst s => s.Value,
            NumberAst n => n.Value,
            _ => throw Error($"{b.Name}(): аргументы — константы (имя, строка или число)", a)
        }).ToArray();
        var index = _model.BuiltinIndex(b.Name);
        _deps.Add(new Dependency("builtin:" + b.Name, BlastPath()));
        if (b.Scope == BuiltinScope.Entity)
            foreach (var input in b.Inputs.Where(i => i.StartsWith(f.Context.Name + ".", StringComparison.Ordinal)))
                _deps.Add(new Dependency(input, f.Path.ToList()));
        var unit = Unit.TryParse(b.Unit, out var u) ? u : Unit.Unknown;
        return new Bound(E.Call(_session, typeof(CalcSession).GetMethod(nameof(CalcSession.Builtin))!, E.Constant(index), f.Object, E.Constant(args)), CalcType.Number, unit);
    }

    private bool IsListArgument(Ast a, Frame f)
    {
        if (a is WhereAst) return true;
        if (a is not NameAst n) return false;
        try
        {
            var saved = _deps.Count;
            var b = BindName(n, f);
            _deps.RemoveRange(saved, _deps.Count - saved);
            return b.Type == CalcType.List;
        }
        catch (CalcSyntaxException) { return false; }
    }

    private Bound BindAggregate(string name, CallAst c, Frame f)
    {
        if (c.Args.Count == 0) throw Error($"{name}(список [where условие], выражение)", c);
        Ast sourceAst = c.Args[0];
        Ast? filterAst = null;
        if (sourceAst is WhereAst w) { sourceAst = w.Source; filterAst = w.Filter; }
        var source = Bind(sourceAst, f);
        if (source.Type != CalcType.List) throw Error($"{name}: первый аргумент — список (decks, holes…), а здесь {TypeName(source.Type)}", sourceAst);
        var target = _model.Registry.Context(source.Target!)!;
        var item = E.Parameter(typeof(object), "it");
        var frame = new Frame(target, item, false, source.Path ?? f.Path);
        E? filter = null;
        if (filterAst != null)
        {
            var fb = Bind(filterAst, frame);
            RequireBool(fb, filterAst);
            filter = E.Lambda<Func<object, bool>>(fb.Expr, item);
        }
        var filterExpr = filter ?? (E)E.Constant(null, typeof(Func<object, bool>));
        if (name == "count")
        {
            if (c.Args.Count != 1) throw Error("count(список [where условие])", c);
            return new Bound(E.Call(typeof(Agg).GetMethod(nameof(Agg.Count))!, source.Expr, filterExpr), CalcType.Number, Unit.One);
        }
        if (c.Args.Count != 2) throw Error($"{name}(список [where условие], выражение)", c);
        var sel = Bind(c.Args[1], frame);
        if (name == "first")
        {
            if (sel.Type == CalcType.Number)
                return new Bound(E.Call(typeof(Agg).GetMethod(nameof(Agg.First))!, source.Expr, filterExpr, E.Lambda<Func<object, double>>(sel.Expr, item)), CalcType.Number, sel.Unit);
            if (sel.Type is CalcType.Ref or CalcType.String)
            {
                var m = typeof(Agg).GetMethod(nameof(Agg.FirstObject))!.MakeGenericMethod(sel.Expr.Type);
                var lambda = E.Lambda(typeof(Func<,>).MakeGenericType(typeof(object), sel.Expr.Type), sel.Expr, item);
                return new Bound(E.Call(m, source.Expr, filterExpr, lambda), sel.Type, Unit.One, sel.Target);
            }
            throw Error("first: выражение — число, строка или объект", c.Args[1]);
        }
        RequireNumber(sel, c.Args[1]);
        var selector = E.Lambda<Func<object, double>>(sel.Expr, item);
        var method = name switch { "sum" => nameof(Agg.Sum), "avg" => nameof(Agg.Avg), "min" => nameof(Agg.Min), _ => nameof(Agg.Max) };
        return new Bound(E.Call(typeof(Agg).GetMethod(method)!, source.Expr, filterExpr, selector), CalcType.Number, sel.Unit);
    }
}

/// <summary>Агрегаты над списками связей. Пустой список: сумма и количество — 0, среднее, минимум, максимум и first — NaN.</summary>
public static class Agg
{
    public static double Sum(IReadOnlyList<object> items, Func<object, bool>? filter, Func<object, double> sel)
    {
        double s = 0;
        for (int i = 0; i < items.Count; i++)
            if (filter == null || filter(items[i])) s += sel(items[i]);
        return s;
    }

    public static double Count(IReadOnlyList<object> items, Func<object, bool>? filter)
    {
        if (filter == null) return items.Count;
        int n = 0;
        for (int i = 0; i < items.Count; i++) if (filter(items[i])) n++;
        return n;
    }

    public static double Avg(IReadOnlyList<object> items, Func<object, bool>? filter, Func<object, double> sel)
    {
        double s = 0;
        int n = 0;
        for (int i = 0; i < items.Count; i++)
            if (filter == null || filter(items[i])) { s += sel(items[i]); n++; }
        return n > 0 ? s / n : double.NaN;
    }

    public static double Min(IReadOnlyList<object> items, Func<object, bool>? filter, Func<object, double> sel)
    {
        double m = double.NaN;
        for (int i = 0; i < items.Count; i++)
            if (filter == null || filter(items[i])) { var v = sel(items[i]); if (double.IsNaN(m) || v < m) m = v; }
        return m;
    }

    public static double Max(IReadOnlyList<object> items, Func<object, bool>? filter, Func<object, double> sel)
    {
        double m = double.NaN;
        for (int i = 0; i < items.Count; i++)
            if (filter == null || filter(items[i])) { var v = sel(items[i]); if (double.IsNaN(m) || v > m) m = v; }
        return m;
    }

    public static double First(IReadOnlyList<object> items, Func<object, bool>? filter, Func<object, double> sel)
    {
        for (int i = 0; i < items.Count; i++)
            if (filter == null || filter(items[i])) return sel(items[i]);
        return double.NaN;
    }

    public static T? FirstObject<T>(IReadOnlyList<object> items, Func<object, bool>? filter, Func<object, T> sel) where T : class
    {
        for (int i = 0; i < items.Count; i++)
            if (filter == null || filter(items[i])) return sel(items[i]);
        return null;
    }
}
