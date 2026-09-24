using System.Globalization;

namespace Quicklight.Core.Calc;

/// <summary>
/// Small recursive-descent calculator: + - * / % ^, parentheses, unary minus, implicit multiplication "2(3+4)" / "2pi",
/// constants pi and e, functions sqrt abs round floor ceil sin cos tan asin acos atan log ln exp. Also accepts × ÷ and thousands commas.
/// </summary>
public static class Calculator
{
    /// <summary>Returns true only for inputs that look like math (contain an operator or a function), not bare numbers or words.</summary>
    public static bool TryEvaluate(string input, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = Normalize(input);
        if (!LooksLikeMath(s)) return false;
        try
        {
            var p = new Parser(s);
            value = p.ParseExpression();
            p.SkipSpaces();
            if (!p.AtEnd) return false;
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch (FormatException) { return false; }
    }

    public static string Format(double v)
    {
        if (Math.Abs(v) >= 1e15 || (v != 0 && Math.Abs(v) < 1e-9)) return v.ToString("G12", CultureInfo.InvariantCulture);
        var rounded = Math.Round(v, 10) + 0.0; // + 0.0 turns -0 into 0
        return rounded.ToString("#,0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>Same as <see cref="Format"/> but without thousands separators, for the clipboard.</summary>
    public static string FormatPlain(double v)
    {
        if (Math.Abs(v) >= 1e15 || (v != 0 && Math.Abs(v) < 1e-9)) return v.ToString("G12", CultureInfo.InvariantCulture);
        return (Math.Round(v, 10) + 0.0).ToString("0.##########", CultureInfo.InvariantCulture);
    }

    static string Normalize(string s)
    {
        s = s.Trim().TrimEnd('=').Trim();
        s = s.Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace("**", "^");
        // Thousands separators: "1,000,000" -> "1000000". Commas elsewhere are argument separators, which we do not support.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=\d),(?=\d{3}(\D|$))", "");
        return s.ToLowerInvariant();
    }

    static readonly string[] Functions = ["sqrt", "abs", "round", "floor", "ceil", "sin", "cos", "tan", "asin", "acos", "atan", "log", "ln", "exp"];

    static bool LooksLikeMath(string s)
    {
        bool hasDigit = s.Any(char.IsDigit) || s.Contains("pi");
        if (!hasDigit) return false;
        foreach (var f in Functions) if (s.Contains(f + "(")) return true;
        // An operator between operands, not just a leading minus of a single number or a date like 2024-01-01.
        var body = s.TrimStart('-', '+');
        // Dates, phone numbers and year ranges look like subtraction but are not: 2024-01-15, 010-1234-5678, 2020-2024.
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d+(-\d+){2,}$") || System.Text.RegularExpressions.Regex.IsMatch(s, @"^(19|20)\d\d-(19|20)\d\d$"))
            return false;
        return body.IndexOfAny(['+', '-', '*', '/', '^', '%', '(']) >= 0 || s.Contains("pi");
    }

    sealed class Parser(string s)
    {
        const int MaxDepth = 200; // "((((…" or "----…" must not overflow the stack
        int pos, depth;

        void Enter() { if (++depth > MaxDepth) throw new FormatException("expression nested too deeply"); }
        public bool AtEnd => pos >= s.Length;

        public void SkipSpaces() { while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++; }

        bool Eat(char c)
        {
            SkipSpaces();
            if (pos < s.Length && s[pos] == c) { pos++; return true; }
            return false;
        }

        public double ParseExpression()
        {
            double v = ParseTerm();
            while (true)
            {
                if (Eat('+')) v += ParseTerm();
                else if (Eat('-')) v -= ParseTerm();
                else return v;
            }
        }

        double ParseTerm()
        {
            double v = ParseUnary();
            while (true)
            {
                if (Eat('*')) v *= ParseUnary();
                else if (Eat('/')) v /= ParseUnary();
                else if (Eat('%'))
                {
                    // "50%" alone means 0.5; "10 % 3" is modulo.
                    SkipSpaces();
                    if (AtEnd || s[pos] is ')' or '+' or '-' or '*' or '/') v /= 100;
                    else v %= ParseUnary();
                }
                else if (StartsOperand()) v *= ParseUnary(); // implicit multiplication
                else return v;
            }
        }

        bool StartsOperand()
        {
            SkipSpaces();
            return pos < s.Length && (s[pos] == '(' || char.IsLetter(s[pos]));
        }

        double ParseUnary()
        {
            Enter();
            try
            {
                if (Eat('-')) return -ParseUnary();
                if (Eat('+')) return ParseUnary();
                return ParsePower();
            }
            finally { depth--; }
        }

        double ParsePower()
        {
            double b = ParsePrimary();
            if (Eat('^')) return Math.Pow(b, ParseUnary()); // right associative
            return b;
        }

        double ParsePrimary()
        {
            SkipSpaces();
            if (Eat('('))
            {
                double v = ParseExpression();
                if (!Eat(')')) throw new FormatException("missing )");
                return v;
            }
            if (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.'))
            {
                int start = pos;
                while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.')) pos++;
                if (pos < s.Length && s[pos] == 'e' && pos + 1 < s.Length && (char.IsDigit(s[pos + 1]) || ((s[pos + 1] == '-' || s[pos + 1] == '+') && pos + 2 < s.Length && char.IsDigit(s[pos + 2]))))
                {
                    pos += 2;
                    while (pos < s.Length && char.IsDigit(s[pos])) pos++;
                }
                if (!double.TryParse(s.AsSpan(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    throw new FormatException("bad number");
                return num;
            }
            if (pos < s.Length && char.IsLetter(s[pos]))
            {
                int start = pos;
                while (pos < s.Length && char.IsLetter(s[pos])) pos++;
                var name = s[start..pos];
                switch (name)
                {
                    case "pi": return Math.PI;
                    case "e": return Math.E;
                }
                if (!Eat('(')) throw new FormatException("unknown identifier " + name);
                double a = ParseExpression();
                if (!Eat(')')) throw new FormatException("missing )");
                return name switch
                {
                    "sqrt" => Math.Sqrt(a),
                    "abs" => Math.Abs(a),
                    "round" => Math.Round(a, MidpointRounding.AwayFromZero),
                    "floor" => Math.Floor(a),
                    "ceil" => Math.Ceiling(a),
                    "sin" => Math.Sin(a),
                    "cos" => Math.Cos(a),
                    "tan" => Math.Tan(a),
                    "asin" => Math.Asin(a),
                    "acos" => Math.Acos(a),
                    "atan" => Math.Atan(a),
                    "log" => Math.Log10(a),
                    "ln" => Math.Log(a),
                    "exp" => Math.Exp(a),
                    _ => throw new FormatException("unknown function " + name),
                };
            }
            throw new FormatException("unexpected input");
        }
    }
}
