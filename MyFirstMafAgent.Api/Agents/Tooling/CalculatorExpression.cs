using System.Globalization;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>
/// 极小的四则运算解析器（递归下降），只支持 + - * / %、括号、小数与一元负号。
/// 不使用 DataTable.Compute / 动态脚本，避免把任意输入当代码执行。
/// </summary>
public static class CalculatorExpression
{
    private const int MaxExpressionLength = 2_000;
    private const int MaxDepth = 64;

    /// <summary>计算表达式；语法错误、除零或结果溢出时抛出 <see cref="FormatException"/>。</summary>
    public static double Evaluate(string? expression) =>
        TryEvaluate(expression, out var value, out var error) ? value : throw new FormatException(error);

    /// <summary>尝试计算表达式，失败时返回 false 并给出中文原因。</summary>
    public static bool TryEvaluate(string? expression, out double value, out string error)
    {
        value = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "表达式为空。";
            return false;
        }

        if (expression.Length > MaxExpressionLength)
        {
            error = $"表达式过长（最多 {MaxExpressionLength} 个字符）。";
            return false;
        }

        // 统一全角括号与运算符，方便中文输入法直接输入。
        var normalized = expression
            .Replace('（', '(')
            .Replace('）', ')')
            .Replace('×', '*')
            .Replace('÷', '/')
            .Replace('−', '-');

        var parser = new Parser(normalized);
        try
        {
            var result = parser.ParseExpression(depth: 0);
            parser.SkipWhitespace();
            if (!parser.AtEnd)
            {
                error = $"表达式在位置 {parser.Position + 1} 处存在无法识别的内容：“{parser.PeekForError()}”。";
                return false;
            }

            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                error = "计算结果无效（出现 NaN 或无穷大）。";
                return false;
            }

            value = result;
            return true;
        }
        catch (FormatException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>把结果格式化成便于阅读的文本：能取整时不带小数点，否则最多 12 位有效小数。</summary>
    public static string Format(double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            return ((long)value).ToString(CultureInfo.InvariantCulture);

        return value.ToString("0.############", CultureInfo.InvariantCulture);
    }

    private sealed class Parser(string text)
    {
        public int Position { get; private set; }

        public bool AtEnd => Position >= text.Length;

        /// <summary>表达式 := 项 (('+' | '-') 项)*</summary>
        public double ParseExpression(int depth)
        {
            EnsureDepth(depth);
            var value = ParseTerm(depth);
            while (true)
            {
                SkipWhitespace();
                if (AtEnd) return value;

                var current = text[Position];
                if (current is not ('+' or '-')) return value;
                Position++;
                var right = ParseTerm(depth);
                value = current == '+' ? value + right : value - right;
            }
        }

        /// <summary>项 := 因子 (('*' | '/' | '%') 因子)*</summary>
        private double ParseTerm(int depth)
        {
            EnsureDepth(depth);
            var value = ParseUnary(depth);
            while (true)
            {
                SkipWhitespace();
                if (AtEnd) return value;

                var current = text[Position];
                if (current is not ('*' or '/' or '%')) return value;
                Position++;
                var right = ParseUnary(depth);
                switch (current)
                {
                    case '*':
                        value *= right;
                        break;
                    case '/':
                        if (right == 0) throw new FormatException("除数不能为 0。");
                        value /= right;
                        break;
                    default:
                        if (right == 0) throw new FormatException("取余的除数不能为 0。");
                        value %= right;
                        break;
                }
            }
        }

        /// <summary>因子 := '-' 因子 | 数字 | '(' 表达式 ')'</summary>
        private double ParseUnary(int depth)
        {
            EnsureDepth(depth);
            SkipWhitespace();
            if (AtEnd) throw new FormatException("表达式不完整，结尾缺少数字或括号。");

            if (text[Position] is '+' or '-')
            {
                var sign = text[Position];
                Position++;
                var operand = ParseUnary(depth + 1);
                return sign == '-' ? -operand : operand;
            }

            if (text[Position] == '(')
            {
                Position++;
                var inner = ParseExpression(depth + 1);
                SkipWhitespace();
                if (AtEnd || text[Position] != ')') throw new FormatException("括号不匹配：缺少右括号“)”。");
                Position++;
                return inner;
            }

            return ParseNumber();
        }

        private double ParseNumber()
        {
            var start = Position;
            var seenDot = false;
            while (!AtEnd)
            {
                var current = text[Position];
                if (char.IsAsciiDigit(current))
                {
                    Position++;
                    continue;
                }

                if (current == '.' && !seenDot)
                {
                    seenDot = true;
                    Position++;
                    continue;
                }

                break;
            }

            if (Position == start) throw new FormatException($"位置 {start + 1} 处应为数字，却遇到“{PeekForError()}”。");

            var token = text[start..Position];
            if (token == ".") throw new FormatException($"位置 {start + 1} 处的数字格式不正确。");

            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"无法解析数字“{token}”。");

            return value;
        }

        public void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(text[Position])) Position++;
        }

        public string PeekForError() => AtEnd ? "末尾" : text[Position].ToString();

        private static void EnsureDepth(int depth)
        {
            if (depth > MaxDepth) throw new FormatException("表达式嵌套层级过深。");
        }
    }
}
