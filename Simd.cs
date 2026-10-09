// Vectorised search for the bytes that end a run of plain string content.
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Json2dir;

static unsafe class Simd
{
    /// <summary>Bytes past the end of the input that are readable and zero.</summary>
    public const int Padding = 64;

    /// <summary>
    /// Index of the first byte at or after <paramref name="p"/> that is '"', '\\' or a control byte (&lt; 0x20).
    /// The search never stops early: the zero padding after the input guarantees a hit, so the loop needs no
    /// bounds checks and always reads whole vectors.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint FindStringSpecial(byte* p)
    {
        if (Avx2.IsSupported) return FindAvx2(p);
        if (Vector128.IsHardwareAccelerated) return Find128(p);
        return FindScalar(p);
    }

    // AVX2 written with the x86 intrinsics themselves: under ReadyToRun these always compile to single
    // instructions, whereas the generic Vector256<T> operators live in CoreLib, outside the version bubble.
    static nint FindAvx2(byte* p)
    {
        var quote = Vector256.Create((byte)'"');
        var backslash = Vector256.Create((byte)'\\');
        var control = Vector256.Create((byte)0x1F);
        for (nint i = 0; ; i += 64)
        {
            var a = Avx.LoadVector256(p + i);
            var b = Avx.LoadVector256(p + i + 32);
            // x < 0x20  <=>  min(x, 0x1F) == x  (unsigned)
            var ma = Avx2.Or(Avx2.Or(Avx2.CompareEqual(a, quote), Avx2.CompareEqual(a, backslash)), Avx2.CompareEqual(Avx2.Min(a, control), a));
            var mb = Avx2.Or(Avx2.Or(Avx2.CompareEqual(b, quote), Avx2.CompareEqual(b, backslash)), Avx2.CompareEqual(Avx2.Min(b, control), b));
            ulong bits = (uint)Avx2.MoveMask(ma) | ((ulong)(uint)Avx2.MoveMask(mb) << 32);
            if (bits != 0) return i + BitOperations.TrailingZeroCount(bits);
        }
    }

    static nint Find128(byte* p)
    {
        var quote = Vector128.Create((byte)'"');
        var backslash = Vector128.Create((byte)'\\');
        var control = Vector128.Create((byte)0x1F);
        for (nint i = 0; ; i += 32)
        {
            var a = Vector128.Load(p + i);
            var b = Vector128.Load(p + i + 16);
            var ma = Vector128.Equals(a, quote) | Vector128.Equals(a, backslash) | Vector128.Equals(Vector128.Min(a, control), a);
            var mb = Vector128.Equals(b, quote) | Vector128.Equals(b, backslash) | Vector128.Equals(Vector128.Min(b, control), b);
            if ((ma | mb) != Vector128<byte>.Zero)
            {
                uint bits = ma.ExtractMostSignificantBits() | (mb.ExtractMostSignificantBits() << 16);
                return i + BitOperations.TrailingZeroCount(bits);
            }
        }
    }

    static nint FindScalar(byte* p)
    {
        nint i = 0;
        while (true)
        {
            byte c = p[i];
            if (c == '"' || c == '\\' || c < 0x20) return i;
            i++;
        }
    }
}
