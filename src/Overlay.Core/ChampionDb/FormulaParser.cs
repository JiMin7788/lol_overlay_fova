using System.Globalization;

namespace Overlay.Core.ChampionDb;

/// <summary>
/// Evaluates a <see cref="ChampionSpecialProperty.ValueFormula"/> string containing only
/// the four arithmetic operators, parentheses, numeric literals, and variable names
/// (substituted from a caller-supplied dictionary, e.g. "level" or "stacks").
///
/// Per M11 Agent Implementation Notes: "임의 코드 실행(eval)은 금지한다" — this is a
/// small hand-rolled recursive-descent parser, not a call into any scripting engine,
/// dynamic compilation, or reflection-based expression evaluator. It cannot execute
/// arbitrary code: the grammar has no function calls, no member access, no assignment.
/// </summary>
public static class FormulaParser
{
    /// <summary>Parses and evaluates <paramref name="formula"/> with the given variable
    /// bindings (case-insensitive). Throws <see cref="FormatException"/> on malformed
    /// input or an unknown variable reference.</summary>
    public static double Evaluate(string formula, IReadOnlyDictionary<string, double> variables)
    {
        var tokens = Tokenize(formula);
        var pos = 0;
        var result = ParseExpression(tokens, ref pos, variables);
        if (pos != tokens.Count)
            throw new FormatException($"Unexpected token '{tokens[pos]}' in formula '{formula}'");
        return result;
    }

    private static List<string> Tokenize(string formula)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < formula.Length)
        {
            var c = formula[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if ("+-*/()".IndexOf(c) >= 0) { tokens.Add(c.ToString()); i++; continue; }
            if (char.IsDigit(c) || c == '.')
            {
                var start = i;
                while (i < formula.Length && (char.IsDigit(formula[i]) || formula[i] == '.')) i++;
                tokens.Add(formula[start..i]);
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < formula.Length && (char.IsLetterOrDigit(formula[i]) || formula[i] == '_')) i++;
                tokens.Add(formula[start..i]);
                continue;
            }
            throw new FormatException($"Unexpected character '{c}' in formula '{formula}'");
        }
        return tokens;
    }

    // expression := term (('+' | '-') term)*
    private static double ParseExpression(List<string> tokens, ref int pos, IReadOnlyDictionary<string, double> vars)
    {
        var value = ParseTerm(tokens, ref pos, vars);
        while (pos < tokens.Count && (tokens[pos] == "+" || tokens[pos] == "-"))
        {
            var op = tokens[pos++];
            var rhs = ParseTerm(tokens, ref pos, vars);
            value = op == "+" ? value + rhs : value - rhs;
        }
        return value;
    }

    // term := factor (('*' | '/') factor)*
    private static double ParseTerm(List<string> tokens, ref int pos, IReadOnlyDictionary<string, double> vars)
    {
        var value = ParseFactor(tokens, ref pos, vars);
        while (pos < tokens.Count && (tokens[pos] == "*" || tokens[pos] == "/"))
        {
            var op = tokens[pos++];
            var rhs = ParseFactor(tokens, ref pos, vars);
            if (op == "*") value *= rhs;
            else
            {
                if (rhs == 0) throw new DivideByZeroException($"Division by zero evaluating formula");
                value /= rhs;
            }
        }
        return value;
    }

    // factor := ['-'] ( number | variable | '(' expression ')' )
    private static double ParseFactor(List<string> tokens, ref int pos, IReadOnlyDictionary<string, double> vars)
    {
        if (pos >= tokens.Count)
            throw new FormatException("Unexpected end of formula");

        if (tokens[pos] == "-")
        {
            pos++;
            return -ParseFactor(tokens, ref pos, vars);
        }

        if (tokens[pos] == "(")
        {
            pos++;
            var value = ParseExpression(tokens, ref pos, vars);
            if (pos >= tokens.Count || tokens[pos] != ")")
                throw new FormatException("Missing closing parenthesis");
            pos++;
            return value;
        }

        var token = tokens[pos];
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var literal))
        {
            pos++;
            return literal;
        }

        // Variable reference.
        if (!vars.TryGetValue(token, out var varValue))
        {
            // Case-insensitive fallback lookup.
            var match = vars.Keys.FirstOrDefault(k => string.Equals(k, token, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new FormatException($"Unknown variable '{token}' in formula");
            varValue = vars[match];
        }
        pos++;
        return varValue;
    }
}
