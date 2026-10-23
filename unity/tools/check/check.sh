#!/bin/sh
# Compile-checks and runs the Unity client's C# without a Unity editor (used while developing blind, and in CI).
#   UNITY_REFS = folder with UnityEngine.*Module.dll (NuGet UnityEngine.Modules 2021.3.33, lib/net45)
#   UNITY_SDK  = folder with monolithic UnityEngine.dll + UnityEditor.dll (NuGet Unity3D.SDK 2021.1.14.1, lib)
# Requires mono-mcs. Unity 6 / HDRP APIs absent from those refs come from the stub files here; the few
# remaining false positives (Light.lightUnit, `in RenderParams`, Client.AddAndRemove, FindAnyObjectByType)
# are filtered. Exits non-zero on any compile error or failing test.
HERE=$(cd "$(dirname "$0")" && pwd); B="$HERE/../../BovieSim/Assets/BovieSim"; OUT=${TMPDIR:-/tmp}/bovie-check; mkdir -p "$OUT"
FILTER='^Compilation|Location of the symbol|lightUnit|RenderPrimitives\(ref|CS1620: Argument .#1. is missing .ref.|AddAndRemoveRequest|FindAnyObjectByType'
status=0
[ -n "$CI" ] && echo "::notice title=toolchain::$(mcs --version 2>&1 | head -1); $(mono --version 2>&1 | head -1)"
report() { # $1 label, $2 log
  errs=$(grep -Ev "$FILTER" "$2" | grep -E "error|warning CS0618" || true)
  if [ -n "$errs" ]; then echo "$errs"; echo "== $1: FAILED"; status=1; else echo "== $1: ok"; fi
  if [ -n "$CI" ] && [ -n "$errs" ]; then
    echo "---- full log $2"; cat "$2"
    # GitHub annotations are readable without signing in, unlike job logs
    echo "$errs" | head -20 | while IFS= read -r line; do echo "::error title=$1::$line"; done
  fi
}
R="$UNITY_REFS"; REFS=""; for m in CoreModule IMGUIModule InputLegacyModule AudioModule JSONSerializeModule TextRenderingModule UIElementsModule ScreenCaptureModule; do REFS="$REFS -r:$R/UnityEngine.$m.dll"; done
mcs -target:library -out:"$OUT/rt.dll" -define:BOVIE_INPUTSYSTEM $REFS "$HERE/stubs_inputsystem.cs" "$HERE/stubs_unity6_hdrp.cs" \
  $(find "$B/Core" "$B/Runtime" -name "*.cs") > "$OUT/rt.log" 2>&1
report "runtime (module refs)" "$OUT/rt.log"
mcs -target:library -out:"$OUT/ed.dll" -r:"$UNITY_SDK/UnityEngine.dll" -r:"$UNITY_SDK/UnityEditor.dll" -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll \
  "$HERE/stubs_unity6_hdrp.cs" "$HERE/stubs_hdrp_editor.cs" $(find "$B" -name "*.cs" -not -path "*/Tests/*") > "$OUT/ed.log" 2>&1
report "everything incl. editor (SDK refs)" "$OUT/ed.log"
echo "== EditMode tests under mono (ScenarioTests need Unity's JsonUtility and are skipped)"
if mcs -out:"$OUT/tests.exe" "$HERE/nunit_shim.cs" "$HERE/fake_unity.cs" "$B"/Core/*.cs "$B"/Tests/EditMode/*.cs > "$OUT/t.log" 2>&1; then
  BOVIE_RESOURCES="$B/Resources" mono "$OUT/tests.exe" ScenarioTests > "$OUT/tests.out" 2>&1 || status=1
  if [ -n "$CI" ]; then cat "$OUT/tests.out"; grep -v '^PASS' "$OUT/tests.out" | head -20 | while IFS= read -r line; do echo "::error title=tests::$line"; done; else grep -v '^PASS' "$OUT/tests.out"; fi
else cat "$OUT/t.log"; echo "== tests: did not compile"; [ -n "$CI" ] && head -20 "$OUT/t.log" | while IFS= read -r line; do echo "::error title=tests compile::$line"; done; status=1; fi
exit $status
