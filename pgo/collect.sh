#!/bin/sh
# Collects the static PGO profile pgo/json2dir-fast.mibc.
#
#   pgo/collect.sh [PUBLISHED_DIR]      (default: out, as built by `dotnet publish -o out`)
#
# Runs the published binary with ReadyToRun code disabled and tiered PGO instrumentation forced on
# representative documents, records EventPipe traces through the runtime's own environment switches
# (no dotnet-trace needed), turns each trace into a .mibc with dotnet-pgo and merges them.
# dotnet-pgo is a local tool:
#   dotnet tool install dotnet-pgo --version 10.0.0-rtm.25531.102 --tool-path <runtimes>/dotnet-tools \
#     --add-source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10-transport/nuget/v3/index.json
# Afterwards `dotnet publish` again: the csproj passes the .mibc to crossgen2 (ReadyToRun) and to
# ILCompiler (NativeAOT). Collect from a build without a profile: dotnet publish -p:StaticPgo=false.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
app=${1:-$here/../out}/json2dir-fast
pgo=${DOTNET_PGO:-$here/../../../runtimes/dotnet-tools/dotnet-pgo}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

python3 - "$work" <<'EOF'
import json, random, sys
d = sys.argv[1]
rnd = random.Random(7)
text = lambda n: "".join(chr(rnd.randrange(97, 123)) for _ in range(n))
docs = {
    "files": {f"f{i:05d}": text(64) for i in range(5000)},
    "payload": {"payload": "x" * (16 << 20)},
    "mixed": {f"d{i}": {**{f"f{j}": text(40) for j in range(50)},
                        **{f"l{j}": ["link", f"../t{j}"] for j in range(5)},
                        **{f"s{j}": ["script", "#!/bin/sh\necho hi\n"] for j in range(5)}} for i in range(20)},
    "unsorted": {f"k{rnd.randrange(10**6)}": text(10) for i in range(3000)},
}
for name, doc in docs.items():
    open(f"{d}/{name}.json", "w", encoding="utf-8").write(json.dumps(doc, ensure_ascii=False))
# Escaped strings, written by hand: \u escapes (with a surrogate pair), short escapes and raw UTF-8.
open(f"{d}/escapes.json", "w", encoding="utf-8").write(
    '{"e":"' + r'café 😀 \n\t\"' * 50000 + '","u":"' + "é€😀" * 100000 + '"}')
EOF

# One instrumented run of the app in an empty directory, stdin as given to this function.
profile_run() {
    mkdir "$work/t$n"
    (cd "$work/t$n" &&
        DOTNET_ReadyToRun=0 DOTNET_TieredPGO=1 DOTNET_TieredPGO_InstrumentOnlyHotCode=0 \
        DOTNET_TC_CallCountThreshold=1 DOTNET_TC_CallCountingDelayMs=0 \
        DOTNET_EnableEventPipe=1 DOTNET_EventPipeOutputPath="$work/trace$n.nettrace" \
        DOTNET_EventPipeConfig='Microsoft-Windows-DotNETRuntime:0x1F000080018:5' \
        "$app")
    rm -rf "$work/t$n"
    "$pgo" create-mibc --trace "$work/trace$n.nettrace" --output "$work/p$n.mibc" >/dev/null
}

n=0
for doc in "$work"/*.json; do
    # Twice with the file redirected (stdin gets mmapped), once through a pipe (as the benchmark harness does).
    n=$((n + 1)); profile_run < "$doc"
    n=$((n + 1)); profile_run < "$doc"
    n=$((n + 1)); cat "$doc" | profile_run
done
"$pgo" merge $(for i in $(seq 1 $n); do printf -- '--input %s ' "$work/p$i.mibc"; done) --output "$here/json2dir-fast.mibc" >/dev/null
echo "wrote $here/json2dir-fast.mibc from $n runs"
