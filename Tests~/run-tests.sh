#!/usr/bin/env bash
# Runs the SDK's PlayMode tests against the mock Jiwe server, in a throwaway Unity project.
#
#   Tests~/run-tests.sh                  # whole suite
#   TEST_FILTER=AuthTests Tests~/run-tests.sh
#   UNITY=/path/to/Unity BUILD_TARGET=Android Tests~/run-tests.sh
#
# The SDK stays a loose folder in the repo; an asmdef is added only to the throwaway copy so the
# test assembly can reference it. Needs: Unity 6, python3. No network access to Jiwe.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(dirname "$HERE")"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.0.41f1/Unity.app/Contents/MacOS/Unity}"
PROJECT="${PROJECT:-${TMPDIR:-/tmp}/jiwe-sdk-tests}"
export JIWE_MOCK_PORT="${JIWE_MOCK_PORT:-8765}"
BUILD_TARGET="${BUILD_TARGET:-}"        # e.g. Android: compiles that platform's #if branches in the editor
FILTER="${TEST_FILTER:-}"

[ -x "$UNITY" ] || { echo "Unity not found at $UNITY (set UNITY=...)"; exit 2; }

if [ ! -d "$PROJECT/ProjectSettings" ]; then
  echo "== creating test project at $PROJECT"
  "$UNITY" -batchmode -nographics -quit -createProject "$PROJECT" -logFile "$PROJECT.create.log" || { tail -20 "$PROJECT.create.log"; exit 2; }
fi

echo "== syncing SDK, samples and tests into $PROJECT"
A="$PROJECT/Assets"
rm -rf "$A/Jiwe" "$A/JiweSamples" "$A/JiweTests" "$A/Editor/JiweTestProjectSetup.cs"
mkdir -p "$A/Editor"
cp -R "${SDK_DIR:-$REPO/Jiwe}" "$A/Jiwe"
echo '{ "name": "Jiwe", "rootNamespace": "Jiwe" }' > "$A/Jiwe/Jiwe.asmdef"
SAMPLES_ROOT="${SAMPLES_ROOT:-$REPO/Samples}"
if [ -d "$SAMPLES_ROOT/Unity" ]; then
  cp -R "$SAMPLES_ROOT/Unity" "$A/JiweSamples"
  echo '{ "name": "JiweSamples", "references": ["Jiwe"] }' > "$A/JiweSamples/JiweSamples.asmdef"
else
  mkdir -p "$A/JiweSamples"; echo '{ "name": "JiweSamples", "references": ["Jiwe"] }' > "$A/JiweSamples/JiweSamples.asmdef"
fi
mkdir -p "$A/JiweTests"
cp "$HERE"/UnityTests/*.cs "$A/JiweTests/"
cat > "$A/JiweTests/JiweSdkTests.asmdef" <<'JSON'
{
  "name": "JiweSdkTests",
  "references": ["Jiwe", "JiweSamples", "UnityEngine.TestRunner", "UnityEditor.TestRunner"],
  "includePlatforms": [], "excludePlatforms": [],
  "overrideReferences": true, "precompiledReferences": ["nunit.framework.dll"],
  "autoReferenced": false, "defineConstraints": ["UNITY_INCLUDE_TESTS"]
}
JSON
cat > "$A/Editor/JiweTestProjectSetup.cs" <<'CS'
using UnityEditor;
[InitializeOnLoad]
static class JiweTestProjectSetup
{
    static JiweTestProjectSetup()
    {
        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed; // the mock server is plain http on localhost
        PlayerSettings.runInBackground = true;
    }
}
CS
cat > "$PROJECT/Packages/manifest.json" <<'JSON'
{ "dependencies": {
  "com.unity.test-framework": "1.4.5",
  "com.unity.modules.imgui": "1.0.0",
  "com.unity.modules.jsonserialize": "1.0.0",
  "com.unity.modules.unitywebrequest": "1.0.0"
} }
JSON
rm -f "$PROJECT/Packages/packages-lock.json"

echo "== starting mock Jiwe on :$JIWE_MOCK_PORT"
python3 "$HERE/mock_jiwe_server.py" --port "$JIWE_MOCK_PORT" > "$PROJECT.mock.log" 2>&1 &
MOCK=$!
trap 'kill $MOCK ${NODE_PID:-} 2>/dev/null || true' EXIT
for i in $(seq 1 30); do curl -sf "http://127.0.0.1:$JIWE_MOCK_PORT/__health" >/dev/null && break; sleep 0.2; done

# The server-in-front-of-Jiwe sample (Samples/server-node), pointed at the mock, so the Unity tests can exercise it for real.
if command -v node >/dev/null && [ -f "$SAMPLES_ROOT/server-node/server.mjs" ]; then
  export JIWE_NODE_PORT="${JIWE_NODE_PORT:-8766}"
  echo "== starting the Node sample server on :$JIWE_NODE_PORT"
  PORT="$JIWE_NODE_PORT" JIWE_API_BASE="http://127.0.0.1:$JIWE_MOCK_PORT/rest/api/v1" JIWE_API_USERNAME=test-user JIWE_API_KEY=test-key \
    DAILY_CAP_XP=300 MAX_XP_PER_RUN=200 ALLOW_TEST_RESET=1 node "$SAMPLES_ROOT/server-node/server.mjs" > "$PROJECT.node.log" 2>&1 &
  NODE_PID=$!
  for i in $(seq 1 30); do curl -sf "http://127.0.0.1:$JIWE_NODE_PORT/api/health" >/dev/null && break; sleep 0.2; done
else
  unset JIWE_NODE_PORT; echo "== node not found: the server-sample tests will be skipped"
fi

RESULTS="$PROJECT/results.xml"; rm -f "$RESULTS"
ARGS=(-batchmode -nographics -projectPath "$PROJECT" -runTests -testPlatform PlayMode -testResults "$RESULTS" -logFile "$PROJECT/unity.log")
[ -n "$BUILD_TARGET" ] && ARGS+=(-buildTarget "$BUILD_TARGET")
[ -n "$FILTER" ] && ARGS+=(-testFilter "$FILTER")
echo "== running the WebGL plugin test (node)"
if command -v node >/dev/null; then node --test "$HERE/jslib/jiwe_storage.test.mjs" 2>&1 | grep -E "^ℹ (tests|pass|fail)" | tr '\n' ' '; echo; fi
echo "== running Unity tests (log: $PROJECT/unity.log)"
set +e; "$UNITY" "${ARGS[@]}"; CODE=$?; set -e

python3 - "$RESULTS" "$PROJECT/unity.log" <<'PY'
import sys, re, xml.etree.ElementTree as ET
res, log = sys.argv[1], sys.argv[2]
try:
    root = ET.parse(res).getroot()
except Exception:
    print("!! no results file — Unity failed before running tests. Last log lines:")
    print("".join(open(log, errors="replace").readlines()[-40:])); sys.exit(2)
print(f"\n== {root.get('passed')} passed, {root.get('failed')} failed, {root.get('skipped')} skipped, of {root.get('total')}")
bad = [c for c in root.iter("test-case") if c.get("result") != "Passed"]
for c in bad:
    msg = c.find("failure/message")
    print(f"  FAIL {c.get('fullname')}\n       {(msg.text or '').strip()[:400] if msg is not None else c.get('result')}")
sys.exit(1 if bad else 0)
PY
