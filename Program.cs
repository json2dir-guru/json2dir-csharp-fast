// json2dir: create the directory tree a JSON document describes, in the current directory.
// Implements RFC J2D-1 (https://github.com/kitsunoff/awesome-json2dir/blob/main/spec/rfc-json2dir.md).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

[assembly: DisableRuntimeMarshalling]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Json2dir;

static unsafe class Program
{
    static int Main(string[] args)
    {
        if (args.Length != 0)
        {
            WriteError("usage: json2dir < document.json\n");
            return 2;
        }
        try
        {
            byte* input = Input.ReadAll(out int length);
            // §3: strict UTF-8 over the whole input first (vectorised); the parser then deals in bytes only.
            if (!Utf8.IsValid(new ReadOnlySpan<byte>(input, length))) throw new Json2dirException("input is not valid UTF-8");
            var parser = new Parser(input, length);
            parser.Parse(); // §6: the complete document is parsed and validated before anything is written
            new Writer(input, parser.Nodes).WriteDirectory(Libc.AT_FDCWD, parser.RootFirst, parser.RootCount);
            return 0;
        }
        catch (Json2dirException e)
        {
            WriteError($"json2dir: {e.Message}\n");
            return 1;
        }
    }

    static void WriteError(string message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        fixed (byte* p = bytes) Libc.write(2, p, bytes.Length);
    }
}
