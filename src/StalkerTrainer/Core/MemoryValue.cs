using System.Buffers.Binary;
using System.Globalization;

namespace StalkerTrainer.Core;

public enum ValueKind { Int32, Float32, Int64, Float64 }
public enum FilterKind { Equals, Changed, Unchanged, Increased, Decreased }

public readonly record struct MemoryValue(ValueKind Kind, ulong Bits)
{
    public int Size => Kind is ValueKind.Int32 or ValueKind.Float32 ? 4 : 8;
    public static int SizeOf(ValueKind kind) => kind is ValueKind.Int32 or ValueKind.Float32 ? 4 : 8;

    public static MemoryValue Parse(string text, ValueKind kind)
    {
        text = text.Trim().Replace(',', '.');
        return kind switch
        {
            ValueKind.Int32 => new(kind, unchecked((uint)int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture))),
            ValueKind.Int64 => new(kind, unchecked((ulong)long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture))),
            ValueKind.Float32 => FloatValue(float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)),
            ValueKind.Float64 => DoubleValue(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static MemoryValue FloatValue(float value) => float.IsFinite(value)
        ? new(ValueKind.Float32, BitConverter.SingleToUInt32Bits(value))
        : throw new FormatException("Нужно конечное число.");
    private static MemoryValue DoubleValue(double value) => double.IsFinite(value)
        ? new(ValueKind.Float64, BitConverter.DoubleToUInt64Bits(value))
        : throw new FormatException("Нужно конечное число.");

    public static MemoryValue Read(ReadOnlySpan<byte> bytes, ValueKind kind) => new(kind,
        SizeOf(kind) == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt64LittleEndian(bytes));

    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        if (Size == 4) BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)Bits);
        else BinaryPrimitives.WriteUInt64LittleEndian(bytes, Bits);
        return bytes;
    }

    public bool Matches(MemoryValue previous, FilterKind filter, MemoryValue? target)
    {
        if (Kind != previous.Kind) throw new ArgumentException("Типы значений не совпадают.");
        if (filter == FilterKind.Equals)
        {
            if (target is null || target.Value.Kind != Kind) throw new ArgumentException("Укажи значение нужного типа.");
            return Bits == target.Value.Bits;
        }
        if (filter == FilterKind.Changed) return Bits != previous.Bits;
        if (filter == FilterKind.Unchanged) return Bits == previous.Bits;
        var comparison = Kind switch
        {
            ValueKind.Int32 => unchecked((int)Bits).CompareTo(unchecked((int)previous.Bits)),
            ValueKind.Int64 => unchecked((long)Bits).CompareTo(unchecked((long)previous.Bits)),
            ValueKind.Float32 => CompareFloat(BitConverter.UInt32BitsToSingle((uint)Bits), BitConverter.UInt32BitsToSingle((uint)previous.Bits)),
            ValueKind.Float64 => CompareFloat(BitConverter.UInt64BitsToDouble(Bits), BitConverter.UInt64BitsToDouble(previous.Bits)),
            _ => throw new ArgumentOutOfRangeException()
        };
        return filter == FilterKind.Increased ? comparison == 1 : comparison == -1;
    }

    private static int CompareFloat(double a, double b) => double.IsFinite(a) && double.IsFinite(b) ? a.CompareTo(b) : 0;

    public override string ToString() => Kind switch
    {
        ValueKind.Int32 => unchecked((int)Bits).ToString(CultureInfo.InvariantCulture),
        ValueKind.Int64 => unchecked((long)Bits).ToString(CultureInfo.InvariantCulture),
        ValueKind.Float32 => BitConverter.UInt32BitsToSingle((uint)Bits).ToString("G9", CultureInfo.InvariantCulture),
        ValueKind.Float64 => BitConverter.UInt64BitsToDouble(Bits).ToString("G17", CultureInfo.InvariantCulture),
        _ => "?"
    };
}
