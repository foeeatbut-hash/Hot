using System.Globalization;

namespace RengaHeat.Core.Mapping;

/// <summary>
/// Вычислитель формул источника «Формула»: арифметика, скобки, переменные и базовые функции.
/// Рекурсивный спуск, без внешних зависимостей. Переменные разрешаются через делегат
/// (обычно это другие расчётные поля того же объекта).
/// </summary>
public static class FormulaEvaluator
{
    public static double Evaluate(string expression, Func<string, object?> variableProvider)
    {
        var p = new Parser(expression, variableProvider);
        var result = p.ParseExpression();
        p.ExpectEnd();
        return result;
    }

    private sealed class Parser(string text, Func<string, object?> variables)
    {
        private int _pos;

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipSpaces();
                if (TryConsume('+')) value += ParseTerm();
                else if (TryConsume('-')) value -= ParseTerm();
                else return value;
            }
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipSpaces();
                if (TryConsume('*')) value *= ParseFactor();
                else if (TryConsume('/'))
                {
                    var d = ParseFactor();
                    if (d == 0) throw new DivideByZeroException("деление на ноль в формуле");
                    value /= d;
                }
                else return value;
            }
        }

        private double ParseFactor()
        {
            var value = ParseUnary();
            SkipSpaces();
            if (TryConsume('^')) value = Math.Pow(value, ParseFactor());
            return value;
        }

        private double ParseUnary()
        {
            SkipSpaces();
            if (TryConsume('-')) return -ParseUnary();
            if (TryConsume('+')) return ParseUnary();
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (TryConsume('('))
            {
                var v = ParseExpression();
                SkipSpaces();
                if (!TryConsume(')')) throw Fail("ожидалась «)»");
                return v;
            }

            if (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.'))
                return ParseNumber();

            if (_pos < text.Length && (char.IsLetter(text[_pos]) || text[_pos] == '_'))
                return ParseIdentifier();

            throw Fail("ожидалось число, переменная или «(»");
        }

        private double ParseNumber()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] is '.' or ','))
                _pos++;
            var s = text[start.._pos].Replace(',', '.');
            return double.Parse(s, CultureInfo.InvariantCulture);
        }

        private double ParseIdentifier()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '_' or '.'))
                _pos++;
            var name = text[start.._pos];

            SkipSpaces();
            if (TryConsume('('))
            {
                var args = new List<double> { ParseExpression() };
                SkipSpaces();
                while (TryConsume(';') || TryConsume(','))
                {
                    args.Add(ParseExpression());
                    SkipSpaces();
                }
                if (!TryConsume(')')) throw Fail("ожидалась «)» после аргументов функции");
                return name.ToLowerInvariant() switch
                {
                    "abs" => Math.Abs(args[0]),
                    "sqrt" => Math.Sqrt(args[0]),
                    "min" => args.Min(),
                    "max" => args.Max(),
                    "round" => Math.Round(args[0], args.Count > 1 ? (int)args[1] : 0),
                    _ => throw Fail($"неизвестная функция «{name}»"),
                };
            }

            var raw = variables(name)
                ?? throw Fail($"переменная «{name}» не определена");
            return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        }

        private void SkipSpaces()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private bool TryConsume(char c)
        {
            if (_pos < text.Length && text[_pos] == c) { _pos++; return true; }
            return false;
        }

        public void ExpectEnd()
        {
            SkipSpaces();
            if (_pos != text.Length) throw Fail("лишние символы в конце формулы");
        }

        private FormatException Fail(string message) =>
            new($"Ошибка в формуле на позиции {_pos + 1}: {message}. Формула: «{text}»");
    }
}
