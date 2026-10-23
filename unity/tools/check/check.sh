#!/bin/sh
# Compile-checks and runs the Unity client's C# without a Unity editor (used while developing blind).
#   UNITY_REFS = folder with UnityEngine.*Module.dll (e.g. NuGet Unity3D 2021.3 net45 refs)
#   UNITY_SDK  = folder with monolithic UnityEngine.dll + UnityEditor.dll (NuGet Unity3D.SDK)
# Requires mono-mcs. Unity 6 / HDRP APIs absent from those refs come from the stub files here;
# the few remaining false positives (Light.lightUnit, `in RenderParams`, Client.AddAndRemove) are filtered.
set -e
HERE=$(cd "$(dirname "$0")" && pwd); B="$HERE/../../BovieSim/Assets/BovieSim"; OUT=${TMPDIR:-/tmp}/bovie-check; mkdir -p "$OUT"
FILTER='^Compilation|Location of the symbol|lightUnit|RenderPrimitives\(ref|CS1620: Argument .#1. is missing .ref.|AddAndRemoveRequest'
echo "== runtime (module refs)"
R="$UNITY_REFS"; REFS=""; for m in CoreModule IMGUIModule InputLegacyModule AudioModule JSONSerializeModule TextRenderingModule; do REFS="$REFS -r:$R/UnityEngine.$m.dll"; done
mcs -target:library -out:"$OUT/rt.dll" $REFS "$HERE/stubs_unity6_hdrp.cs" $(find "$B/Core" "$B/Runtime" -name "*.cs") 2>&1 | grep -Ev "$FILTER" || true
echo "== everything incl. editor (SDK refs)"
mcs -target:library -out:"$OUT/ed.dll" -r:"$UNITY_SDK/UnityEngine.dll" -r:"$UNITY_SDK/UnityEditor.dll" -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll \
  "$HERE/stubs_unity6_hdrp.cs" "$HERE/stubs_hdrp_editor.cs" $(find "$B" -name "*.cs" -not -path "*/Tests/*") 2>&1 | grep -Ev "$FILTER" || true
echo "== EditMode tests under mono (ScenarioTests need Unity's JsonUtility and are skipped)"
mcs -out:"$OUT/tests.exe" "$HERE/nunit_shim.cs" "$HERE/fake_unity.cs" "$B"/Core/*.cs "$B"/Tests/EditMode/*.cs
BOVIE_RESOURCES="$B/Resources" mono "$OUT/tests.exe" ScenarioTests | grep -v '^PASS'
