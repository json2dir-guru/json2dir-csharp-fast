// The parsed document: a flat array of nodes whose strings are ranges into the input buffer.
using System;
using System.Text;

namespace Json2dir;

enum Kind : byte
{
    Directory,
    File,
    Link,
    Script,
    // JSON values that are not json2dir entries. Kept until duplicates are resolved
    // (the last occurrence wins), then rejected.
    Number,
    True,
    False,
    Null,
    BadArray,
    BadArrayKind,
}

/// <summary>
/// One object member. Name and value are byte ranges in the (in-place decoded) input buffer;
/// both are followed by a NUL byte, so they can be passed to libc as C strings.
/// For a directory, ValueStart/ValueLength are the first child index and child count in the node array.
/// </summary>
struct Node
{
    public int NameStart, NameLength;
    public int ValueStart, ValueLength;
    public Kind Kind;
}

sealed class Json2dirException(string message) : Exception(message)
{
    string? _path;

    public void PrependName(string name) => _path = _path is null ? name : name + "/" + _path;

    public override string Message => _path is null ? base.Message : $"{_path}: {base.Message}";
}

static class Text
{
    /// <summary>A name or value for diagnostics: JSON-style quoting of the UTF-8 bytes.</summary>
    public static string Show(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in Encoding.UTF8.GetString(bytes))
        {
            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else if (c is '"' or '\\') sb.Append('\\').Append(c);
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    public static string Plain(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}

/// <summary>
/// Small scalar byte-range helpers for names and kinds, which are short. (The span methods of CoreLib are
/// vectorised generics; calling them from this assembly costs a one-off load of the Vector types at startup,
/// which is more than these loops ever spend.)
/// </summary>
static unsafe class Bytes
{
    public static bool Equal(byte* p, int length, ReadOnlySpan<byte> literal)
    {
        if (length != literal.Length) return false;
        for (int i = 0; i < length; i++)
            if (p[i] != literal[i]) return false;
        return true;
    }

    public static bool Contains(byte* p, int length, byte value)
    {
        for (int i = 0; i < length; i++)
            if (p[i] == value) return true;
        return false;
    }

    /// <summary>Lexicographic order of the bytes (shorter first on a common prefix).</summary>
    public static int Compare(byte* a, int aLength, byte* b, int bLength)
    {
        int n = Math.Min(aLength, bLength);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i]) return a[i] - b[i];
        return aLength - bLength;
    }
}
