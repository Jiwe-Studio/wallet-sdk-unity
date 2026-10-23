#!/usr/bin/env bash
# Real-browser end-to-end for the WebGL login: builds a tiny WebGL player containing the SDK, serves it, and
# drives it in headless Chrome against the mock Jiwe server. Proves the things Unity's editor can't:
#   * login navigates the SAME tab (Application.OpenURL would open a second one),
#   * the PKCE verifier survives the full-page redirect (localStorage via JiweStorage.jslib),
#   * the token exchange and every wallet call work cross-origin (CORS + preflight),
#   * a starter sample runs in a real player.
#
#   Tests~/webgl/run-webgl-e2e.sh            # build (first time is slow), serve, drive Chrome, report
#   SKIP_BUILD=1 Tests~/webgl/run-webgl-e2e.sh
#   SDK_DIR=<copy of Jiwe/> ...              # test a modified SDK (used to prove these checks catch the original bugs)
#   KEEP_RUNNING=1 ...                       # leave the servers up so you can open http://127.0.0.1:8000/ yourself
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"; TESTS="$(dirname "$HERE")"; REPO="$(dirname "$TESTS")"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.0.41f1/Unity.app/Contents/MacOS/Unity}"
CHROME="${CHROME:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"
PROJECT="${PROJECT:-${TMPDIR:-/tmp}/jiwe-sdk-webgl}"
MOCK_PORT=8765; WEB_PORT="${WEB_PORT:-8000}"
[ -x "$UNITY" ] || { echo "Unity not found (set UNITY=...)"; exit 2; }
[ -x "$CHROME" ] || { echo "Chrome not found (set CHROME=...)"; exit 2; }

if [ -z "${SKIP_BUILD:-}" ]; then
  [ -d "$PROJECT/ProjectSettings" ] || { echo "== creating project"; "$UNITY" -batchmode -nographics -quit -createProject "$PROJECT" -logFile "$PROJECT.create.log" || exit 2; }
  A="$PROJECT/Assets"
  rm -rf "$A/Jiwe" "$A/JiweSamples" "$A/E2E" "$A/Editor/WebGlE2EBuild.cs"
  mkdir -p "$A/E2E" "$A/Editor"
  cp -R "${SDK_DIR:-$REPO/Jiwe}" "$A/Jiwe"; cp -R "$REPO/Samples/Unity" "$A/JiweSamples"
  cp "$HERE/WebGlE2E.cs" "$A/E2E/"; cp "$HERE/WebGlE2EBuild.cs" "$A/Editor/"
  cat > "$PROJECT/Packages/manifest.json" <<'JSON'
{ "dependencies": { "com.unity.modules.imgui": "1.0.0", "com.unity.modules.jsonserialize": "1.0.0", "com.unity.modules.unitywebrequest": "1.0.0" } }
JSON
  rm -f "$PROJECT/Packages/packages-lock.json"
  echo "== building the WebGL player (log: $PROJECT/build.log)"
  JIWE_E2E_OUT="$PROJECT/Build/WebGL" "$UNITY" -batchmode -nographics -quit -projectPath "$PROJECT" -buildTarget WebGL -executeMethod WebGlE2EBuild.Build -logFile "$PROJECT/build.log" \
    || { echo "!! WebGL build failed"; grep -E "error CS|Build failed|Exception" "$PROJECT/build.log" | head -20; exit 2; }
fi
[ -f "$PROJECT/Build/WebGL/index.html" ] || { echo "no WebGL build at $PROJECT/Build/WebGL"; exit 2; }

echo "== starting mock Jiwe :$MOCK_PORT and the game server :$WEB_PORT"
python3 "$TESTS/mock_jiwe_server.py" --port "$MOCK_PORT" > "$PROJECT.mock.log" 2>&1 & MOCK=$!
python3 -m http.server "$WEB_PORT" --bind 127.0.0.1 --directory "$PROJECT/Build/WebGL" > "$PROJECT.web.log" 2>&1 & WEB=$!
CH=""; trap 'kill $MOCK $WEB $CH 2>/dev/null || true' EXIT
for i in $(seq 1 30); do curl -sf "http://127.0.0.1:$MOCK_PORT/__health" >/dev/null && curl -sf "http://127.0.0.1:$WEB_PORT/" >/dev/null && break; sleep 0.2; done

