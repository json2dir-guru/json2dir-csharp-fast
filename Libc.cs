// Thin P/Invoke layer over the few libc calls json2dir needs (Linux only).
// All signatures are blittable and the assembly disables runtime marshalling, so ReadyToRun
// precompiles the call stubs and no IL stub has to be generated at startup.
// Callers read Errno right after a failing call.
using System.Runtime.InteropServices;

namespace Json2dir;

static unsafe partial class Libc
{
    const string Lib = "libc.so.6";

    public const int AT_FDCWD = -100;
    public const int AT_REMOVEDIR = 0x200;
    public const int SEEK_SET = 0, SEEK_CUR = 1, SEEK_END = 2;

    public const int EINTR = 4, EEXIST = 17, EISDIR = 21;

    public const int PROT_READ = 1, PROT_WRITE = 2;
    public const int MAP_PRIVATE = 2, MAP_ANONYMOUS = 0x20, MAP_POPULATE = 0x8000;
    public const int MREMAP_MAYMOVE = 1;
    public static readonly void* MAP_FAILED = (void*)-1;
    public const int F_SETPIPE_SZ = 1031;

    public const int O_RDONLY = 0, O_WRONLY = 1, O_CREAT = 0x40, O_EXCL = 0x80, O_CLOEXEC = 0x80000;

    // O_DIRECTORY and O_NOFOLLOW differ between the generic Linux ABI (x86, x64, RISC-V, ...) and ARM.
    static readonly bool IsArm = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64;
    public static readonly int O_DIRECTORY = IsArm ? 0x4000 : 0x10000;
    public static readonly int O_NOFOLLOW = IsArm ? 0x8000 : 0x20000;

    [LibraryImport(Lib)]
    public static partial nint read(int fd, byte* buf, nint count);

    [LibraryImport(Lib)]
    public static partial nint write(int fd, byte* buf, nint count);

    [LibraryImport(Lib)]
    public static partial long lseek(int fd, long offset, int whence);

    [LibraryImport(Lib)]
    public static partial int openat(int dirfd, byte* path, int flags, uint mode);

    [LibraryImport(Lib)]
    public static partial int close(int fd);

    [LibraryImport(Lib)]
    public static partial int fchmod(int fd, uint mode);

    [LibraryImport(Lib)]
    public static partial int mkdirat(int dirfd, byte* path, uint mode);

    [LibraryImport(Lib)]
    public static partial int symlinkat(byte* target, int dirfd, byte* path);

    [LibraryImport(Lib)]
    public static partial int unlinkat(int dirfd, byte* path, int flags);

    [LibraryImport(Lib)]
    public static partial uint umask(uint mask);

    [LibraryImport(Lib)]
    public static partial int fcntl(int fd, int cmd, int arg);

    [LibraryImport(Lib)]
    public static partial void* mmap(void* addr, nuint length, int prot, int flags, int fd, long offset);

    [LibraryImport(Lib)]
    public static partial void* mremap(void* oldAddress, nuint oldSize, nuint newSize, int flags);

    /// <summary>errno of the call that just returned. Read directly instead of SetLastError = true, which
    /// would store it in a thread-static field whose access helper is not precompiled (and so would load the JIT).</summary>
    public static int Errno => Marshal.GetLastSystemError();
}
