using System;

namespace Primordium;

// A fractional amount of molecules (loose matter, burials, partial uptake, protein substrate) in
// fixed point: a whole number of 2⁻³² molecule. Adding and subtracting are integer operations and
// exact, so moving an amount from one pool to another conserves atoms exactly however full the
// pools are. (In float a pool of thousands rounds every small addition to ~10⁻⁴ of a molecule; in
// boomed worlds that summed to atoms appearing and vanishing — CHANGELOG 2026-10-06 (7).)
// A float or double amount is rounded to the grid once, symmetrically (−x gives −q(x)), when it is
// converted: taking −m here and putting +m there move exactly the same quantity.
// Reading is implicit to double (exact); to float it must be explicit (it rounds).
public readonly struct Qty : IEquatable<Qty>, IComparable<Qty>
{
    public const int Bits = 32;
    public const double One = 4294967296.0;   // 2^Bits
    const double Inv = 1.0 / One;
    public readonly long Raw;
    Qty(long raw) => Raw = raw;

    public static readonly Qty Zero = default;
    public static Qty FromRaw(long raw) => new(raw);
    public static Qty Of(double x)
    {
        double r = x * One;
        return new((long)(r >= 0 ? r + 0.5 : r - 0.5));   // round half away from zero: symmetric in sign
    }

    public double D => Raw * Inv;
    public float F => (float)(Raw * Inv);

    public static implicit operator Qty(int n) => new((long)n << Bits);
    public static implicit operator Qty(float x) => Of(x);
    public static implicit operator Qty(double x) => Of(x);
    public static implicit operator double(Qty q) => q.Raw * Inv;
    public static explicit operator float(Qty q) => (float)(q.Raw * Inv);

    public static Qty operator +(Qty a, Qty b) => new(a.Raw + b.Raw);
    public static Qty operator -(Qty a, Qty b) => new(a.Raw - b.Raw);
    public static Qty operator -(Qty a) => new(-a.Raw);
    public static Qty operator ++(Qty a) => new(a.Raw + (1L << Bits));
    public static Qty operator --(Qty a) => new(a.Raw - (1L << Bits));
    public static bool operator ==(Qty a, Qty b) => a.Raw == b.Raw;
    public static bool operator !=(Qty a, Qty b) => a.Raw != b.Raw;
    public static bool operator <(Qty a, Qty b) => a.Raw < b.Raw;
    public static bool operator >(Qty a, Qty b) => a.Raw > b.Raw;
    public static bool operator <=(Qty a, Qty b) => a.Raw <= b.Raw;
    public static bool operator >=(Qty a, Qty b) => a.Raw >= b.Raw;
    public static Qty Min(Qty a, Qty b) => a.Raw <= b.Raw ? a : b;
    public static Qty Max(Qty a, Qty b) => a.Raw >= b.Raw ? a : b;

    public bool Equals(Qty o) => Raw == o.Raw;
    public override bool Equals(object o) => o is Qty q && q.Raw == Raw;
    public override int GetHashCode() => Raw.GetHashCode();
    public int CompareTo(Qty o) => Raw.CompareTo(o.Raw);
    public override string ToString() => D.ToString("R");
}
