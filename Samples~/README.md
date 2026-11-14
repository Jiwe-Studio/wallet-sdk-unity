# Starter samples

Seven small, working starting points, each taken from a game we shipped. Every Unity sample is **one
component** that draws its own on-screen UI (IMGUI), so there's no scene, prefab or canvas to build — add
it to a GameObject, press Play. Each one exposes its state publicly (`Status`, `Board`, …), so you can read
it from your own UI later and delete the `OnGUI`.

| # | Sample | Taken from | Shows | Needs |
|---|---|---|---|---|
| 0 | [`JiweBootstrap`](Unity/JiweBootstrap.cs) | JiweChess | Guest-first setup: credentials from a gitignored asset, sign-in only when the player asks, a Cancel that stays live, XP that no-ops for guests | — |
| 1 | [`XpAndLeaderboardSample`](Unity/XpAndLeaderboardSample.cs) | KentePunk | XP per run, a leaderboard that's sorted and reads the right score field | — |
| 2 | [`UpdateGateSample`](Unity/UpdateGateSample.cs) | KentePunk | Rotated-key and minimum-version "please update", plus announcements. Every check fails open | Game Data you publish |
| 3 | [`CosmeticShopSample`](Unity/CosmeticShopSample.cs) | KentePunk | Buying with cowrie with a correctly-kept idempotency key | A funded player |
| 4 | [`CloudSaveSample`](Unity/CloudSaveSample.cs) | KentePunk | Cloud saves with User Data, local-first | — |
| 5 | [`AirtimeClaimSample`](Unity/AirtimeClaimSample.cs) | KentePunk | An airtime milestone: pool pre-check, double-tap guard, "accepted ≠ paid" | A funded app wallet |
| 6 | [`ServerRewardSample`](Unity/ServerRewardSample.cs) + [`server-node/`](server-node) | Sheng Word League | A server between the game and Jiwe: caps enforced, reserve-before-pay, release on failure | Node 18+ |

Pick by what a **forged call would cost you**: client-only (1–5) is fastest to ship; use 6 when airtime, cash
or a daily pool is on the line. The reasoning is in the main README, §9. The longer write-ups, with the
mistakes each game made, are in [docs/use-cases.md](../docs/use-cases.md).

## Using one

1. **Copy** the SDK's `Jiwe/` folder (including `Plugins/`) and `Samples/Unity/` into your project's `Assets/`.
2. **Register your app** with Jiwe *before* you create it — the redirect URI and scopes matter (main README §3).
   Standalone/Editor: set `JiweAuth.loopbackPort` to a fixed port and register `http://127.0.0.1:<port>/`.
3. **Credentials:** Assets ▸ Create ▸ Jiwe ▸ Credentials Config, save it as
   `Assets/Resources/JiweCredentials.asset`, fill it in, and **gitignore that path**. (Or type them straight
   into `JiweAuth`/`JiweWallet` in the Inspector while you experiment — just don't commit them.)
4. **Add a sample** to an empty GameObject. `[RequireComponent]` adds `JiweAuth` and `JiweWallet` for you.
   For guest-first, add `JiweBootstrap` to the same object (it turns off auto-login).
5. **Press Play.** Use the on-screen *Log in with Jiwe* button; the sample's buttons do the rest.

You can put several samples on one GameObject — they share the same `JiweAuth`/`JiweWallet`.
If login fails at the token step, see "How the client secret is presented" in the main README (§7).

## The server sample

```bash
cd Samples/server-node
JIWE_API_USERNAME=... JIWE_API_KEY=... node server.mjs
```

Set `ServerRewardSample.serverUrl` to `http://localhost:8766` (or wherever you host it). No dependencies, and the
file is short enough to read in one sitting — the comments explain each rule. Replace the in-memory cap tally with
a database before you ship, and verify the ID token's signature if you use it for anything beyond keying the cap.

## Are they tested?

Yes — every sample is exercised end to end by `Tests~/run-tests.sh` against a mock Jiwe server (the server sample
against a real Node process), and `XpAndLeaderboardSample` also runs inside a real WebGL player in
`Tests~/webgl`. That proves they behave as written against our model of Jiwe; it doesn't replace trying your own
credentials. See [Tests~/README.md](../Tests~/README.md).
