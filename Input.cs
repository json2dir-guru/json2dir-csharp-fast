// Gets the whole of stdin into memory, followed by Simd.Padding zero bytes, with as little copying and
// page-faulting as the kind of stdin allows.
using System;
using System.Runtime.InteropServices;

namespace Json2dir;

static unsafe class Input
{
    public static byte* ReadAll(out int length)
    {
        long here = Libc.lseek(0, 0, Libc.SEEK_CUR);
        if (here >= 0)
        {
            long end = Libc.lseek(0, 0, Libc.SEEK_END);
            if (end >= 0 && Libc.lseek(0, here, Libc.SEEK_SET) >= 0)
            {
                byte* mapped = TryMapFile(here, end - here, out length);
                if (mapped != null) return mapped;
                return ReadStream(end - here + 1, out length); // +1: room to see EOF
            }
        }
        // A pipe: a larger pipe buffer means fewer, larger reads and wake-ups (failure is harmless).
        Libc.fcntl(0, Libc.F_SETPIPE_SZ, 1 << 20);
        return ReadStream(1 << 20, out length);
    }

    /// <summary>
    /// A regular file is mapped instead of read: no copy, and MAP_POPULATE maps the cached pages in one go.
    /// The mapping is private, so the parser's in-place edits never reach the file. Only possible when the
    /// zero bytes after the end of the file in its last page cover the padding.
    /// </summary>
    static byte* TryMapFile(long offset, long size, out int length)
    {
        length = 0;
        long page = Environment.SystemPageSize;
        long tail = size % page;
        if (offset != 0 || size == 0 || size > int.MaxValue - Simd.Padding || tail == 0 || page - tail < Simd.Padding) return null;
        void* p = Libc.mmap(null, (nuint)size, Libc.PROT_READ | Libc.PROT_WRITE, Libc.MAP_PRIVATE | Libc.MAP_POPULATE, 0, 0);
        if (p == Libc.MAP_FAILED) return null;
        length = (int)size;
        return (byte*)p;
    }

    /// <summary>
    /// Reads into an anonymous mapping that grows with mremap, which moves pages instead of copying bytes.
    /// (Transparent huge pages were measured too: zeroing 2 MiB pages up front made pipe reads slower.)
    /// </summary>
    static byte* ReadStream(long capacity, out int length)
    {
        nuint mapped = (nuint)(capacity + Simd.Padding);
        byte* buffer = (byte*)Libc.mmap(null, mapped, Libc.PROT_READ | Libc.PROT_WRITE, Libc.MAP_PRIVATE | Libc.MAP_ANONYMOUS, -1, 0);
        if (buffer == Libc.MAP_FAILED) throw new Json2dirException("out of memory");
        long size = 0;
        while (true)
        {
            if (size == capacity)
            {
                capacity *= 2;
                if (capacity > int.MaxValue - Simd.Padding) throw new Json2dirException("input is too large");
                nuint larger = (nuint)(capacity + Simd.Padding);
                buffer = (byte*)Libc.mremap(buffer, mapped, larger, Libc.MREMAP_MAYMOVE);
                if (buffer == Libc.MAP_FAILED) throw new Json2dirException("out of memory");
                mapped = larger;
                    }
            nint n = Libc.read(0, buffer + size, (nint)(capacity - size));
            if (n == 0) break;
            if (n < 0)
            {
                int errno = Libc.Errno;
                if (errno == Libc.EINTR) continue;
                throw new Json2dirException($"cannot read standard input: {Marshal.GetPInvokeErrorMessage(errno)}");
            }
            size += n;
        }
        if (size > int.MaxValue - Simd.Padding) throw new Json2dirException("input is too large");
        // Fresh anonymous pages are zero, so the padding after the data already is.
        length = (int)size;
        return buffer;
    }
}
