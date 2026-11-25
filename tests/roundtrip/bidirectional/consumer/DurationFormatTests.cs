using System.Globalization;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;

public class DurationFormatTests
{
    [Test]
    public void FixedDecimalMatchesExactRationalRounding()
    {
        // Duration cannot itself represent arbitrary doubles. Exercise its internal
        // formatter from the actual stdlib DLL, including both neighbors of each tie.
        var format = Assembly.Load("DotKt.Stdlib").GetType("kotlin.time.DurationKt", true)!
            .GetMethod("formatToExactDecimals", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                [typeof(double), typeof(int)])!.CreateDelegate<Func<double, int, string>>();
        for (int d = 0; d <= 12; ++d)
        {
            void Check(double x) => Assert.That(format(x, d), Is.EqualTo(Reference(x, d)),
                $"bits=0x{BitConverter.DoubleToUInt64Bits(x):x16}, decimals={d}");
            foreach (double value in new[] { 0.0, double.Epsilon, Math.BitDecrement(Math.ScaleB(1.0, -1022)),
                         double.MaxValue, 1e27, 999_999_999.5, 1_999_999_999.5, 9.96875,
                         Math.BitDecrement(1.0005e-9), 1.0005e-9, double.PositiveInfinity, double.NaN })
            {
                Check(value);
                Check(-value);
            }
            // Multiplying 1/2^(d+1) by 10^d gives exactly 5^d / 2.
            foreach (double tie in new[] { Math.ScaleB(1.0, -d - 1), Math.ScaleB(3.0, -d - 1) })
                foreach (double x in new[] { Math.BitDecrement(tie), tie, Math.BitIncrement(tie) })
                {
                    Check(x);
                    Check(-x);
                }
        }
    }

    private static string Reference(double x, int decimals)
    {
        if (!double.IsFinite(x)) return double.IsNaN(x) ? "NaN" : x < 0 ? "-Infinity" : "Infinity";
        var sign = double.IsNegative(x) ? "-" : "";
        int shift = x == 0 ? 0 : 52 - Math.ILogB(Math.Abs(x));
        var numerator = new BigInteger(Math.ScaleB(Math.Abs(x), shift)) * BigInteger.Pow(10, decimals);
        var denominator = BigInteger.One << Math.Max(0, shift);
        var q = BigInteger.DivRem(numerator << Math.Max(0, -shift), denominator, out var r);
        if (2 * r > denominator || 2 * r == denominator && !q.IsEven) ++q;
        var digits = q.ToString(CultureInfo.InvariantCulture).PadLeft(decimals + 1, '0');
        return sign + (decimals == 0 ? digits : digits.Insert(digits.Length - decimals, "."));
    }
}
