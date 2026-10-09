// A strict RFC 8259 parser that works on the raw UTF-8 input and builds the json2dir tree directly.
//
// - Iterative: nesting is tracked on an explicit stack, so deep documents cannot overflow the call stack.
// - Zero-copy: strings are ranges of the input buffer. Escaped strings are decoded in place (a decoded
//   string is never longer than its escaped form) and every string is NUL-terminated in place, which
//   lets the writer hand names and link targets to libc without copying.
// - When an object closes, its members are sorted by name bytes, duplicates are dropped (the last one
//   wins), and names and values are validated. The whole document is therefore checked before anything
//   is written.
using System;
using System.Runtime.CompilerServices;

namespace Json2dir;

sealed unsafe class Parser
{
    /// <summary>Object nesting limit below the root (RFC J2D-1 requires at least 64). Each level holds a
    /// directory descriptor while it is written.</summary>
    public const int MaxDepth = 512;
    const int MaxSkipDepth = 10_000;

    readonly byte* _p;
    readonly int _length;
    int _pos;

    // Members of the objects that are still open, innermost last.
    Node[] _pending = new Node[64];
    int _pendingCount;

    // Members of closed objects; each object's members are contiguous and sorted.
    Node[] _nodes = new Node[64];
    int _nodeCount;

    struct Frame
    {
        public int DirectoryIndex; // index in _pending of the member that opened this object; -1 for the root
        public int FirstChild;     // where its members start in _pending
    }

    Frame[] _frames = new Frame[16];
    int _depth;

    public Node[] Nodes => _nodes;
    public int RootFirst { get; private set; }
    public int RootCount { get; private set; }

    public Parser(byte* input, int length)
    {
        _p = input;
        _length = length;
    }

    public void Parse()
    {
        // §3: a byte order mark at the start may be ignored.
        if (_length >= 3 && _p[0] == 0xEF && _p[1] == 0xBB && _p[2] == 0xBF) _pos = 3;
        SkipWhitespace();
        if (_pos >= _length) Fail("unexpected end of input");
        if (_p[_pos] != '{') throw new Json2dirException("the root of the document must be an object");
        _pos++;
        PushFrame(-1);

        // Each iteration handles one position inside an object: just after '{' or after ','.
        bool afterComma = false;
        while (true)
        {
            SkipWhitespace();
            if (!afterComma && _p[_pos] == '}')
            {
                _pos++;
                if (CloseObject()) return;
            }
            else
            {
                if (ParseMember())
                {
                    // The member opened an object: continue inside it.
                    afterComma = false;
                    continue;
                }
            }

            // After a value: ',' or the end of the object (possibly several).
            while (true)
            {
                SkipWhitespace();
                byte c = _p[_pos];
                if (c == ',')
                {
                    _pos++;
                    afterComma = true;
                    break;
                }
                if (c != '}') Fail(_pos >= _length ? "unexpected end of input" : "expected ',' or '}'");
                _pos++;
                if (CloseObject()) return;
            }
        }
    }

    /// <summary>Parses `"name": value`. Returns true if the value is an object that is now open.</summary>
    bool ParseMember()
    {
        if (_p[_pos] != '"') Fail(_pos >= _length ? "unexpected end of input" : "expected a member name");
        int nameStart = ParseString(out int nameLength);
        SkipWhitespace();
        if (_p[_pos] != ':') Fail("expected ':'");
        _pos++;
        SkipWhitespace();

        ref Node node = ref AddPending();
        node.NameStart = nameStart;
        node.NameLength = nameLength;
        switch (_p[_pos])
        {
            case (byte)'"':
                node.Kind = Kind.File;
                node.ValueStart = ParseString(out node.ValueLength);
                return false;
            case (byte)'{':
                node.Kind = Kind.Directory;
                _pos++;
                if (_depth > MaxDepth) Fail($"objects are nested deeper than {MaxDepth} levels");
                PushFrame(_pendingCount - 1);
                return true;
            case (byte)'[':
                ParseArray(ref node);
                return false;
            case (byte)'t':
                Literal("true"u8);
                node.Kind = Kind.True;
                return false;
            case (byte)'f':
                Literal("false"u8);
                node.Kind = Kind.False;
                return false;
            case (byte)'n':
                Literal("null"u8);
                node.Kind = Kind.Null;
                return false;
            default:
                ParseNumber();
                node.Kind = Kind.Number;
                return false;
        }
    }

