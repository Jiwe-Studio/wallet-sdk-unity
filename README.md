# Jiwe Wallet SDK for Unity

Log players into Jiwe with OAuth2 + PKCE, then credit them XP/points/airtime and read
leaderboard/wallet data — against Jiwe's real, live API (`id.jiwe.io` for auth,
`abacus.jiwe.io` for wallet calls).

Two scripts, no dependencies: `JiweAuth.cs` handles login, `JiweWallet.cs` handles
everything else. Copy `Jiwe/` into `Assets/` and you're done importing.

> **This doc is Unity-specific.** The concepts below — two credential pairs, the
> reward/leaderboard API shape, the security rules — are the same on any engine.
> If you're building the Unreal or Godot SDK next, sections 2–4 are the ones to
> port; only §5 (platform redirect handling) is Unity/C#-specific.

---

## 1. Architecture at a glance

```mermaid
flowchart LR
    subgraph Game["Your Unity Game"]
        A["JiweAuth\n(login)"]
        W["JiweWallet\n(rewards, leaderboard,\nbalance)"]
        A -- "IdToken" --> W
    end

    A -- "OAuth2 + PKCE" --> ID["id.jiwe.io\n(login server)"]
    W -- "X-API-TOKEN (player)\nX-API-USERNAME/KEY (app)" --> AB["abacus.jiwe.io\n(wallet API)"]
```

`JiweAuth` logs a *player* in and produces an `IdToken`. `JiweWallet` uses that token
for player-scoped calls, plus a separate static credential pair for app-scoped calls.
They're independent components wired together by one Inspector reference
(`JiweWallet.auth`).

---

## 2. Two credential pairs — don't confuse them

This is the single most common integration mistake: there are **two unrelated
credential pairs**, not one.

| Pair | Lives on | Identifies | Required for |
|---|---|---|---|
| `clientId` + `apiSecret` | `JiweAuth` | your **app**, used to run OAuth login | Logging a player in at all |
| `apiUsername` + `apiKey` | `JiweWallet` | your **app**, sent as `X-API-USERNAME`/`X-API-KEY` | Every wallet API call, even before login |

