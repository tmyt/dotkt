using System;
using System.Text;

namespace DotKt
{
    /// <summary>
    /// Runtime helper for Kotlin's <c>String.format</c> when the format string is NOT a compile-time literal
    /// (a literal is converted to a .NET composite format at compile time; a non-literal must convert at runtime).
    /// Converts a printf-style format ("%d", "%-5s", "%.2f", "%x", "%%") to a .NET composite format ("{0}",
    /// "{0,-5}", "{0:F2}", "{0:x}") and applies String.Format with the invariant culture.
    /// </summary>
    public static class Fmt
    {
        public static string format(string format, object[] args)
        {
            if (format == null) return null;
            var sb = new StringBuilder(format.Length + 8);
            int argIndex = 0;
            for (int i = 0; i < format.Length; i++)
            {
                char c = format[i];
                if (c != '%') { if (c == '{' || c == '}') sb.Append(c); sb.Append(c); continue; }
                if (i + 1 < format.Length && format[i + 1] == '%') { sb.Append('%'); i++; continue; }

                // %[-][0][width][.precision]conv
                int j = i + 1;
                bool left = false, zero = false;
                while (j < format.Length && (format[j] == '-' || format[j] == '0' || format[j] == '+' || format[j] == ' '))
                { if (format[j] == '-') left = true; if (format[j] == '0') zero = true; j++; }
                int width = 0; bool hasWidth = false;
                while (j < format.Length && char.IsDigit(format[j])) { width = width * 10 + (format[j] - '0'); hasWidth = true; j++; }
                int prec = -1;
                if (j < format.Length && format[j] == '.') { j++; prec = 0; while (j < format.Length && char.IsDigit(format[j])) { prec = prec * 10 + (format[j] - '0'); j++; } }
                if (j >= format.Length) { sb.Append('%'); continue; }
                char conv = format[j];

                string fmtSpec = conv switch
                {
                    'd' or 'i' => zero && hasWidth ? "D" + width : "",
                    'f' or 'F' => "F" + (prec < 0 ? 6 : prec),
                    'e' or 'E' => "E" + (prec < 0 ? 6 : prec),
                    'g' or 'G' => "G",
                    'x' => "x",
                    'X' => "X",
                    'o' => "",            // octal: no direct .NET spec; fall through to plain
                    's' or 'b' or 'c' => "",
                    _ => null,
                };
                if (fmtSpec == null) { sb.Append('%'); continue; }   // unknown -> leave the '%' literal

                sb.Append('{').Append(argIndex);
                if (hasWidth && !(zero && (conv == 'd' || conv == 'i')))
                    sb.Append(',').Append(left ? -width : width);
                if (fmtSpec.Length > 0) sb.Append(':').Append(fmtSpec);
                sb.Append('}');
                argIndex++;
                i = j;
            }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, sb.ToString(), args ?? Array.Empty<object>());
        }
    }
}