    /// <summary>["link", target] or ["script", content]; anything else is parsed as JSON and marked bad.</summary>
    void ParseArray(ref Node node)
    {
        _pos++; // '['
        node.Kind = Kind.BadArray;
        SkipWhitespace();
        if (_p[_pos] != '"')
        {
            SkipRest(SkipState.Open);
            return;
        }
        int kindStart = ParseString(out int kindLength);
        SkipWhitespace();
        if (_p[_pos] != ',')
        {
            SkipRest(SkipState.AfterValue);
            return;
        }
        _pos++;
        SkipWhitespace();
        if (_p[_pos] != '"')
        {
            SkipRest(SkipState.AfterComma);
            return;
        }
        node.ValueStart = ParseString(out node.ValueLength);
        SkipWhitespace();
        if (_p[_pos] != ']')
        {
            SkipRest(SkipState.AfterValue);
            return;
        }
        _pos++;
        node.Kind = Bytes.Equal(_p + kindStart, kindLength, "link"u8) ? Kind.Link
            : Bytes.Equal(_p + kindStart, kindLength, "script"u8) ? Kind.Script
            : Kind.BadArrayKind;
        if (node.Kind == Kind.BadArrayKind)
        {
            // Remember the kind for the diagnostic.
            node.ValueStart = kindStart;
            node.ValueLength = kindLength;
        }
    }

    enum SkipState { Open, AfterComma, AfterValue }

    /// <summary>Skips (while fully checking) the rest of an array whose '[' has been consumed.</summary>
    void SkipRest(SkipState state)
    {
        // Container stack: true = object, false = array.
        var stack = new bool[MaxSkipDepth];
        int top = 0;
        stack[0] = false;
        while (true)
        {
            SkipWhitespace();
            byte c = _p[_pos];
            if (state == SkipState.AfterValue || (state == SkipState.Open && c == (stack[top] ? '}' : ']')))
            {
                if (state == SkipState.AfterValue && c == ',')
                {
                    _pos++;
                    state = SkipState.AfterComma;
                    continue;
                }
                if (c != (stack[top] ? '}' : ']')) Fail(_pos >= _length ? "unexpected end of input" : "expected ',' or a closing bracket");
                _pos++;
                if (top-- == 0) return;
                state = SkipState.AfterValue;
                continue;
            }
            if (stack[top])
            {
                if (c != '"') Fail(_pos >= _length ? "unexpected end of input" : "expected a member name");
                ParseString(out _);
                SkipWhitespace();
                if (_p[_pos] != ':') Fail("expected ':'");
                _pos++;
                SkipWhitespace();
                c = _p[_pos];
            }
            switch (c)
            {
                case (byte)'"': ParseString(out _); state = SkipState.AfterValue; break;
                case (byte)'[':
                case (byte)'{':
                    if (++top == MaxSkipDepth) Fail("values are nested too deeply");
                    stack[top] = c == '{';
                    _pos++;
                    state = SkipState.Open;
                    break;
                case (byte)'t': Literal("true"u8); state = SkipState.AfterValue; break;
                case (byte)'f': Literal("false"u8); state = SkipState.AfterValue; break;
                case (byte)'n': Literal("null"u8); state = SkipState.AfterValue; break;
                default: ParseNumber(); state = SkipState.AfterValue; break;
            }
        }
    }

    void Literal(ReadOnlySpan<byte> word)
    {
        // The zero padding makes reading past the end safe; it never matches a letter.
        if (!Bytes.Equal(_p + _pos, word.Length, word)) Fail("invalid literal");
        _pos += word.Length;
    }

