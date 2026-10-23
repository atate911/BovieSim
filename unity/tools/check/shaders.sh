#!/bin/sh
# Compiles every compute kernel and the particle shader with glslangValidator (HLSL front end).
# Usage: unity/tools/check/shaders.sh   (needs glslang-tools)
HERE=$(cd "$(dirname "$0")" && pwd); S="$HERE/../../BovieSim/Assets/BovieSim/Resources/BovieSim/Shaders"
OUT=${TMPDIR:-/tmp}/bovie-shaders; mkdir -p "$OUT"; fail=0; n=0
for f in Sim Surface; do
  for k in $(grep -oE '^#pragma kernel [A-Za-z0-9_]+' "$S/$f.compute" | awk '{print $3}'); do
    n=$((n + 1))
    if ! glslangValidator -D -S comp -e "$k" --target-env vulkan1.1 -o "$OUT/$k.spv" "$S/$f.compute" > "$OUT/$k.log" 2>&1; then
      echo "FAIL $f.compute:$k"; cat "$OUT/$k.log"; fail=1
    fi
  done
done
# .shader files: the HLSL block with HDRP includes swapped for the stub header
cp "$HERE/particles_stub.hlsl" "$OUT/"
for sh in "$S"/*.shader; do
  base=$(basename "$sh" .shader)
  { echo '#include "particles_stub.hlsl"'; sed -n '/HLSLPROGRAM/,/ENDHLSL/p' "$sh" | sed '1d;$d' | grep -vE '^\s*#(pragma|include)'; } > "$OUT/$base.hlsl"
  for st in vert:Vert frag:Frag; do
    n=$((n + 1))
    if ! glslangValidator -D -S "${st%%:*}" -e "${st##*:}" --target-env vulkan1.1 -o "$OUT/p.spv" "$OUT/$base.hlsl" > "$OUT/p.log" 2>&1; then
      echo "FAIL $base.shader:${st##*:}"; cat "$OUT/p.log"; fail=1
    fi
  done
done
[ $fail -eq 0 ] && echo "$n shader entry points compiled"
exit $fail
