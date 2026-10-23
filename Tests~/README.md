# Tests

Everything here runs without Jiwe. A mock server stands in for it, so the SDK and the samples are exercised
against realistic successes **and** failures: wrong client-auth mechanism, dropped connections, 429/5xx, a replayed
idempotency key, a 422, an HTML error page.

> **What passing means.** The mock encodes what Jiwe's docs say and what our shipped games have seen live
> (`mock_jiwe_server.py` documents each rule). Green tests prove the SDK conforms to *that model*. They can't prove
> Jiwe still behaves that way — the opt-in [live smoke test](#live-smoke-test-opt-in) is for that.

| Command | What it runs | Needs |
|---|---|---|
| `Tests~/run-tests.sh` | The Unity PlayMode suite (SDK + all samples) against the mock, the Node sample server, and the WebGL plugin's Node test | Unity 6, python3 (node optional) |
| `node --test ./Tests~/jslib/jiwe_storage.test.mjs` | `JiweStorage.jslib` executed against a fake browser | Node 18+ |
| `Tests~/webgl/run-webgl-e2e.sh` | A real WebGL build driven in headless Chrome: same-tab login, PKCE across the redirect, CORS, a sample in a real player | Unity 6 + WebGL module, Chrome |

Useful switches: `TEST_FILTER=Jiwe.Tests.AuthTests`, `UNITY=/path/to/Unity`, `BUILD_TARGET=Android`
(compiles that platform's `#if` branches in the editor), `SDK_DIR=<folder>` (test a modified copy of `Jiwe/`).

## How it's wired

The SDK stays a loose folder in this repo. The runner copies it, the samples and the tests into a **throwaway Unity
project** and adds `.asmdef` files there only, so the test assembly can reference the SDK without changing how you
consume it. Tests point the SDK at the mock through the same overrides a developer would use for a non-production
environment (`walletBaseUrl`, `tokenEndpoint`, …), and capture the login URL through `JiweAuth.UrlOpener`.

## Are the tests any good?

The suite was **mutation-tested**: ten deliberate bugs were injected into the SDK, one at a time (a wrong purchase field
name, 429 no longer retryable, Basic auth never sent, the state check removed, the leaderboard ignoring uppercase `XP`, …)
and each one made at least one test fail. Run the same experiment on your changes: copy `Jiwe/`, break something, run
`SDK_DIR=<copy> Tests~/run-tests.sh`, and expect red.

## Live smoke test (opt-in)

Read-only checks against the real API — credentials valid, wallet balance, Game Data envelope, both leaderboard
shapes. Nothing is written. It only runs if you provide credentials in the environment:

```bash
JIWE_LIVE_API_USERNAME=... JIWE_LIVE_API_KEY=... TEST_FILTER=Jiwe.Tests.LiveSmokeTests Tests~/run-tests.sh
# non-production environment:  JIWE_LIVE_BASE_URL=https://<your-env>/rest
```

## What is *not* tested

- **Login against the real `id.jiwe.io`** — it needs a person in a browser. The mock's `/auth` page stands in.
- **Android/iOS deep-link login** — the code compiles for both, but only the Standalone/Editor loopback and the WebGL
  paths run end to end.
- **Anything that moves real value** (airtime delivery, a real wallet debit) — by design.
- **Unity versions other than the one you run it on.** It was developed on 6000.0.41f1.