Get all four by logging in at [www.jiwe.io](http://www.jiwe.io) → **Profile** →
**My Apps** → **Create an Application** — one application per game.

```mermaid
flowchart TB
    subgraph NoLoginNeeded["Work without a logged-in player"]
        direction LR
        L1["GetLeaderboard"]
        L2["GetWalletBalance"]
        L3["GetTransactionStatus"]
        L4["CheckCredentialsValid"]
    end
    subgraph LoginRequired["Require IdToken (player logged in)"]
        direction LR
        R1["GiveXpReward"]
        R2["GivePointsReward"]
        R3["GiveAirtimeReward"]
    end
    Static(["apiUsername / apiKey only"]) --> NoLoginNeeded
    Token(["apiUsername / apiKey\n+ player IdToken"]) --> LoginRequired
```

There is **no purchase (charge-the-player) endpoint** in the current API — an older
SDK's `Purchase()` call has no live equivalent and was removed rather than left
pointing at a dead host.

---

## 3. Quickstart

1. **Get credentials.** Log in at [www.jiwe.io](http://www.jiwe.io) → **Profile** →
   **My Apps** → **Create an Application** to generate `clientId` / `apiSecret` /
   `apiUsername` / `apiKey` for this game. The form also asks for **Redirect URIs**
   and scopes — see §5 for exactly what to put there, it isn't obvious from the
   form alone.
2. **Import.** Copy `Jiwe/` (`JiweAuth.cs` + `JiweWallet.cs`) into `Assets/`. No
   Newtonsoft dependency — it uses Unity's built-in `JsonUtility`.
3. **Wire it up.** Create an empty GameObject ("JiweSDK"), add both `JiweAuth` and
   `JiweWallet` to it, and drag the `JiweAuth` component into `JiweWallet.auth`.
4. **Fill in credentials** on the two components (see the table in §2). Do this via a
   gitignored config asset, not hardcoded — see §6.
5. **Run.** With `loginOnStart` checked (default), the login page opens automatically;
   `JiweAuth.OnLoginSuccess` fires once `IdToken` is populated.

```csharp
jiweAuth.OnLoginSuccess += () => {
    jiweWallet.GiveXpReward(20, "Reward for passing boss#1", result => {
        if (result.Success) Debug.Log("XP awarded!");
        else Debug.LogWarning($"XP reward failed: {result.Error}");
    });
};
```

---

## 4. API reference

All calls are async/callback-based; every result carries `Success` + `Error` (+
call-specific fields).

| Method | Needs login? | Notes |
|---|---|---|
| `GiveXpReward(xp, description, onComplete, transactionId?)` | Yes | XP has no monetary value |
| `GivePointsReward(points, description, onComplete, transactionId?)` | Yes | "Cowrie" — Jiwe's in-game currency |
| `GiveAirtimeReward(units, phoneNumber, description, onComplete, transactionId?)` | Yes | Min. 5 units; credits real airtime |
| `GetLeaderboard(rewardType, maxEntries, period, bestPointsRanking, onComplete)` | No | `rewardType`: `"xp"`\|`"cowrie"`; `period`: `"day"`\|`"week"`\|`"month"`\|`"year"`\|`null` |
| `GetWalletBalance(onComplete)` | No | Your app's own balance, not a player's |
| `GetTransactionStatus(transactionId, onComplete)` | No | Poll any `ledger_transaction_id` from a reward result |
| `CheckCredentialsValid(onResult)` | No | See §7, forced-update pattern |

`transactionId` is auto-generated if omitted. All three reward calls return the same
`JiweWalletResult { Success, Error, RawResponse }` shape.

```csharp
jiweWallet.GetLeaderboard("xp", maxEntries: 20, period: null, bestPointsRanking: "highest", result => {
    if (result.Success)
        foreach (var e in result.Entries) Debug.Log($"{e.rank}. {e.name} — {e.currentXP}");
});
```

---

## 5. Login flow, per platform

`JiweAuth` runs standard OAuth2 Authorization Code + PKCE, but how the browser hands
control back to your game is inherently different per platform — this is the one part
of the SDK that can't be unified.

```mermaid
sequenceDiagram
    participant Game
    participant Browser as System Browser
    participant Jiwe as id.jiwe.io

    Game->>Browser: Open login URL (code_challenge)
    Browser->>Jiwe: Player logs in
    Jiwe-->>Browser: Redirect with ?code=...&state=...

    alt Standalone / Editor
        Browser->>Game: GET http://127.0.0.1:{port}/  (loopback listener)
    else Android / iOS
        Browser->>Game: Custom URI scheme reopens app (deep link)
    else WebGL
        Browser->>Game: Full-page redirect back to same hosted URL
    end

    Game->>Jiwe: POST /token (code, code_verifier, client_secret)
    Jiwe-->>Game: id_token, access_token
    Game->>Game: IdToken set, OnLoginSuccess fires
```

| Platform | Mechanism | Extra setup |
|---|---|---|
| Standalone / Editor | Local loopback HTTP listener catches the redirect | None |
| Android / iOS | System browser → custom URI scheme (`mobileRedirectScheme`) reopens the app | Register that redirect URI with Jiwe |
| WebGL | Jiwe redirects back to your **same hosted page** with `?code=...`; page reloads and resumes | Jiwe must allow CORS from your hosted domain |

### Redirect URIs — what to put in the "Redirect URIs" field

When you create your application (§9), the form has a **Redirect URIs** field that's
left blank by default — there's no platform default shown in the UI, and it's easy to
not know what belongs there. This is exactly what each platform's branch above sends
as `redirect_uri` in the login request, so it has to match what you register:

| Platform | `redirect_uri` the SDK actually sends | Register this |
|---|---|---|
| Standalone / Editor | `http://127.0.0.1:<random port>/` — a **new free port every login** | `http://127.0.0.1/` — see caveat below |
| Android / iOS | `<mobileRedirectScheme>://oauth-callback` — default scheme is `jiwewallet` unless you changed the `mobileRedirectScheme` field on `JiweAuth` | `jiwewallet://oauth-callback` (or your own scheme, kept in sync with the Inspector field) |
| WebGL | Your hosted game's own page URL, no query string (e.g. `https://yourgame.com/play`) | That exact hosted URL — add both a staging and production entry if you have separate domains |

> **Standalone/Editor picks a random loopback port on every login**, so it can never
> match one fixed `redirect_uri` exactly. If Jiwe's server validates `redirect_uri`
> as an exact string match, Editor testing will fail with a redirect_uri error no
> matter what you register. Two ways out: ask Jiwe (§8 support contact) whether
> standalone/editor redirect validation is host-based rather than exact-match, or
> pin `GetFreeLoopbackPort()` in `JiweAuth.cs` to a fixed port and register that
> exact `http://127.0.0.1:<port>/` instead.

The form also has an **"My app will..."** scope checklist — check every scope the SDK
actually requests (`openid`, `profile`, `in-app-purchases`, `rewards`; see the
`Scope` constant in `JiweAuth.cs`). Leaving one unchecked here produces the same
undiagnosable `access_denied` redirect as a missing `scope` param.

`networkTimeoutSeconds` (default 20s) bounds every step of this flow — a hung browser
or dead network fails visibly instead of hanging forever. **Always** also wire a
Skip/Cancel button, clickable the *entire* time login is in progress:

```csharp
skipButton.onClick.AddListener(() => {
    jiweAuth.Cancel(); // safe to call unconditionally, even if nothing's in progress
    ShowMainMenuAnonymously();
});
```

---

## 6. Security checklist

Real bugs found (and fixed, in this SDK) shipping a live game against Jiwe's actual
servers — not theoretical advice.

- [ ] **Skip/Cancel button stays clickable for the whole login flow**, not just
      before it starts. This was the single costliest mistake building against this
      SDK.
- [ ] **`apiSecret` is sent in the token exchange's POST body on every platform**,
      including a pure-PKCE mobile flow. Confirmed by a live 401 `invalid_client`
      when omitted — Jiwe's server isn't spec-pure "public client" here. Don't
      "fix" this away without re-testing against a real login.
- [ ] **No real credentials hardcoded in a committed script/scene/prefab.** Load
      them at runtime from a gitignored `ScriptableObject`:

  ```csharp
  var config = Resources.Load<MyJiweCredentialsConfig>("MyJiweCredentials");
  if (config != null) {
      jiweAuth.clientId = config.clientId;
      jiweAuth.apiSecret = config.apiSecret;
      jiweWallet.apiUsername = config.apiUsername;
      jiweWallet.apiKey = config.apiKey;
  }
  ```
  One `.gitignore` line (`Assets/Resources/MyJiweCredentials.asset`) keeps real keys
  out of git while every rebuild still picks them up.

---

## 7. Forcing an update via key rotation

If you need to push all players to a new build (breaking API change, key
compromise), rotate `apiUsername`/`apiKey` on Jiwe's side and gate a banner on
`CheckCredentialsValid`:

```csharp
jiweWallet.CheckCredentialsValid(valid => {
    if (!valid) ShowUpdateRequiredBanner();
});
```

This reports `false` **only** on a definitive 401/403 (credentials actively
rejected) — a network blip, timeout, or 5xx reports `true`, so a transient outage is
never mistaken for "this build is stale."

---

## 8. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `invalid_client` 401 on token exchange | `apiSecret` missing from POST body | It's required on every platform — see §6 |
| Login hangs forever with no error | No timeout / no Cancel wired | Set `networkTimeoutSeconds`, wire Skip/Cancel (§5) |
| WebGL token exchange fails with a CORS error | Jiwe hasn't allow-listed your hosted domain | Ask Jiwe to whitelist your domain |
| Reward call silently does nothing | Player not logged in | Reward calls need `auth.IsLoggedIn == true` first |
| Leaderboard entry shows XP `0` | Some entries carry the total under `currentXP` instead of `xp` | Prefer `currentXP` when nonzero |
| Response written after `HttpListener.Stop()` throws silently | `Stop()` called before the browser response finished writing | Already fixed in `LoginViaLoopback` — don't reorder if you touch it |
| Login redirect fails / `redirect_uri` error, only in Editor | Standalone/Editor's random loopback port doesn't match a registered exact `redirect_uri` | See the caveat under §5 "Redirect URIs" |

Still stuck (e.g. need your domain CORS-whitelisted, or an app-key issue)? Contact
Jiwe directly: WhatsApp **+254773754444** or **rock@jiwe.io**.

---

## 9. Jiwe IO platform basics

Steps outside the SDK itself — registering an account and creating your application.

<details>
<summary>Registering on Jiwe IO and creating an application</summary>

1. Go to [www.jiwe.io](http://www.jiwe.io), sign up, and log in.
2. Open **Profile** → **My Apps**.
3. **Create an Application** — this generates the `clientId` / `apiSecret` /
   `apiUsername` / `apiKey` set your game needs (see §2). Create one application
   per game. When you get to **Redirect URIs** and the scope checklist, see §5 —
   both are easy to get wrong and fail silently with an undiagnosable
   `access_denied`.

</details>

<details>
<summary>Uploading your game to Jiwe IO</summary>

Upload directly from the Jiwe IO upload page — no form or manual handoff needed.

</details>

---

## Roadmap

- **Unreal and Godot SDKs.** When they land, §2–4 above (credential model, API
  surface, security checklist) should carry over almost unchanged — only §5
  (platform redirect mechanics) is engine-specific and will need its own writeup
  per engine.
- **Celo wallet / Kotani Pay withdrawal flow.** Partially built — connecting a
  Jiwe wallet to Celo and withdrawing out to M-Pesa via Kotani Pay exists but
  isn't finished end-to-end. Not documented here yet; will get its own section
  once the flow is stable.
