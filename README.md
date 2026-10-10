# json2dir-csharp-fast

[json2dir](https://github.com/alurm/json2dir) (RFC J2D-1) in C# on .NET 10, written for speed. It uses only the BCL: no NuGet packages and no System.Text.Json.

```sh
dotnet publish -c Release -o out && out/json2dir-fast < tree.json
```

## Techniques

- **Raw UTF-8 bytes, no strings.** stdin goes into one native buffer. A regular file is `mmap`ed (private, `MAP_POPULATE`). A pipe is read into an anonymous mapping that grows with `mremap`, after `F_SETPIPE_SZ` raises the pipe buffer to 1 MiB. The whole input is validated once with `Utf8.IsValid`, which is vectorised.
- **SIMD lexer.** String contents are skipped 64 bytes at a time with AVX2 intrinsics: `vpcmpeqb` finds `"` and `\`, `vpminub` finds control bytes, and `vpmovmskb`+`tzcnt` give the position. There is a `Vector128` fallback for other CPUs, e.g. ARM. 64 zero bytes of padding after the input mean the loop needs no bounds checks.
- **Zero-copy strings.** A string is a range in the input buffer. Escaped strings are decoded in place, since decoded text is never longer than its escaped form, and every string is NUL-terminated in place. Names and link targets therefore go to libc as they are, and a file's contents are written straight from the buffer. Lone surrogates, bad escapes and control characters are rejected while lexing.
- **Flat tree.** The parser is iterative with an explicit stack. Nodes are 20-byte structs in one array. When an object closes, its members are sorted by name bytes with a stable merge sort, skipped if they are already sorted. Duplicates are dropped, the last one winning, and names and values are validated. Everything is validated before anything is written.
- **`*at()` syscalls via thin P/Invoke to libc.** The writer uses `openat(O_CREAT|O_EXCL|O_NOFOLLOW)`, `mkdirat`, `symlinkat`, `unlinkat` and `fchmod` relative to open directory descriptors. Creation is optimistic: a file in an empty directory costs `openat`+`write`+`close`. Only on `EEXIST` is the old entry handled. `unlinkat` fails with `EISDIR` for a directory in the way. `openat(O_DIRECTORY|O_NOFOLLOW)` tells a real directory, which is reused, from a symlink, which is replaced. Existing symlinks are never followed.
- **Startup.** The app is self-contained ReadyToRun compiled for `x86-64-v3`, with a static PGO profile (`pgo/json2dir-fast.mibc`). Interop is blittable with `DisableRuntimeMarshalling`, and the code avoids CoreLib generics over its own structs. Only one method, inside the runtime, is still JIT-compiled per run. Other settings: invariant globalization, non-concurrent GC, no EventSource, and the LTTng provider dropped.

## Benchmarks

Median of 15 runs (after 3 warmups) and max RSS, with `bench/bench.py`, on WSL2 Ubuntu 22.04 with a Ryzen 9 7900X3D. All binaries were copied to `/tmp`. stdin is a pipe written by the parent process, as in the benchmark harness. "disk" is ext4; "tmpfs" is `/dev/shm`.

| storage | workload | R2R + PGO (default) | NativeAOT variant | json2dir-aot | Rust original | json2dir-c23 |
|---|---|---|---|---|---|---|
| disk | empty | 12.2 ms / 23 MiB | 2.1 ms / 3 MiB | 2.7 ms / 4 MiB | 1.3 ms / 2 MiB | 1.2 ms / 2 MiB |
| disk | files-1000 | 36.8 ms / 24 MiB | 25.0 ms / 3 MiB | 43.7 ms / 11 MiB | 26.0 ms / 2 MiB | 26.7 ms / 2 MiB |
| disk | payload-8MiB | 25.1 ms / 31 MiB | 13.4 ms / 11 MiB | 78.2 ms / 69 MiB | 16.2 ms / 18 MiB | 24.0 ms / 17 MiB |
| tmpfs | empty | 12.0 ms / 23 MiB | 2.1 ms / 3 MiB | 2.5 ms / 4 MiB | 1.3 ms / 2 MiB | 1.2 ms / 2 MiB |
| tmpfs | files-1000 | 20.8 ms / 24 MiB | 10.4 ms / 3 MiB | 26.8 ms / 11 MiB | 10.6 ms / 2 MiB | 10.5 ms / 2 MiB |
| tmpfs | payload-8MiB | 25.1 ms / 31 MiB | 14.9 ms / 11 MiB | 79.8 ms / 69 MiB | 18.1 ms / 18 MiB | 24.6 ms / 17 MiB |

With stdin redirected from a file (`--stdin file`), the mmap path applies. payload-8MiB then takes 17.8 ms with R2R, 7.0 ms with NativeAOT and 11.3 ms with Rust, all on disk.

### NativeAOT with and without the static PGO profile

ILCompiler gets the same `pgo/json2dir-fast.mibc` (through its `MibcFile` item, i.e. `--mibc`). Its JIT reports
"with Static PGO" for the hot SIMD scanner and seven other methods; the remaining hot methods get synthesized
profiles plus PGO data of their inlinees. Each configuration was run twice (`#1`, `#2`) to show the noise;
same conditions as above, stdin through a pipe:

| storage | workload | AOT #1 | AOT+PGO #1 | AOT #2 | AOT+PGO #2 | R2R+PGO | Rust |
|---|---|---|---|---|---|---|---|
| disk | empty | 2.3 ms | 2.2 ms | 2.1 ms | 2.1 ms | 13.3 ms | 1.6 ms |
| disk | files-1000 | 25.9 ms | 25.4 ms | 26.4 ms | 24.9 ms | 35.9 ms | 25.2 ms |
| disk | payload-8MiB | 13.8 ms | 12.8 ms | 13.5 ms | 12.9 ms | 25.4 ms | 17.6 ms |
| tmpfs | empty | 2.2 ms | 2.2 ms | 2.1 ms | 2.1 ms | 12.0 ms | 1.3 ms |
| tmpfs | files-1000 | 10.3 ms | 10.2 ms | 10.4 ms | 10.3 ms | 21.2 ms | 11.0 ms |
| tmpfs | payload-8MiB | 15.7 ms | 14.5 ms | 14.1 ms | 14.3 ms | 25.5 ms | 21.1 ms |

With `--stdin file`, payload-8MiB takes 7.3 / 7.1 / 7.2 / 6.7 ms on disk and 8.6 / 8.0 / 8.2 / 8.1 ms on tmpfs.
RSS is the same with and without the profile: 3 MiB for empty and files-1000, 11 MiB for payload.

Conclusion: the static profile makes no difference beyond noise for NativeAOT, as for R2R. The hot loops are
already straight-line SIMD code, and the time goes to syscalls and the kernel.

About 10 ms of every R2R run is CoreCLR itself: loading the runtime, CoreLib and type loads. PGO and other runtime knobs barely move it. The NativeAOT build of the same code has no such cost and matches or beats Rust and C.

## Building

The build needs the .NET 10 SDK. Restore downloads the linux-x64 runtime pack and crossgen2.

```sh
dotnet publish -c Release -o out                 # R2R + static PGO (the default)
dotnet publish -c Release -o out-aot -p:PublishAot=true -p:IlcInstructionSet=x86-64-v3 \
    -p:IntermediateOutputPath=obj/aot/           # NativeAOT + static PGO (needs clang)
```

Add `-p:StaticPgo=false` to either command to build without the profile.

Regenerating the PGO profile:

```sh
dotnet tool install dotnet-pgo --version 10.0.0-rtm.25531.102 --tool-path ../../runtimes/dotnet-tools \
    --add-source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10-transport/nuget/v3/index.json
dotnet publish -c Release -o out-nopgo -p:StaticPgo=false   # a build without the old profile
sh pgo/collect.sh out-nopgo                      # instrumented runs (file and pipe stdin) -> EventPipe traces -> .mibc
dotnet publish -c Release -o out                 # crossgen2 / ILCompiler now use pgo/json2dir-fast.mibc
```

Running the benchmarks:

```sh
python3 bench/bench.py [--stdin pipe|file] [--storage disk|tmpfs|both] NAME=COMMAND ...
```
