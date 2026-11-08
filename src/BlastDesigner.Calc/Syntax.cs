using System.Globalization;
using System.Text;

namespace BlastDesigner.Calc;

/// <summary>Ошибка в тексте формулы: позиция и сообщение для строки статуса редактора.</summary>
public sealed class CalcSyntaxException(string message, int position, int length = 1) : Exception(message)
{
    public int Position { get; } = position;
    public int Length { get; } = Math.Max(1, length);
}

public enum TokenKind { Number, String, Identifier, Operator, LParen, RParen, Comma, Dot, End }

public readonly record struct Token(TokenKind Kind, string Text, int Position, double Number = 0, bool Quoted = false)
{
    /// <summary>Оператор или ключевое слово по кодовому имени; русское слово (и, или, не, где) тоже подходит, имя в [скобках] — нет.</summary>
    public bool Is(string op) => Kind == TokenKind.Operator ? Text == op
        : Kind == TokenKind.Identifier && !Quoted && (string.Equals(Text, op, StringComparison.OrdinalIgnoreCase) || Aliases.Word(Text) == op);
    public int Length => Math.Max(1, Text.Length + (Quoted ? 2 : 0));
}

/// <summary>Лексер языка формул: числа (1e-9), строки в одинарных кавычках, имена, операторы.</summary>
public static class Lexer
{
    private static readonly string[] Ops = { "<=", ">=", "<>", "!=", "==", "+", "-", "*", "/", "^", "=", "<", ">", "%" };

    public static List<Token> Tokenize(string text)
    {
        var list = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                // комментарий до конца строки
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            int start = i;
            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                if (i < text.Length && (text[i] == 'e' || text[i] == 'E') && i + 1 < text.Length
                    && (char.IsDigit(text[i + 1]) || ((text[i + 1] == '-' || text[i + 1] == '+') && i + 2 < text.Length && char.IsDigit(text[i + 2]))))
                {
                    i += 2;
                    while (i < text.Length && char.IsDigit(text[i])) i++;
                }
                var s = text[start..i];
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    throw new CalcSyntaxException($"Неверное число «{s}»", start, s.Length);
                list.Add(new Token(TokenKind.Number, s, start, v));
                continue;
            }
            if (c == '\'' || c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= text.Length) throw new CalcSyntaxException("Строка не закрыта кавычкой", start, i - start);
                    if (text[i] == c)
                    {
                        if (i + 1 < text.Length && text[i + 1] == c) { sb.Append(c); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(text[i++]);
                }
                list.Add(new Token(TokenKind.String, sb.ToString(), start));
                continue;
            }
            if (c == '[')
            {
                // русское имя в скобках: [Масса ВВ], [Продукт]
                var close = text.IndexOf(']', i + 1);
                if (close < 0) throw new CalcSyntaxException("Нет закрывающей скобки «]» у имени", start, text.Length - start);
                var name = text[(i + 1)..close].Trim();
                if (name.Length == 0) throw new CalcSyntaxException("Пустое имя в скобках []", start, 2);
                list.Add(new Token(TokenKind.Identifier, name, start, 0, true));
                i = close + 1;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                list.Add(new Token(TokenKind.Identifier, text[start..i], start));
                continue;
            }
            switch (c)
            {
                case '(': list.Add(new Token(TokenKind.LParen, "(", i++)); continue;
                case ')': list.Add(new Token(TokenKind.RParen, ")", i++)); continue;
                case ',': case ';': list.Add(new Token(TokenKind.Comma, ",", i++)); continue;
                case '.': list.Add(new Token(TokenKind.Dot, ".", i++)); continue;
            }
            var op = Ops.FirstOrDefault(o => string.CompareOrdinal(text, i, o, 0, o.Length) == 0);
            if (op == null) throw new CalcSyntaxException($"Неожиданный символ «{c}»", i);
            list.Add(new Token(TokenKind.Operator, op, i));
            i += op.Length;
        }
        list.Add(new Token(TokenKind.End, "", text.Length));
        return list;
    }
}

/// <summary>Узел дерева разбора формулы.</summary>
public abstract record Ast(int Position, int Length);
public sealed record NumberAst(double Value, int Position, int Length) : Ast(Position, Length);
public sealed record StringAst(string Value, int Position, int Length) : Ast(Position, Length);
/// <summary>Имя или цепочка через точку: <c>length</c>, <c>product.density</c>, <c>hole.row.burden</c>.</summary>
public sealed record NameAst(IReadOnlyList<string> Path, int Position, int Length, bool Quoted = false) : Ast(Position, Length)
{
    public string Text => string.Join(".", Path);
}
public sealed record CallAst(string Name, IReadOnlyList<Ast> Args, int Position, int Length) : Ast(Position, Length);
public sealed record UnaryAst(string Op, Ast Operand, int Position, int Length) : Ast(Position, Length);
public sealed record BinaryAst(string Op, Ast Left, Ast Right, int Position, int Length) : Ast(Position, Length);
/// <summary>Отбор в агрегате: <c>decks where product.isExplosive</c>.</summary>
public sealed record WhereAst(Ast Source, Ast Filter, int Position, int Length) : Ast(Position, Length);

