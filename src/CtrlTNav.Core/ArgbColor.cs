using System;
using System.Globalization;

namespace CtrlTNav.Core
{
    /// <summary>
    /// ARGB colour value shared between the Visual Studio integration (which can obtain the theme colours)
    /// and the WPF UI (which turns them into brushes). Using a plain struct keeps WPF/System.Drawing types
    /// out of assemblies that must not depend on them.
    /// </summary>
    public readonly struct ArgbColor : IEquatable<ArgbColor>
    {
        public ArgbColor(byte a, byte r, byte g, byte b)
        {
            A = a;
            R = r;
            G = g;
            B = b;
        }

        public byte A { get; }

        public byte R { get; }

        public byte G { get; }

        public byte B { get; }

        public static ArgbColor FromArgb(byte a, byte r, byte g, byte b)
        {
            return new ArgbColor(a, r, g, b);
        }

        public static ArgbColor FromRgb(byte r, byte g, byte b)
        {
            return new ArgbColor(255, r, g, b);
        }

        public static ArgbColor FromUInt32(uint argb)
        {
            return new ArgbColor(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));
        }

        public uint ToUInt32()
        {
            return ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;
        }

        /// <summary>Returns <c>#AARRGGBB</c>.</summary>
        public string ToHex()
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", A, R, G, B);
        }

        /// <summary>Returns a copy with the alpha channel replaced.</summary>
        public ArgbColor WithAlpha(byte alpha)
        {
            return new ArgbColor(alpha, R, G, B);
        }

        /// <summary>Blends towards <paramref name="other"/> using <paramref name="amount"/> 0..1.</summary>
        public ArgbColor Blend(ArgbColor other, double amount)
        {
            if (amount <= 0)
            {
                return this;
            }

            if (amount >= 1)
            {
                return other;
            }

            return new ArgbColor(
                (byte)(A + ((other.A - A) * amount)),
                (byte)(R + ((other.R - R) * amount)),
                (byte)(G + ((other.G - G) * amount)),
                (byte)(B + ((other.B - B) * amount)));
        }

        public bool Equals(ArgbColor other)
        {
            return A == other.A && R == other.R && G == other.G && B == other.B;
        }

        public override bool Equals(object? obj)
        {
            return obj is ArgbColor other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (A << 24) | (R << 16) | (G << 8) | B;
        }

        public override string ToString()
        {
            return ToHex();
        }
    }
}