if [ -n "${KEEP_RUNNING:-}" ]; then echo "== open http://127.0.0.1:$WEB_PORT/  (results: curl http://127.0.0.1:$MOCK_PORT/__reports) — Ctrl-C to stop"; wait; fi

echo "== driving headless Chrome"
rm -rf "$PROJECT/chrome-profile"
"$CHROME" --headless=new --no-first-run --no-default-browser-check --user-data-dir="$PROJECT/chrome-profile" \
  --use-angle=swiftshader --enable-unsafe-swiftshader --ignore-gpu-blocklist --window-size=1000,700 \
  --remote-debugging-port=9222 --enable-logging=stderr --v=0 "http://127.0.0.1:$WEB_PORT/" > "$PROJECT.chrome.log" 2>&1 & CH=$!

python3 - "$MOCK_PORT" "$PROJECT.chrome.log" <<'PY'
import json, sys, time, urllib.request, re
port, chromelog = sys.argv[1], sys.argv[2]
get = lambda p: json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}{p}"))
deadline = time.time() + 180
reports = []
while time.time() < deadline:
    reports = get("/__reports")["items"]
    steps = [r["step"] for r in reports]
    if "done" in steps or any(r["step"] == "login" and not r["ok"] for r in reports): break
    time.sleep(1)
time.sleep(1); reports = get("/__reports")["items"]

ok_all = True
def check(name, cond, detail=""):
    global ok_all; ok_all &= bool(cond)
    print(f"  {'PASS' if cond else 'FAIL'}  {name}" + (f"   [{detail}]" if detail and not cond else ""))

print("\n== browser results")
boots = [r for r in reports if r["step"] == "boot"]
check("a page load came back from the login with ?code= (the redirect returned to the game)", any("code=" in r["detail"] for r in boots), str([r["detail"] for r in boots]))
# Ask Chrome itself what it ended up with — direct evidence, not something the game reports about itself.
try:
    targets = [t for t in json.load(urllib.request.urlopen("http://127.0.0.1:9222/json/list")) if t.get("type") == "page"]
except Exception as e:
    targets = None; print("  (could not reach Chrome's debugging port:", e, ")")
check("exactly ONE browser tab exists (Application.OpenURL would have opened a second)", targets is not None and len(targets) == 1, f"{None if targets is None else len(targets)} tabs: {[t['url'] for t in (targets or [])]}")
if targets:
    check("the single-use ?code= was removed from the address bar after it was read", "code=" not in targets[0]["url"], targets[0]["url"])
state = get("/__state")
check("exactly ONE login page visit (a second tab would make two)", state["auth_seq"] == 1, f"auth_seq={state['auth_seq']}")
for r in reports:
    if r["step"] != "boot": check(f"step '{r['step']}'", r["ok"], r["detail"])
steps = {r["step"] for r in reports if r["ok"]}
for needed in ["login", "xp", "leaderboard", "purchase", "earned", "bearer", "userdata", "gamedata", "sample", "done"]:
    check(f"'{needed}' completed", needed in steps)

log = get("/__log")["items"]
tok = [i for i in log if i["path"] == "/token" and i["method"] == "POST"]
check("token exchange sent exactly once, with Basic auth, from the browser", len(tok) == 1 and "authorization: Basic" in tok[0]["headers"], f"{len(tok)} token requests")
pre = open(chromelog, errors="replace").read()
errs = [l for l in pre.splitlines() if re.search(r"CONSOLE.*(Uncaught|Exception|NullReference|error CS|Failed to load)", l)]
check("no uncaught errors in the browser console", not errs, (errs[0][:200] if errs else ""))
sys.exit(0 if ok_all else 1)
PY
