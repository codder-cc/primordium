using System;

namespace Primordium;

// Deterministic elementary functions (open decision O8 of docs/DESIGN-LIFE-MODEL-2.md): exp and log
// built only from IEEE double addition, multiplication and division (and exact scaling by powers of
// two), so they give the same bits on every platform and runtime — unlike Math.Exp/MathF.Exp, which
// call the platform's library. Math.Sqrt is IEEE correctly rounded everywhere and may be used as is.
// Life model 2 uses these for everything it folds and for its tables; model 1 still uses MathF (moving
// it would change its hashes: a separate task).
public static class DetMath
{
    const double Ln2Hi = 6.93147180369123816490e-01, Ln2Lo = 1.90821492927058770002e-10, InvLn2 = 1.44269504088896338700e+00;

    // e^x, relative error ~1e-15; 0 below −745, +∞ above 709.
    public static double Exp(double x)
    {
        if (double.IsNaN(x)) return x;
        if (x > 709.7) return double.PositiveInfinity;
        if (x < -745.0) return 0;
        // x = k·ln2 + r, |r| ≤ ln2/2; e^r by its Taylor series to r^13 (Horner), then × 2^k.
        double kf = Math.Floor(x * InvLn2 + 0.5);
        int k = (int)kf;
        double r = (x - kf * Ln2Hi) - kf * Ln2Lo;
        double p = 1.0 / 6227020800.0;
        p = p * r + 1.0 / 479001600.0;
        p = p * r + 1.0 / 39916800.0;
        p = p * r + 1.0 / 3628800.0;
        p = p * r + 1.0 / 362880.0;
        p = p * r + 1.0 / 40320.0;
        p = p * r + 1.0 / 5040.0;
        p = p * r + 1.0 / 720.0;
        p = p * r + 1.0 / 120.0;
        p = p * r + 1.0 / 24.0;
        p = p * r + 1.0 / 6.0;
        p = p * r + 0.5;
        p = p * r + 1.0;
        p = p * r + 1.0;
        return Scale(p, k);
    }

    // ln x for x > 0 (NaN below 0, −∞ at 0).
    public static double Log(double x)
    {
        if (double.IsNaN(x) || x < 0) return double.NaN;
        if (x == 0) return double.NegativeInfinity;
        if (double.IsPositiveInfinity(x)) return x;
        // x = m·2^e with m in [√½, √2); ln m = 2·atanh(s), s = (m − 1)/(m + 1), by its odd series.
        long bits = BitConverter.DoubleToInt64Bits(x);
        int e = (int)((bits >> 52) & 0x7FF);
        if (e == 0) { x *= 18014398509481984.0; bits = BitConverter.DoubleToInt64Bits(x); e = (int)((bits >> 52) & 0x7FF) - 54; }
        e -= 1023;
        double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        if (m > 1.4142135623730951) { m *= 0.5; e++; }
        double s = (m - 1) / (m + 1), s2 = s * s, t = 0;
        for (int n = 27; n >= 1; n -= 2) t = t * s2 + 1.0 / n;
        return 2 * s * t + e * Ln2Hi + e * Ln2Lo;
    }

    public static double Pow(double x, double y) => x == 0 ? (y == 0 ? 1 : 0) : Exp(y * Log(x));

    // sin and cos for |x| ≤ π (Taylor series to x^27, Horner): enough for tables of headings.
    public static double Sin(double x)
    {
        double x2 = x * x, t = 0;
        for (int n = 27; n >= 3; n -= 2) t = (t + 1) * -x2 / (n * (n - 1));
        return x * (1 + t);
    }

    public static double Cos(double x)
    {
        double x2 = x * x, t = 0;
        for (int n = 28; n >= 2; n -= 2) t = (t + 1) * -x2 / (n * (n - 1));
        return 1 + t;
    }

    // p·2^k exactly (as long as the result is a normal number).
    static double Scale(double p, int k)
    {
        while (k > 1023) { p *= 8.98846567431158e307; k -= 1023; }
        while (k < -1022) { p *= 2.2250738585072014e-308; k += 1022; }
        return p * BitConverter.Int64BitsToDouble((long)(k + 1023) << 52);
    }
}
