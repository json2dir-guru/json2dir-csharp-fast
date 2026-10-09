// Materialises the validated tree with *at() system calls relative to open directory descriptors.
//
// Entries are created optimistically: in an empty target a file costs openat + write + close and a
// directory mkdirat + openat + close. Only when creation fails with EEXIST is the old entry examined:
// - unlinkat succeeds for any non-directory (a symlink is removed itself, never followed, §5.3) and
//   fails with EISDIR for a directory, which is "in the way" of a non-object (§5.4);
// - for an object, openat(O_DIRECTORY | O_NOFOLLOW) succeeds only for a real directory, which is reused
//   (§5.1); anything else is unlinked and replaced by a new directory.
using System;

namespace Json2dir;

sealed unsafe class Writer(byte* input, Node[] nodes)
{
    uint _scriptMode; // 0666 & ~umask | 0111, computed on first use

    public void WriteDirectory(int dirFd, int first, int count)
    {
        for (int i = first; i < first + count; i++)
        {
            ref Node node = ref nodes[i];
            byte* name = input + node.NameStart;
            try
            {
                switch (node.Kind)
                {
                    case Kind.File:
                        WriteFile(dirFd, name, input + node.ValueStart, node.ValueLength, executable: false);
                        break;
                    case Kind.Script:
                        WriteFile(dirFd, name, input + node.ValueStart, node.ValueLength, executable: true);
                        break;
                    case Kind.Link:
                        CreateLink(dirFd, name, input + node.ValueStart);
                        break;
                    case Kind.Directory:
                        if (node.ValueLength == 0)
                        {
                            OpenDirectory(dirFd, name, open: false);
                            break;
                        }
                        int fd = OpenDirectory(dirFd, name, open: true);
                        try
                        {
                            WriteDirectory(fd, node.ValueStart, node.ValueLength);
                        }
                        finally
                        {
                            Libc.close(fd);
                        }
                        break;
                }
            }
            catch (Json2dirException e)
            {
                e.PrependName(Text.Plain(new ReadOnlySpan<byte>(name, node.NameLength)));
                throw;
            }
        }
    }

    void WriteFile(int dirFd, byte* name, byte* content, int length, bool executable)
    {
        int flags = Libc.O_WRONLY | Libc.O_CREAT | Libc.O_EXCL | Libc.O_NOFOLLOW | Libc.O_CLOEXEC;
        int fd = Libc.openat(dirFd, name, flags, 0x1B6 /* 0666 */);
        if (fd < 0 && Libc.Errno == Libc.EEXIST)
        {
            RemoveNonDirectory(dirFd, name);
            fd = Libc.openat(dirFd, name, flags, 0x1B6);
        }
        if (fd < 0) Fail("cannot create file");
        try
        {
            while (length > 0)
            {
                nint written = Libc.write(fd, content, length);
                if (written < 0)
                {
                    if (Libc.Errno == Libc.EINTR) continue;
                    Fail("cannot write file");
                }
                content += written;
                length -= (int)written;
            }
            // §4.4.2: add execute permission for owner, group and others.
            if (executable && Libc.fchmod(fd, ScriptMode()) != 0) Fail("cannot make the script executable");
        }
        finally
        {
            Libc.close(fd);
        }
    }

    void CreateLink(int dirFd, byte* name, byte* target)
    {
        if (Libc.symlinkat(target, dirFd, name) == 0) return;
        if (Libc.Errno == Libc.EEXIST)
        {
            RemoveNonDirectory(dirFd, name);
            if (Libc.symlinkat(target, dirFd, name) == 0) return;
        }
        Fail("cannot create symbolic link");
    }

    /// <summary>Creates or reuses a directory and returns a descriptor for it (-1 if <paramref name="open"/> is false).</summary>
    static int OpenDirectory(int dirFd, byte* name, bool open)
    {
        int flags = Libc.O_RDONLY | Libc.O_DIRECTORY | Libc.O_NOFOLLOW | Libc.O_CLOEXEC;
        if (Libc.mkdirat(dirFd, name, 0x1FF /* 0777 */) != 0)
        {
            if (Libc.Errno != Libc.EEXIST) Fail("cannot create directory");
            int existing = Libc.openat(dirFd, name, flags, 0);
            if (existing >= 0) // §5.1: a real directory is reused
            {
                if (open) return existing;
                Libc.close(existing);
                return -1;
            }
            // §5.2, §5.3: a file or a symbolic link (even one to a directory) is replaced.
            if (Libc.unlinkat(dirFd, name, 0) != 0) Fail("cannot remove the existing entry");
            if (Libc.mkdirat(dirFd, name, 0x1FF) != 0) Fail("cannot create directory");
        }
        if (!open) return -1;
        int fd = Libc.openat(dirFd, name, flags, 0);
        if (fd < 0) Fail("cannot open directory");
        return fd;
    }

    static void RemoveNonDirectory(int dirFd, byte* name)
    {
        if (Libc.unlinkat(dirFd, name, 0) == 0) return;
        if (Libc.Errno == Libc.EISDIR) throw new Json2dirException("a directory is in the way");
        Fail("cannot remove the existing entry");
    }

    uint ScriptMode()
    {
        if (_scriptMode == 0)
        {
            uint mask = Libc.umask(0);
            Libc.umask(mask);
            _scriptMode = (0x1B6u & ~mask) | 0x49u; // (0666 & ~umask) | 0111
        }
        return _scriptMode;
    }

    static void Fail(string what)
    {
        int errno = Libc.Errno;
        throw new Json2dirException($"{what}: {System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(errno)}");
    }
}