    void ParseNumber()
    {
        if (_p[_pos] == '-') _pos++;
        if (_p[_pos] == '0') _pos++;
        else if (IsDigit(_p[_pos])) SkipDigits();
        else Fail(_pos >= _length ? "unexpected end of input" : "unexpected character");
        if (_p[_pos] == '.')
        {
            _pos++;
            if (!IsDigit(_p[_pos])) Fail("invalid number");
            SkipDigits();
        }
        if (_p[_pos] is (byte)'e' or (byte)'E')
        {
            _pos++;
            if (_p[_pos] is (byte)'+' or (byte)'-') _pos++;
            if (!IsDigit(_p[_pos])) Fail("invalid number");
            SkipDigits();
        }
    }

    static bool IsDigit(byte c) => (uint)(c - '0') <= 9;

    void SkipDigits()
    {
        while (IsDigit(_p[_pos])) _pos++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void SkipWhitespace()
    {
        while (true)
        {
            byte c = _p[_pos];
            if (c != ' ' && c != '\n' && c != '\r' && c != '\t') return;
            _pos++;
        }
    }

    /// <summary>
    /// Parses a string starting at its opening quote. Plain runs are skipped with SIMD; escapes are decoded
    /// in place. Returns the start of the decoded bytes, which are followed by a NUL written in place.
    /// </summary>
    int ParseString(out int length)
    {
        int start = ++_pos;
        int pos = start + (int)Simd.FindStringSpecial(_p + start);
        byte c = _p[pos];
        if (c == '"')
        {
            _p[pos] = 0;
            _pos = pos + 1;
            length = pos - start;
            return start;
        }
        return ParseEscapedString(start, pos, out length);
    }

    int ParseEscapedString(int start, int pos, out int length)
    {
        int write = pos; // decoded bytes go here; always at or behind pos
        while (true)
        {
            byte c = _p[pos];
            if (c == '"') break;
            if (c != '\\')
            {
                _pos = pos;
                Fail(pos >= _length ? "unterminated string" : "control character in string");
            }
            pos++;
            switch (_p[pos++])
            {
                case (byte)'"': _p[write++] = (byte)'"'; break;
                case (byte)'\\': _p[write++] = (byte)'\\'; break;
                case (byte)'/': _p[write++] = (byte)'/'; break;
                case (byte)'b': _p[write++] = (byte)'\b'; break;
                case (byte)'f': _p[write++] = (byte)'\f'; break;
                case (byte)'n': _p[write++] = (byte)'\n'; break;
                case (byte)'r': _p[write++] = (byte)'\r'; break;
                case (byte)'t': _p[write++] = (byte)'\t'; break;
                case (byte)'u':
                    int code = Hex4(pos);
                    pos += 4;
                    if (code is >= 0xD800 and <= 0xDFFF)
                    {
                        // §3.5: only a high surrogate followed by a low one forms a character.
                        if (code >= 0xDC00 || _p[pos] != '\\' || _p[pos + 1] != 'u')
                            FailAt(pos, "string contains an unpaired surrogate");
                        int low = Hex4(pos + 2);
                        if (low is < 0xDC00 or > 0xDFFF) FailAt(pos, "string contains an unpaired surrogate");
                        pos += 6;
                        code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                    }
                    write += EncodeUtf8(code, _p + write);
                    break;
                default:
                    FailAt(pos - 1, "invalid escape");
                    break;
            }
            // Copy the next plain run down to the write position.
            int run = (int)Simd.FindStringSpecial(_p + pos);
            if (run > 0)
            {
                new ReadOnlySpan<byte>(_p + pos, run).CopyTo(new Span<byte>(_p + write, run));
                pos += run;
                write += run;
            }
        }
        _p[write] = 0;
        _pos = pos + 1;
        length = write - start;
        return start;
    }

    int Hex4(int at)
    {
        int code = 0;
        for (int i = 0; i < 4; i++)
        {
            int h = HexValue(_p[at + i]);
            if (h < 0) FailAt(at, "invalid \\u escape");
            code = (code << 4) | h;
        }
        return code;
    }

    static int HexValue(byte c)
    {
        if ((uint)(c - '0') <= 9) return c - '0';
        uint lower = (uint)(c | 0x20) - 'a';
        return lower <= 5 ? (int)lower + 10 : -1;
    }

    static int EncodeUtf8(int code, byte* dst)
    {
        if (code < 0x80)
        {
            dst[0] = (byte)code;
            return 1;
        }
        if (code < 0x800)
        {
            dst[0] = (byte)(0xC0 | (code >> 6));
            dst[1] = (byte)(0x80 | (code & 0x3F));
            return 2;
        }
        if (code < 0x10000)
        {
            dst[0] = (byte)(0xE0 | (code >> 12));
            dst[1] = (byte)(0x80 | ((code >> 6) & 0x3F));
            dst[2] = (byte)(0x80 | (code & 0x3F));
            return 3;
        }
        dst[0] = (byte)(0xF0 | (code >> 18));
        dst[1] = (byte)(0x80 | ((code >> 12) & 0x3F));
        dst[2] = (byte)(0x80 | ((code >> 6) & 0x3F));
        dst[3] = (byte)(0x80 | (code & 0x3F));
        return 4;
    }

    ref Node AddPending()
    {
        if (_pendingCount == _pending.Length) Grow(ref _pending, _pendingCount + 1);
        ref Node node = ref _pending[_pendingCount++];
        node = default;
        return ref node;
    }

    void PushFrame(int directoryIndex)
    {
        if (_depth == _frames.Length) Grow(ref _frames, _depth + 1);
        _frames[_depth++] = new Frame { DirectoryIndex = directoryIndex, FirstChild = _pendingCount };
    }

    // The node arrays are handled with plain indices and Array.Copy rather than Span<Node>, Array.Resize<Node>
    // or Span<Node>.Sort: instantiations of CoreLib generics over this assembly's structs cannot be
    // precompiled by ReadyToRun (CoreLib is outside the version bubble) and would be JIT-compiled at startup.
    static void Grow<T>(ref T[] array, int minLength)
    {
        var larger = new T[Math.Max(array.Length * 2, minLength)];
        Array.Copy(array, larger, array.Length);
        array = larger;
    }

    /// <summary>Finishes the innermost object. Returns true when it was the root and the document is complete.</summary>
    bool CloseObject()
    {
        Frame frame = _frames[--_depth];
        int start = frame.FirstChild;
        int count = SortAndDeduplicate(start, _pendingCount - start);

        int first = _nodeCount;
        if (_nodeCount + count > _nodes.Length) Grow(ref _nodes, _nodeCount + count);
        for (int i = start; i < start + count; i++)
        {
            Validate(ref _pending[i]);
            _nodes[_nodeCount++] = _pending[i];
        }
        _pendingCount = start;

        if (frame.DirectoryIndex >= 0)
        {
            ref Node directory = ref _pending[frame.DirectoryIndex];
            directory.ValueStart = first;
            directory.ValueLength = count;
            return false;
        }

        RootFirst = first;
        RootCount = count;
        SkipWhitespace();
        if (_pos != _length) Fail("unexpected data after the JSON value");
        return true;
    }

    /// <summary>
    /// Sorts _pending[start..start+length) by name bytes and keeps only the last occurrence of each name
    /// (packed at the front). Returns the number of members kept.
    /// </summary>
    int SortAndDeduplicate(int start, int length)
    {
        int end = start + length;
        // Generated documents are often already sorted without duplicates: check that first.
        int i = start + 1;
        while (i < end && CompareNames(_pending[i - 1], _pending[i]) < 0) i++;
        if (i >= end) return length;

        // Stable merge sort: members with equal names keep their document order.
        if (_scratch.Length < length) _scratch = new Node[Math.Max(length, _scratch.Length * 2)];
        MergeSort(start, end);

        int kept = start;
        for (i = start; i < end; i++)
        {
            if (i + 1 < end && CompareNames(_pending[i], _pending[i + 1]) == 0) continue; // a later duplicate wins
            _pending[kept++] = _pending[i];
        }
        return kept - start;
    }

    Node[] _scratch = new Node[16];

    void MergeSort(int lo, int hi)
    {
        if (hi - lo <= 12)
        {
            for (int i = lo + 1; i < hi; i++) // insertion sort for short runs
            {
                Node x = _pending[i];
                int j = i - 1;
                while (j >= lo && CompareNames(_pending[j], x) > 0)
                {
                    _pending[j + 1] = _pending[j];
                    j--;
                }
                _pending[j + 1] = x;
            }
            return;
        }
        int mid = lo + (hi - lo) / 2;
        MergeSort(lo, mid);
        MergeSort(mid, hi);
        if (CompareNames(_pending[mid - 1], _pending[mid]) <= 0) return;
        Array.Copy(_pending, lo, _scratch, 0, mid - lo);
        int a = 0, aEnd = mid - lo, b = mid, k = lo;
        while (a < aEnd && b < hi) _pending[k++] = CompareNames(_pending[b], _scratch[a]) < 0 ? _pending[b++] : _scratch[a++];
        while (a < aEnd) _pending[k++] = _scratch[a++];
    }

    int CompareNames(in Node a, in Node b) =>
        Bytes.Compare(_p + a.NameStart, a.NameLength, _p + b.NameStart, b.NameLength);

    /// <summary>§4: checks a member that survived duplicate resolution.</summary>
    void Validate(ref Node member)
    {
        var name = new ReadOnlySpan<byte>(_p + member.NameStart, member.NameLength);
        // §4.2.1: names are used exactly; trailing "/" or "/." forms are rejected, not trimmed.
        byte* namePtr = _p + member.NameStart;
        if (name.Length == 0 || Bytes.Equal(namePtr, name.Length, "."u8) || Bytes.Equal(namePtr, name.Length, ".."u8)
            || Bytes.Contains(namePtr, name.Length, (byte)'/') || Bytes.Contains(namePtr, name.Length, 0))
            FailMember(member, $"invalid name {Text.Show(name)}");
        switch (member.Kind)
        {
            case Kind.Number: FailMember(member, "number values are not allowed"); break;
            case Kind.True: FailMember(member, "true values are not allowed"); break;
            case Kind.False: FailMember(member, "false values are not allowed"); break;
            case Kind.Null: FailMember(member, "null values are not allowed"); break;
            case Kind.BadArray:
                FailMember(member, "an array must be [\"link\", target] or [\"script\", content]");
                break;
            case Kind.BadArrayKind:
                FailMember(member, $"unknown array kind {Text.Show(new ReadOnlySpan<byte>(_p + member.ValueStart, member.ValueLength))}");
                break;
            case Kind.Link:
                // §10: a link target with NUL cannot be created.
                if (Bytes.Contains(_p + member.ValueStart, member.ValueLength, 0))
                    FailMember(member, "a link target cannot contain NUL");
                break;
        }
    }

    void FailMember(in Node member, string message)
    {
        var e = new Json2dirException(message);
        e.PrependName(Text.Plain(new ReadOnlySpan<byte>(_p + member.NameStart, member.NameLength)));
        // Names of the enclosing objects, innermost first (the root frame has no name).
        for (int d = _depth; d >= 1; d--)
        {
            ref Node dir = ref _pending[_frames[d].DirectoryIndex];
            e.PrependName(Text.Plain(new ReadOnlySpan<byte>(_p + dir.NameStart, dir.NameLength)));
        }
        throw e;
    }

    void FailAt(int pos, string message)
    {
        _pos = pos;
        Fail(message);
    }

    void Fail(string message) => throw new Json2dirException($"input is not valid JSON: {message} at offset {_pos}");
}
