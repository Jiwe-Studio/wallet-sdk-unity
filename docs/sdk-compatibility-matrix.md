# Jiwe Unity SDK: Compatibility matrix

What is compatible with what, and how. Read with [legacy-migration.md](legacy-migration.md), which covers the path and the approvals needed.

**Baseline:** `origin/main` at `9fb721f` (PR #7, 23 October 2026). The local checkout at `~/Projects/wallet-sdk-unity` is an older commit (`dd07fd6`) and is not the baseline.

---

## A. Legacy public API: preserved

Every public member in the baseline is recorded in `Tests~/compat/legacy-public-api.golden` (112 lines). `Tests~/compat/snapshot-legacy-api.sh` fails if any is removed or its signature changes. Against the baseline, the legacy source differs only in doc comments (one line in `JiweWallet.cs`, three legacy samples). No executable line changed. The package also now ships `Jiwe/Jiwe.Legacy.asmdef` and `.meta` files; see [legacy-migration.md §2](legacy-migration.md#2-what-is-preserved).

| Area | Members | Status |
|---|---|---|
| `JiweAuth` (MonoBehaviour) | `clientId`, `apiSecret`, `clientAuth`, `mobileRedirectScheme`, `loopbackPort`, `extraScopes`, `walletResource`, `webglRedirectUri`, `authorizationEndpoint`, `tokenEndpoint`, `userInfoEndpoint`, `loginOnStart`, `networkTimeoutSeconds`, `IdToken`, `AccessToken`, `AccessTokenExpiresAtUtc`, `IsAccessTokenExpired`, `IsLoggedIn`, `IsLoggingIn`, `UserInfo`, `OnLoginSuccess`, `OnLoginFailed`, `Login()`, `Cancel()`, `UrlOpener` | **Preserved, unchanged** |
| `JiweWallet` (MonoBehaviour) | `apiUsername`, `apiKey`, `auth`, `playerTokenKind`, `walletBaseUrl`, `GameSessionId`, and 14 operations | **Preserved, unchanged** |
| DTOs | `JiweUserInfo`, `JiweWalletResult`, `JiweWalletBalanceResult`, `JiweRewardsEarnedResult`, `JiwePurchaseResult`, `JiweLeaderboardResult`, `JiweLeaderboardEntry`, `PlayerTokenKind`, `TokenClientAuth` | **Preserved, unchanged** |
| `JiweReward`, `JiweOAuth2` | All members | **Preserved, unchanged** |

**Known legacy defects that remain, deliberately.** The review identified defects in the legacy path. Changing legacy behaviour would silently alter deployed games, so they are not fixed in this release. They are listed in the release verification document, and the migration path is the remedy.

## B. Legacy configuration fields: preserved

Serialised fields keep their names, so existing scenes and prefabs still load. The new SDK has no serialised fields that collide with them.

| Component | Serialised fields | Compatibility |
|---|---|---|
| `JiweAuth` | as in section A | Unchanged. A stored `apiSecret` still loads |
| `JiweWallet` | `apiUsername`, `apiKey`, `auth`, `playerTokenKind`, `walletBaseUrl` | Unchanged |
| `JiweQuickstartSample` (new) | `clientId` only | New. Holds no secret |

**Verified in the Editor (Unity 2022.3.30f1, macOS):** the legacy `Tests~/UnityTests` suite passes, 101 passed, 0 failed, 1 skipped (the live smoke test, which needs credentials). **Not verified:** loading a real shipped game's scenes.

## C. Legacy request headers and token modes: preserved

These are unchanged, and they are where the legacy path is most fragile.

| Mode | Header behaviour (legacy) | Notes |
|---|---|---|
| Key (`api_key`, `api_secret`) | Sent on every wallet call | The privileged credential in the build. See [legacy-migration.md §4](legacy-migration.md#4-credential-exposure-immediate-assessment) |
| ID token (`playerTokenKind = IdToken`, or `Auto` without `walletResource`) | `Authorization: Bearer <ID token>` | Sent to Abacus. The review found this is the decisive mismatch when the ID token's audience is not the linked client |
| Access token (`playerTokenKind = AccessToken`, or `Auto` with `walletResource`) | `Authorization: Bearer <access token>` | Resource token; fails the legacy audience check on some routes |

**Not changed:** `Auto` token selection, which the review describes as an implicit mode switch. Changing it would alter legacy behaviour. The new SDK does not use it.

## D. New API: how it maps to the legacy API

| Need | Legacy | New (public-client path) |
|---|---|---|
| Configure | Inspector fields on `JiweAuth`, `JiweWallet` | `JiweSdk.InitializeAsync(new JiweConfig { ClientId = ... })` |
| Sign in | `JiweAuth.Login()` + `OnLoginSuccess` | `await JiweSdk.Auth.SignInAsync()` |
| Sign-in state | `IsLoggedIn`, `IsAccessTokenExpired` | `JiweSdk.Auth.IsSignedIn`, `JiweSdk.Auth.Status` |
| Cancel | `JiweAuth.Cancel()` | `CancellationToken` on `SignInAsync` |
| Account identity | `UserInfo` (name, nickname, picture, walletId) | `JiweSdk.Auth.PlayerId` (verified `sub` only) |
| Sign out | none | `await JiweSdk.Auth.SignOutAsync()` |
| Account switch | none | `await JiweSdk.Auth.SwitchAccountAsync()` |
| Earned XP | `JiweWallet.GetRewardsEarned` / `GetRewardsEarnedBearer` (key or bearer) | `await JiweSdk.Rewards.GetEarnedXpAsync()` (bearer) |
| Award game-reported XP | `JiweWallet.GiveXpReward` (key) | `await JiweSdk.Rewards.AwardXpAsync(xp, description, awardId)` (bearer, idempotent by `awardId`) |
| XP leaderboard | `JiweWallet.GetLeaderboard` (key) | `await JiweSdk.Leaderboard.GetLeaderboardAsync()` (bearer). Known server-side issue; see [api-reference.md](api-reference.md#known-server-side-issue-awarded-players-missing-from-the-leaderboard) |
| Score (performance) leaderboards | none | **None.** Needs the player's ID token, which the SDK does not expose; see [authentication.md](authentication.md#known-gap-no-id-token-for-api-key-routes) |
| Airtime rewards, purchases, config writes | legacy `JiweWallet` | **None.** Move to a trusted server |

**Behaviour differences a legacy developer will notice:**

- **Identity fields.** The new SDK exposes only the verified `sub`. Name, nickname, picture and wallet ID are not available, because the public-client scope excludes `profile`.
- **Missing values.** Legacy code could read a missing total as `0`. The new SDK throws `UnexpectedResponse`.
- **Units.** Legacy values were `float` and `int`, and in places dropped the unit. The new SDK uses `decimal` in major units and states the unit.
- **Expiry.** Legacy `IsLoggedIn` only checked that an ID token existed. The new SDK checks the access-token lifetime, and expiry means signing in again.

## E. Serialised DTO fields: preserved

The legacy DTOs are serialised with `JsonUtility`, which is case-sensitive. Field names are unchanged. `JiweLeaderboardEntry.XP` (uppercase) and `currentXP` are kept deliberately, because removing either would zero the score on live boards. The new SDK does not use these DTOs.

## F. Samples

| Sample | Status |
|---|---|
| `Samples/Unity/*` (legacy samples) | **Code unchanged** (doc comments only). Still use the key-based API. Compiled and exercised by the legacy suite in Unity 2022.3.30f1 |
| `Samples/server-node/*` (legacy server sample) | **Unchanged.** Known defects remain; see the release verification document |
| `Samples/Quickstart/QuickstartSample.cs` (new canonical sample) | New. Public API only. **Compiles in Unity 2022.3.30f1** against a Git-installed package (2026-11-16). Not run in the Editor |

## G. Unity Editor and install method

| Item | Status |
|---|---|
| Unity **2022.3.30f1**, macOS Editor | **Verified 2026-11-14:** production sign-in through the loopback redirect (`http://127.0.0.1:51772/callback`), ID-token verification, earned-XP read, XP award with idempotent retry, leaderboard read (parses; see the known issue), sign-out |
| Unity 6, Windows Editor, any player build | **Not verified** |
| UPM **Git URL** install | Compiles only from a commit that ships `.meta` files and `Jiwe/Jiwe.Legacy.asmdef`. `release/unity-bearer-xp` at `d4f4c09` compiles nothing. With the packaging fix, Git and `file:` installs both compile `Jiwe.Core`, `Jiwe.Runtime` and `Jiwe.Legacy` in 2022.3.30f1 (2026-11-16) |
| Copy into `Assets/` | Works for the legacy `Jiwe/` folder, as before |

## H. Coexistence

A legacy component and the new SDK can be in one scene. They use separate state and separate default redirect ports (legacy: dynamic; new: `51772`). **Running both sign-ins at once is unverified.**

## I. Known consumers and version pinning

**Not established.** The repository contains no registry of games or consumers, no tags and no GitHub releases. The review found no published package. Installed game binaries cannot be enumerated from here. This must be established by Jiwe before any deprecation decision.

## J. Backend compatibility risks

| Risk | Owner | Note |
|---|---|---|
| The `resource` parameter is required for the Abacus audience. Removing it on Hermes would break the new SDK | Hermes | Covered by the Hermes source contract |
| The public-client registration must match the exact allow-list in `db.js`. Adding a scope breaks registration | Hermes | The SDK depends on that exact set |
| `ID token` signed with RS256 by default. A change to PS256 would make the new SDK fail closed | Hermes | Discovery advertises PS256 too |
| `/oidc/rewards/earned` returns `XP` (the SDK requires it; points are ignored). Adding a field is safe; removing `XP` breaks the SDK | Abacus | The parser requires `XP` |
| Legacy key-based routes remain live for existing games | Abacus | Required for backward compatibility |
| `/oidc/rewards/xp` success is `"type": "SUCCESS"` with a GUID `ledger_transaction_id`; `/oidc/leaderboard` is `"type": "OK"` with `"period": "all"`. Changing either value breaks the SDK | Abacus | Both shapes matched in the 2026-11-14 live run |
| `/oidc/leaderboard` omitted a player with earned XP (2026-11-14) | Abacus | Unexplained. In the 2026-11-14 verification report sent to Jiwe |
