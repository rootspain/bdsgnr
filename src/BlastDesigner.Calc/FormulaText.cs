using System.Globalization;
using System.Text;

namespace BlastDesigner.Calc;

/// <summary>
/// Запись формулы для человека и для хранения: <see cref="ToRussian"/> показывает свойства русскими названиями в скобках
/// и функции по-русски (<c>сумма([Интервалы] где [Продукт].[ВВ], [Масса])</c>), <see cref="ToCode"/> возвращает
/// кодовую запись, которая хранится в профиле (<c>sum(decks where product.isExplosive, mass)</c>). Обе записи разбирает
/// один парсер; перевод идёт по дереву разбора с учётом контекста (агрегат переводит выражение в контекст элемента).
/// </summary>
public static class FormulaText
{
    public static string ToRussian(CalcModel model, string context, string text) => Render(model, context, text, true);
    public static string ToCode(CalcModel model, string context, string text) => Render(model, context, text, false);

    /// <summary>Перевод; текст с синтаксической ошибкой возвращается как есть.</summary>
    public static string Render(CalcModel model, string context, string text, bool russian)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        Ast ast;
        try { ast = Parser.Parse(text); }
        catch (CalcSyntaxException) { return text; }
        return new Writer(model, russian).Write(ast, context, 0);
    }

    private sealed class Writer(CalcModel model, bool russian)
    {
        private static int Precedence(Ast a) => a switch
        {
            BinaryAst { Op: "or" } => 1,
            BinaryAst { Op: "and" } => 2,
            UnaryAst { Op: "not" } => 3,
            BinaryAst { Op: "=" or "<>" or "<" or "<=" or ">" or ">=" } => 4,
            BinaryAst { Op: "+" or "-" } => 5,
            BinaryAst { Op: "*" or "/" or "%" } => 6,
            UnaryAst => 7,
            BinaryAst { Op: "^" } => 8,
            _ => 9
        };

        public string Write(Ast a, string ctx, int parent)
        {
            var text = a switch
            {
                NumberAst n => n.Value.ToString("R", CultureInfo.InvariantCulture),
                StringAst s => "'" + s.Value.Replace("'", "''") + "'",
                NameAst n => Name(n, ctx).Text,
                UnaryAst { Op: "not" } u => Word("not") + " " + Write(u.Operand, ctx, 3),
                UnaryAst u => "-" + Write(u.Operand, ctx, 7),
                BinaryAst b => Binary(b, ctx),
                CallAst c => Call(c, ctx),
                WhereAst w => Write(w.Source, ctx, 0) + " " + Word("where") + " " + Write(w.Filter, Target(w.Source, ctx) ?? ctx, 0),
                _ => ""
            };
            return Precedence(a) < parent ? "(" + text + ")" : text;
        }

        private string Word(string code) => russian ? Aliases.RussianWord(code) : code;

        private string Binary(BinaryAst b, string ctx)
        {
            var p = Precedence(b);
            // слева тот же приоритет без скобок (левая ассоциативность), справа — со скобками; у ^ наоборот
            var left = Write(b.Left, ctx, b.Op == "^" ? p + 1 : p);
            var right = Write(b.Right, ctx, b.Op == "^" ? p : p + 1);
            var op = b.Op is "and" or "or" ? Word(b.Op) : b.Op;
            return $"{left} {op} {right}";
        }

        private string Call(CallAst c, string ctx)
        {
            var code = Aliases.Function(c.Name);
            var name = russian ? Aliases.RussianFunction(code) : code;
            var lower = code.ToLowerInvariant();
            var args = new List<string>();
            if (lower is "param" or "lookup" && c.Args.Count > 0 && c.Args[0] is NameAst or StringAst)
            {
                var id = c.Args[0] is NameAst n ? n.Text : ((StringAst)c.Args[0]).Value;
                var p = model.ParameterByName(id);
                args.Add(p == null ? Bracket(id) : russian ? Bracket(model.AliasOf(p.Def)) : p.Def.Id);
                args.AddRange(c.Args.Skip(1).Select(a => Write(a, ctx, 0)));
            }
            else if (lower == "to" && c.Args.Count == 2)
            {
                args.Add(UnitArg(c.Args[0]));
                args.Add(Write(c.Args[1], ctx, 0));
            }
            else if (IsAggregate(lower, c, ctx))
            {
                var inner = Target(c.Args[0] is WhereAst w ? w.Source : c.Args[0], ctx) ?? ctx;
                args.Add(Write(c.Args[0], ctx, 0));
                args.AddRange(c.Args.Skip(1).Select(a => Write(a, inner, 0)));
            }
            else if (model.Registry.Builtins.ContainsKey(code))
                args.AddRange(c.Args.Select(a => a is NameAst n ? n.Text : Write(a, ctx, 0)));
            else args.AddRange(c.Args.Select(a => Write(a, ctx, 0)));
            return name + "(" + string.Join(", ", args) + ")";
        }

        private bool IsAggregate(string lower, CallAst c, string ctx) =>
            c.Args.Count > 0 && (lower is "sum" or "avg" or "count" or "first" || (lower is "min" or "max" && c.Args.Count <= 2 && (c.Args[0] is WhereAst || Target(c.Args[0], ctx) != null)));

        private string UnitArg(Ast a)
        {
            var text = a switch
            {
                NameAst n => n.Text,
                StringAst s => s.Value,
                BinaryAst { Op: "/" or "*" } b => UnitArg(b.Left) + b.Op + UnitArg(b.Right),
                NumberAst n => n.Value.ToString(CultureInfo.InvariantCulture),
                _ => ""
            };
            if (!russian) return string.Concat(System.Text.RegularExpressions.Regex.Split(text, "([/*])").Select(t => Aliases.UnitAtoms.FirstOrDefault(x => x.Ru == t.TrimEnd('²', '³', '2', '3')) is { En: { } en } ? en + Suffix(t) : t)).Replace("²", "2").Replace("³", "3");
            return Aliases.RussianUnit(text).Replace("²", "2").Replace("³", "3");
        }

        private static string Suffix(string t) => new(t.Reverse().TakeWhile(ch => char.IsDigit(ch) || ch is '²' or '³').Reverse().ToArray());

        /// <summary>Контекст, куда ведёт имя-список или ссылка (decks → deck); null — не связь.</summary>
        private string? Target(Ast a, string ctx)
        {
            if (a is not NameAst n) return null;
            var r = Name(n, ctx);
            return r.Target;
        }

        private (string Text, string? Target) Name(NameAst n, string ctx)
        {
            if (n.Path.Count == 1 && !n.Quoted && Aliases.Words.ContainsKey(Aliases.Word(n.Path[0])) && model.Resolve(ctx, n.Path[0]) == null)
                return (Word(Aliases.Word(n.Path[0])), null);
            var parts = new List<string>();
            var cur = ctx;
            string? target = null;
            foreach (var seg in n.Path)
            {
                var p = cur == null ? null : model.Resolve(cur, seg);
                if (p == null)
                {
                    parts.Add(russian ? Bracket(seg) : seg);
                    cur = null;
                    target = null;
                    continue;
                }
                parts.Add(russian ? Bracket(model.AliasOf(p)) : p.Name);
                target = p.Target;
                cur = p.Type == CalcType.Ref ? p.Target : null;
            }
            return (string.Join(".", parts), target);
        }

        private static string Bracket(string name) => "[" + name + "]";
    }

    /// <summary>Отрезки текста формулы для подсветки: свойство, функция, число, строка, ключевое слово, прочее.</summary>
    public enum Part { Text, Property, Function, Number, String, Keyword, Error }

    /// <summary>Разбивает текст на отрезки для подсветки (по лексеру); при ошибке разбора остаток — <see cref="Part.Error"/>.</summary>
    public static IReadOnlyList<(string Text, Part Kind)> Highlight(string text)
    {
        var result = new List<(string, Part)>();
        List<Token> tokens;
        try { tokens = Lexer.Tokenize(text); }
        catch (CalcSyntaxException e)
        {
            result.Add((text[..Math.Min(e.Position, text.Length)], Part.Text));
            result.Add((text[Math.Min(e.Position, text.Length)..], Part.Error));
            return result;
        }
        int pos = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.End) break;
            if (t.Position > pos) result.Add((text[pos..t.Position], Part.Text));
            var end = Math.Min(text.Length, t.Position + (t.Kind == TokenKind.String ? FindStringEnd(text, t.Position) - t.Position : t.Kind == TokenKind.Identifier ? t.Length : t.Text.Length));
            var kind = t.Kind switch
            {
                TokenKind.Number => Part.Number,
                TokenKind.String => Part.String,
                TokenKind.Identifier when t.Quoted => Part.Property,
                TokenKind.Identifier when i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.LParen => Part.Function,
                TokenKind.Identifier when t.Is("and") || t.Is("or") || t.Is("not") || t.Is("where") => Part.Keyword,
                TokenKind.Identifier => Part.Property,
                _ => Part.Text
            };
            result.Add((text[t.Position..end], kind));
            pos = end;
        }
        if (pos < text.Length) result.Add((text[pos..], Part.Text));
        return result;
    }

    private static int FindStringEnd(string text, int start)
    {
        var q = text[start];
        for (int i = start + 1; i < text.Length; i++)
            if (text[i] == q)
            {
                if (i + 1 < text.Length && text[i + 1] == q) { i++; continue; }
                return i + 1;
            }
        return text.Length;
    }
}