/// <summary>
/// Разбор формулы по приоритетам: or &lt; and &lt; not &lt; сравнения &lt; + - &lt; * / % &lt; унарный минус &lt; ^ (правоассоциативно);
/// <c>where</c> допускается только в аргументе функции.
/// </summary>
public sealed class Parser
{
    private readonly List<Token> _t;
    private int _i;

    private Parser(string text) => _t = Lexer.Tokenize(text);

    public static Ast Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new CalcSyntaxException("Пустая формула", 0);
        var p = new Parser(text);
        var e = p.Or();
        if (p.Peek.Kind != TokenKind.End) throw new CalcSyntaxException($"Лишнее «{p.Peek.Text}»", p.Peek.Position, p.Peek.Length);
        return e;
    }

    private Token Peek => _t[_i];
    private Token Next() => _t[_i++];

    private Ast Or()
    {
        var l = And();
        while (Peek.Is("or")) { var op = Next(); var r = And(); l = new BinaryAst("or", l, r, op.Position, op.Length); }
        return l;
    }

    private Ast And()
    {
        var l = Not();
        while (Peek.Is("and")) { var op = Next(); var r = Not(); l = new BinaryAst("and", l, r, op.Position, op.Length); }
        return l;
    }

    private Ast Not()
    {
        if (Peek.Is("not")) { var op = Next(); return new UnaryAst("not", Not(), op.Position, op.Length); }
        return Compare();
    }

    private static readonly string[] CompareOps = { "=", "==", "<>", "!=", "<", "<=", ">", ">=" };

    private Ast Compare()
    {
        var l = Additive();
        while (Peek.Kind == TokenKind.Operator && CompareOps.Contains(Peek.Text))
        {
            var op = Next();
            var r = Additive();
            var norm = op.Text switch { "==" => "=", "!=" => "<>", _ => op.Text };
            l = new BinaryAst(norm, l, r, op.Position, op.Length);
        }
        return l;
    }

    private Ast Additive()
    {
        var l = Multiplicative();
        while (Peek.Kind == TokenKind.Operator && Peek.Text is "+" or "-")
        {
            var op = Next();
            l = new BinaryAst(op.Text, l, Multiplicative(), op.Position, op.Length);
        }
        return l;
    }

    private Ast Multiplicative()
    {
        var l = Unary();
        while (Peek.Kind == TokenKind.Operator && Peek.Text is "*" or "/" or "%")
        {
            var op = Next();
            l = new BinaryAst(op.Text, l, Unary(), op.Position, op.Length);
        }
        return l;
    }

    private Ast Unary()
    {
        if (Peek.Kind == TokenKind.Operator && Peek.Text is "-" or "+")
        {
            var op = Next();
            var operand = Unary();
            return op.Text == "+" ? operand : new UnaryAst("-", operand, op.Position, op.Length);
        }
        return Power();
    }

    private Ast Power()
    {
        var b = Primary();
        if (Peek.Kind == TokenKind.Operator && Peek.Text == "^")
        {
            var op = Next();
            // показатель может быть с унарным минусом: x^-0.8
            return new BinaryAst("^", b, Unary(), op.Position, op.Length);
        }
        return b;
    }

    private Ast Primary()
    {
        var t = Next();
        switch (t.Kind)
        {
            case TokenKind.Number: return new NumberAst(t.Number, t.Position, t.Length);
            case TokenKind.String: return new StringAst(t.Text, t.Position, t.Text.Length + 2);
            case TokenKind.LParen:
            {
                var e = Or();
                Expect(TokenKind.RParen, ")");
                return e;
            }
            case TokenKind.Identifier:
            {
                if (Peek.Kind == TokenKind.LParen && !t.Quoted)
                {
                    Next();
                    var args = new List<Ast>();
                    if (Peek.Kind != TokenKind.RParen)
                    {
                        do args.Add(Argument());
                        while (Accept(TokenKind.Comma));
                    }
                    var close = Expect(TokenKind.RParen, ")");
                    return new CallAst(t.Text, args, t.Position, close.Position + 1 - t.Position);
                }
                var path = new List<string> { t.Text };
                var quoted = t.Quoted;
                var end = t.Position + t.Length;
                while (Peek.Kind == TokenKind.Dot)
                {
                    Next();
                    var id = Next();
                    if (id.Kind != TokenKind.Identifier) throw new CalcSyntaxException("После точки ожидается имя свойства", id.Position, id.Length);
                    path.Add(id.Text);
                    quoted |= id.Quoted;
                    end = id.Position + id.Length;
                }
                return new NameAst(path, t.Position, end - t.Position, quoted);
            }
            case TokenKind.End: throw new CalcSyntaxException("Формула оборвалась", t.Position);
            default: throw new CalcSyntaxException($"Неожиданное «{t.Text}»", t.Position, t.Length);
        }
    }

    private Ast Argument()
    {
        var e = Or();
        if (Peek.Is("where"))
        {
            var w = Next();
            return new WhereAst(e, Or(), w.Position, w.Length);
        }
        return e;
    }

    private bool Accept(TokenKind k)
    {
        if (Peek.Kind != k) return false;
        _i++;
        return true;
    }

    private Token Expect(TokenKind k, string text)
    {
        if (Peek.Kind != k) throw new CalcSyntaxException($"Ожидается «{text}»", Peek.Position, Peek.Length);
        return Next();
    }
}
