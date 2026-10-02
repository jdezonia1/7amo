using System.Globalization;

namespace Raffaello.Core.Assemblies;

public sealed class FormulaException : Exception
{
    public string Formula { get; }
    public FormulaException(string formula, string message) : base($"{message} in '{formula}'") => Formula = formula;
}

/// <summary>
/// [assemblies] Small arithmetic language for quantity formulas: numbers, names (parameters, spec values, earlier component
/// keys), + - * / ^, parentheses, comparisons (&lt; &lt;= &gt; &gt;= == !=), and / or / not (&amp;&amp; || !), and the functions
/// ceil, floor, round(x[, digits]), roundup(x, step), min, max, abs, sqrt, if(cond, a, b). Booleans are 1 / 0.
/// Example: <c>ceil(conduit / stick_len)</c>, <c>if(is_pvc, joints * glue_per_joint, 0)</c>.
/// </summary>
public static class Formula
{
    public static double Eval(string formula, IReadOnlyDictionary<string, double> vars)
    {
        if (string.IsNullOrWhiteSpace(formula)) return 0;
        var p = new Parser(formula, vars);
        var v = p.ParseExpr();
        p.ExpectEnd();
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new FormulaException(formula, "result is not a number (division by zero?)");
        return v;
    }

    /// <summary>Names a formula uses (for the template editor: unknown names are reported before saving).</summary>
    public static List<string> Names(string formula)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(formula)) return res;
        var i = 0;
        while (i < formula.Length)
        {
            var c = formula[i];
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < formula.Length && (char.IsLetterOrDigit(formula[i]) || formula[i] == '_')) i++;
                var name = formula[start..i];
                var j = i; while (j < formula.Length && formula[j] == ' ') j++;
                var isCall = j < formula.Length && formula[j] == '(';
                if (!isCall && !res.Contains(name, StringComparer.OrdinalIgnoreCase) && !IsKeyword(name)) res.Add(name);
            }
            else i++;
        }
        return res;
    }

    /// <summary>Checks a formula against the known names; returns the problem or "".</summary>
    public static string Validate(string formula, IEnumerable<string> known)
    {
        var set = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        var unknown = Names(formula).Where(n => !set.Contains(n)).ToList();
        if (unknown.Count > 0) return "unknown name(s): " + string.Join(", ", unknown);
        // evaluated with every name = 1, then = 2.5 (a formula like 1 / (x - 1) is fine for real values)
        string first = "";
        foreach (var probe in new[] { 1.0, 2.5 })
        {
            try { Eval(formula, set.ToDictionary(n => n, _ => probe, StringComparer.OrdinalIgnoreCase)); return ""; }
            catch (FormulaException ex) { if (first.Length == 0) first = ex.Message; if (!ex.Message.Contains("division by zero") && !ex.Message.Contains("not a number")) return ex.Message; }
        }
        return first;
    }

    private static bool IsKeyword(string n) => n.Equals("and", StringComparison.OrdinalIgnoreCase) || n.Equals("or", StringComparison.OrdinalIgnoreCase) || n.Equals("not", StringComparison.OrdinalIgnoreCase)
                                              || n.Equals("true", StringComparison.OrdinalIgnoreCase) || n.Equals("false", StringComparison.OrdinalIgnoreCase);

    private sealed class Parser
    {
        private readonly string _s;
        private readonly IReadOnlyDictionary<string, double> _vars;
        private int _i;
        /// <summary>&gt; 0 while parsing the branch of if() that is not taken (division by zero there is not an error).</summary>
        private int _quiet;

        public Parser(string s, IReadOnlyDictionary<string, double> vars) { _s = s; _vars = vars; }

        private FormulaException Error(string msg) => new(_s, msg);

        private void Ws() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

        private bool Eat(string tok)
        {
            Ws();
            if (string.CompareOrdinal(_s, _i, tok, 0, tok.Length) != 0) return false;
            if (char.IsLetter(tok[0]) && _i + tok.Length < _s.Length && (char.IsLetterOrDigit(_s[_i + tok.Length]) || _s[_i + tok.Length] == '_')) return false;
            _i += tok.Length;
            return true;
        }

        public void ExpectEnd()
        {
            Ws();
            if (_i < _s.Length) throw Error($"unexpected '{_s[_i]}' at {_i + 1}");
        }

        public double ParseExpr() => Or();

        private double Or()
        {
            var v = And();
            while (Eat("||") || Eat("or")) { var r = And(); v = (v != 0 || r != 0) ? 1 : 0; }
            return v;
        }

        private double And()
        {
            var v = Cmp();
            while (Eat("&&") || Eat("and")) { var r = Cmp(); v = (v != 0 && r != 0) ? 1 : 0; }
            return v;
        }

        private double Cmp()
        {
            var v = Add();
            while (true)
            {
                if (Eat("<=")) v = v <= Add() ? 1 : 0;
                else if (Eat(">=")) v = v >= Add() ? 1 : 0;
                else if (Eat("==")) v = Math.Abs(v - Add()) < 1e-9 ? 1 : 0;
                else if (Eat("!=")) v = Math.Abs(v - Add()) >= 1e-9 ? 1 : 0;
                else if (Eat("<")) v = v < Add() ? 1 : 0;
                else if (Eat(">")) v = v > Add() ? 1 : 0;
                else return v;
            }
        }

        private double Add()
        {
            var v = Mul();
            while (true)
            {
                if (Eat("+")) v += Mul();
                else if (Eat("-")) v -= Mul();
                else return v;
            }
        }

        private double Mul()
        {
            var v = Unary();
            while (true)
            {
                if (Eat("*")) v *= Unary();
                else if (Eat("/"))
                {
                    var r = Unary();
                    if (Math.Abs(r) < 1e-12)
                    {
                        if (_quiet > 0) { v = 0; continue; }
                        throw Error("division by zero");
                    }
                    v /= r;
                }
                else return v;
            }
        }

        private double Unary()
        {
            if (Eat("-")) return -Unary();
            if (Eat("+")) return Unary();
            if (Eat("!") || Eat("not")) return Unary() == 0 ? 1 : 0;
            return Pow();
        }

        private double Pow()
        {
            var b = Atom();
            if (Eat("^")) return Math.Pow(b, Unary());
            return b;
        }

        private double Atom()
        {
            Ws();
            if (_i >= _s.Length) throw Error("unexpected end");
            var c = _s[_i];
            if (c == '(')
            {
                _i++;
                var v = ParseExpr();
                if (!Eat(")")) throw Error("missing ')'");
                return v;
            }
            if (char.IsDigit(c) || c == '.')
            {
                var start = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E') && _i + 1 < _s.Length && (char.IsDigit(_s[_i + 1]) || _s[_i + 1] is '-' or '+'))
                {
                    _i += 2;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }
                if (!double.TryParse(_s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) throw Error($"bad number '{_s[start.._i]}'");
                return n;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                var name = _s[start.._i];
                Ws();
                if (_i < _s.Length && _s[_i] == '(' && name.Equals("if", StringComparison.OrdinalIgnoreCase))
                {
                    _i++;
                    var cond = ParseExpr();
                    if (!Eat(",")) throw Error("if needs 3 arguments");
                    if (cond == 0) _quiet++;
                    var a = ParseExpr();
                    if (cond == 0) _quiet--;
                    if (!Eat(",")) throw Error("if needs 3 arguments");
                    if (cond != 0) _quiet++;
                    var b = ParseExpr();
                    if (cond != 0) _quiet--;
                    if (!Eat(")")) throw Error("missing ')' after if(");
                    return cond != 0 ? a : b;
                }
                if (_i < _s.Length && _s[_i] == '(')
                {
                    _i++;
                    var args = new List<double>();
                    if (!Eat(")"))
                    {
                        do args.Add(ParseExpr()); while (Eat(","));
                        if (!Eat(")")) throw Error($"missing ')' after {name}(");
                    }
                    return Call(name, args);
                }
                if (name.Equals("true", StringComparison.OrdinalIgnoreCase)) return 1;
                if (name.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0;
                if (_vars.TryGetValue(name, out var v)) return v;
                throw Error($"unknown name '{name}'");
            }
            throw Error($"unexpected '{c}' at {_i + 1}");
        }

        private double Call(string name, List<double> a)
        {
            void Need(int n) { if (a.Count != n) throw Error($"{name} needs {n} argument(s)"); }
            switch (name.ToLowerInvariant())
            {
                case "ceil": Need(1); return Math.Ceiling(a[0] - 1e-9);
                case "floor": Need(1); return Math.Floor(a[0] + 1e-9);
                case "round":
                    if (a.Count == 1) return Math.Round(a[0], MidpointRounding.AwayFromZero);
                    Need(2); return Math.Round(a[0], (int)Math.Clamp(a[1], 0, 10), MidpointRounding.AwayFromZero);
                case "roundup": Need(2); return a[1] <= 0 ? a[0] : Math.Ceiling(a[0] / a[1] - 1e-9) * a[1];
                case "min": if (a.Count == 0) throw Error("min needs arguments"); return a.Min();
                case "max": if (a.Count == 0) throw Error("max needs arguments"); return a.Max();
                case "abs": Need(1); return Math.Abs(a[0]);
                case "sqrt": Need(1); return Math.Sqrt(Math.Max(0, a[0]));
                case "if": Need(3); return a[0] != 0 ? a[1] : a[2];
                default: throw Error($"unknown function '{name}'");
            }
        }
    }
}
