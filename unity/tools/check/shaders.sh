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
# particle shader: the HLSL block with HDRP includes swapped for the stub header
{ echo '#include "particles_stub.hlsl"'; sed -n '/HLSLPROGRAM/,/ENDHLSL/p' "$S/Particles.shader" | sed '1d;$d' | grep -vE '^\s*#(pragma|include)'; } > "$OUT/particles.hlsl"
cp "$HERE/particles_stub.hlsl" "$OUT/"
for st in vert:Vert frag:Frag; do
  n=$((n + 1))
  if ! glslangValidator -D -S "${st%%:*}" -e "${st##*:}" --target-env vulkan1.1 -o "$OUT/p.spv" "$OUT/particles.hlsl" > "$OUT/p.log" 2>&1; then
    echo "FAIL Particles.shader:${st##*:}"; cat "$OUT/p.log"; fail=1
  fi
done
[ $fail -eq 0 ] && echo "$n shader entry points compiled"
exit $fail
