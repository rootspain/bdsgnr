using System.Globalization;

namespace BlastDesigner.Calc;

/// <summary>Шаг объяснения: свойство, применённая формула, она же с подставленными значениями и итог.</summary>
public sealed record ExplainStep(int Depth, string Property, string Caption, string Formula, string Substituted, string Result, string? Condition);

/// <summary>
/// «Объяснить значение»: формула свойства сущности по-русски, затем с числами и единицами вместо свойств, агрегатов,
/// параметров и встроенных алгоритмов, затем итог; вычисляемые свойства той же сущности и связанных по ссылке
/// объясняются следующими шагами (до заданной глубины).
/// </summary>
public static class CalcExplain
{
    public static List<ExplainStep> Explain(CalcSession session, string propertyId, object entity, int maxDepth = 3)
    {
        var steps = new List<ExplainStep>();
        var seen = new HashSet<(string, object)>();
        Walk(session, propertyId, entity, 0, maxDepth, steps, seen);
        return steps;
    }

    private static void Walk(CalcSession s, string id, object entity, int depth, int maxDepth, List<ExplainStep> steps, HashSet<(string, object)> seen)
    {
        var model = s.Model;
        var node = model.NodeOf(id);
        if (node == null || !seen.Add((id, entity))) return;
        var ctx = node.Context.Name;
        var value = s.Value(node, entity);
        var formula = ActiveFormula(s, node, entity);
        if (formula?.Value == null)
        {
            steps.Add(new ExplainStep(depth, id, Caption(node.Property), "", "", Format(model, value, node.Property.Unit), null));
            return;
        }
        Ast ast;
        try { ast = Parser.Parse(formula.Def.Expression); }
        catch (CalcSyntaxException) { return; }
        var children = new List<(string Id, object Entity)>();
        string? Substitute(Ast a, string at)
        {
            if (at != ctx) return null;
            switch (a)
            {
                case NameAst n:
                {
                    var (prop, target) = Follow(model, ctx, n, s, entity);
                    if (prop == null || prop.IsRelation) return null;
                    if (prop.Kind == PropertyKind.Computed && target != null) children.Add((prop.Id, target));
                    return Evaluate(s, ctx, a, entity, prop.Unit);
                }
                case CallAst c when FormulaText.IsAggregate(model, c, ctx):
                case CallAst c2 when Aliases.Function(c2.Name).ToLowerInvariant() is "param" or "lookup":
                case CallAst c3 when model.Registry.Builtins.ContainsKey(Aliases.Function(c3.Name)):
                {
                    var text = FormulaText.Render(model, ctx, a, true, (_, _) => null);
                    return "{" + text + " = " + Evaluate(s, ctx, a, entity, null) + "}";
                }
            }
            return null;
        }
        var ru = FormulaText.ToRussian(model, ctx, formula.Def.Expression);
        var substituted = FormulaText.Render(model, ctx, ast, true, Substitute);
        var condition = formula.Def.Condition is { Length: > 0 } c ? FormulaText.ToRussian(model, ctx, c) : null;
        steps.Add(new ExplainStep(depth, id, Caption(node.Property), ru, substituted, Format(model, value, node.Property.Unit), condition));
        if (depth + 1 > maxDepth) return;
        foreach (var (child, target) in children.Distinct()) Walk(s, child, target, depth + 1, maxDepth, steps, seen);
    }

    /// <summary>Формула, которая дала значение: первая по приоритету с выполненным условием.</summary>
    private static CompiledFormula? ActiveFormula(CalcSession s, CalcNode node, object entity)
    {
        foreach (var f in node.Formulas)
        {
            if (f.Value == null) continue;
            try { if (f.Condition == null || f.Condition.Bool(s, entity)) return f; }
            catch (Exception) { return f; }
        }
        return null;
    }

    /// <summary>Свойство в конце пути имени и сущность, которой оно принадлежит (через ссылки N:1).</summary>
    private static (PropertyDef?, object?) Follow(CalcModel model, string ctx, NameAst n, CalcSession s, object entity)
    {
        string? cur = ctx;
        object? obj = entity;
        PropertyDef? p = null;
        for (int i = 0; i < n.Path.Count; i++)
        {
            p = cur == null ? null : model.Resolve(cur, n.Path[i]);
            if (p == null) return (null, null);
            if (i < n.Path.Count - 1)
            {
                if (p.Type != CalcType.Ref || obj == null) return (null, null);
                obj = ((Func<CalcScope, object, object?>)p.Getter!)(s.Scope, obj);
                cur = p.Target;
            }
        }
        return (p, obj);
    }

    private static string Evaluate(CalcSession s, string ctx, Ast a, object entity, string? unit)
    {
        try
        {
            var text = FormulaText.Render(s.Model, ctx, a, false, (_, _) => null);
            var compiled = s.Model.CompileExpression(ctx, text);
            var v = s.Evaluate(compiled, entity);
            return Format(s.Model, v, unit ?? (compiled.Unit.IsUnknown ? "?" : compiled.Unit.Symbol));
        }
        catch (CalcSyntaxException e) { return "#" + e.Message; }
    }

    private static string Caption(PropertyDef p) => p.NameRu.Length > 0 ? p.NameRu : p.Name;

    private static string Format(CalcModel model, CalcValue v, string unit)
    {
        if (v.IsError) return "#" + v.Error;
        if (v.Object is bool b) return b ? "истина" : "ложь";
        if (v.Object is string str) return "'" + str + "'";
        var number = double.IsPositiveInfinity(v.Number) ? "∞" : v.Number.ToString(Math.Abs(v.Number) >= 100 ? "0.#" : "0.###", CultureInfo.CurrentCulture);
        var u = Aliases.RussianUnit(unit);
        return u is "?" or "1" or "" ? number : number + " " + u;
    }
}
