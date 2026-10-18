using System;

namespace CtrlTNav.Core.Model
{
    /// <summary>A half-open range of characters, used for match highlighting.</summary>
    public readonly struct TextRange : IEquatable<TextRange>
    {
        public TextRange(int start, int length)
        {
            Start = start;
            Length = length;
        }

        public int Start { get; }

        public int Length { get; }

        public int End
        {
            get { return Start + Length; }
        }

        public bool Contains(int index)
        {
            return index >= Start && index < End;
        }

        public bool Equals(TextRange other)
        {
            return Start == other.Start && Length == other.Length;
        }

        public override bool Equals(object? obj)
        {
            return obj is TextRange other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (Start * 397) ^ Length;
        }

        public override string ToString()
        {
            return "[" + Start + "," + Length + ")";
        }
    }
}
