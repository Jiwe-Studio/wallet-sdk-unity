# Jiwe Unity SDK: API reference

The supported public API for new integrations. Everything in this document lives in `Runtime/` and is covered by the core tests unless marked otherwise. For the legacy API, see [legacy-migration.md](legacy-migration.md).

All monetary and point values are `decimal` in **major units**. Nothing is returned in minor units, and a missing value is never reported as zero.

---

## Entry point: `Jiwe`

```csharp
public static class JiweSdk
```

| Member | Type | Description |
|---|---|---|
| `Auth` | `JiweAuthApi` | Sign-in, account switching, sign-out, session state |
| `Rewards` | `JiweRewardsApi` | Earned-XP reads and XP awards for the signed-in player |
| `Leaderboard` | `JiweLeaderboardApi` | The game's XP leaderboard, with the caller's placement |
| `IsInitialized` | `bool` | True after a successful `InitializeAsync` and before `Shutdown` |
| `InitializeAsync(JiweConfig)` | `Task` | Validates and stores the configuration. Calling it again ends any session first |
| `Shutdown()` | `void` | Cancels waiting sign-ins, clears the session, and stops accepting results from earlier work. Does not contact Jiwe |

**Throws:** `InitializeAsync` throws `JiweException` with `InvalidConfiguration` when the configuration is unusable.

---

## `JiweConfig`

```csharp
public sealed class JiweConfig
```

| Member | Type | Default | Description |
|---|---|---|---|
| `ClientId` | `string` | none | Public client ID issued by Jiwe. Not a secret. 1–128 printable ASCII characters, no spaces |
| `RedirectUri` | `string` | `http://127.0.0.1:51772/callback` | Must match the registration exactly. On Standalone builds only `http://127.0.0.1:<port>/<path>` is accepted |
| `Validate()` | `string` | — | Returns `null` if valid, otherwise a description of the first problem |
| `ValidateRedirectUri(string)` | `static string` | — | Validates a redirect URI on its own |
| `DefaultLoopbackRedirectUri` | `const string` | `http://127.0.0.1:51772/callback` | The default |

There are **no endpoint overrides**. The SDK contacts only the endpoints in `JiweEndpoints`.

### `JiweEndpoints` (read-only constants)

| Constant | Value |
|---|---|
| `Issuer` | `https://id.jiwe.io` |
| `Authorization` | `https://id.jiwe.io/auth` |
| `Token` | `https://id.jiwe.io/token` |
| `Jwks` | `https://id.jiwe.io/jwks` |
| `ResourceIndicator` | `https://api.jiwe.io` (Abacus audience) |
| `AbacusApi` | `https://abacus.jiwe.io/rest/api/v1` |

---

## `JiweAuthApi`: `JiweSdk.Auth`

| Member | Type | Description |
|---|---|---|
| `SignInAsync(CancellationToken = default)` | `Task<JiwePlayer>` | Opens the browser and waits for the player. Completes only after the ID token is verified |
| `SwitchAccountAsync(CancellationToken = default)` | `Task<JiwePlayer>` | Signs out locally, then signs in with the account chooser forced on |
| `SignOutAsync()` | `Task` | Clears the local session and cancels any waiting sign-in. Local only |
| `Status` | `JiweSessionStatus` | `SignedOut`, `SigningIn` or `SignedIn`. Reads check the token lifetime, so an expired session reports `SignedOut` |
| `IsSignedIn` | `bool` | True only when `Status == SignedIn` |
| `PlayerId` | `string` | The signed-in player's ID, or `null` |
| `StatusChanged` | `event Action` | Raised on every change of `Status`, including expiry and sign-out |
| `LoginTimeout` | `static TimeSpan` | 5 minutes |

**Throws:** `LoginCancelled`, `LoginFailed`, `LoginSuperseded`, `Timeout`, `InvalidConfiguration`.

---

## `JiwePlayer`

| Member | Type | Description |
|---|---|---|
| `Id` | `string` | Stable player identifier: the verified ID-token `sub`. Use it as a key, not as a display name |

The SDK does not expose a display name, email, phone number or wallet ID in this release.

---

## `JiweRewardsApi`: `JiweSdk.Rewards`

| Member | Type | Description |
|---|---|---|
| `GetEarnedXpAsync(CancellationToken = default)` | `Task<JiweEarnedXp>` | Lifetime earned XP for the signed-in player in this game |

**Throws:** `NotSignedIn`, `SessionExpired`, `SessionChanged`, `Unauthorized`, `InsufficientScope`, `RateLimited`, `ServiceUnavailable`, `Network`, `UnexpectedResponse`, `InvalidRequest`.

The value is **cumulative earnings**, not a spendable balance. Points are not exposed in this SDK.

### `JiweEarnedXp`

| Member | Type | Description |
|---|---|---|
| `Amount` | `decimal` | Earned XP, major units |
| `Unit` | `string` | Always `"XP"` |

---

## Errors: `JiweException`

Every failure from the public API is a `JiweException`.

| Member | Type | Description |
|---|---|---|
| `Kind` | `JiweErrorKind` | The category. Branch on this |
| `Message` | `string` | Developer-facing description. Never contains a token or raw provider payload |
| `Recovery` | `string` | What to do next |
| `HttpStatus` | `int` | The HTTP status, or `0` when no response was received |
| `RetryAfter` | `TimeSpan?` | Server-provided wait for `RateLimited` / `ServiceUnavailable`. HTTP-date values are ignored |

### `JiweErrorKind`

| Kind | Meaning |
|---|---|
| `InvalidConfiguration` | Configuration is missing or unsafe |
| `LoginCancelled` | The player declined or closed the page |
| `LoginFailed` | Sign-in could not be completed or verified |
| `LoginSuperseded` | A newer sign-in or sign-out replaced this attempt |
| `NotSignedIn` | No player is signed in |
| `SessionExpired` | The session lifetime ended. Public clients cannot refresh |
| `SessionChanged` | The account changed while the request ran |
| `InvalidRequest` | Jiwe rejected the request (HTTP 400). Usually a bug |
| `Unauthorized` | The token was rejected (HTTP 401). The session was cleared |
| `InsufficientScope` | The client may not read this data (HTTP 403) |
| `RateLimited` | Too many requests (HTTP 429) |
| `ServiceUnavailable` | Jiwe failed (HTTP 5xx) |
| `Network` | No response |
| `Timeout` | Sign-in was not completed in time |
| `UnexpectedResponse` | A response did not match the expected shape or range |

---

## Session status: `JiweSessionStatus`

`SignedOut`, `SigningIn`, `SignedIn`.

---

## Not included in this release

These are deliberately absent. Each has a precise blocker in [release-verification.md](release-verification.md#remaining-release-blockers).

- **Points rewards.** Not exposed. The SDK reads XP only.
- **Cowrie balance, earned or spendable.** `/oidc/rewards/earned` returns points and `XP`, and the SDK does not read Cowrie. There is no verified public-client Cowrie contract.
- **Spendable balance of any kind.** Earned totals are not balances.
- **Purchases, rewards issuance, airtime, payouts, refunds.** These are financial or administrative and belong on a trusted server.
- **Game data and user data.** Permitted for public clients but not implemented here.
- **Score (performance) leaderboards**, such as lap times. See [authentication.md](authentication.md#known-gap-no-id-token-for-api-key-routes).
- **Profile fields** (name, picture, email, phone).
- **Token refresh**, and **provider-side logout or revocation**.
- **Android, iOS, WebGL and Linux.**

---

## Internal types

`JiweSessionCore`, `JiweAuthorization`, `JiwePkce`, `JiweTokenResponse`, `JiweJwks`, `JiweIdTokenValidator`, `JiweRewardsParser`, `JiweHttpStatus` and `JiweSessionSnapshot` are compiled as part of `Jiwe.Core`. They are `public` only because `Jiwe.Runtime` is a separate assembly and must reach them. **They are not part of the supported API.** Do not depend on them; they may change without notice.

## XP awards: `JiweSdk.Rewards.AwardXpAsync`

```csharp
Task<string> AwardXpAsync(decimal xpEarned, string description, Guid awardId,
                          string gameSessionId = null, CancellationToken cancellationToken = default)
```

Posts to `/oidc/rewards/xp` and returns the ledger transaction ID. `xpEarned` must be positive with at most two decimal places, and `awardId` must not be `Guid.Empty`; otherwise it throws `InvalidRequest` without contacting Jiwe. Bearer writes must be enabled for the game.

**Retry with the same `awardId` and the same details.** The award ID is the idempotency key.

**Verified live (2026-11-14, Unity 2022.3.30f1, production, one test player):** an award of 1 XP raised `GetEarnedXpAsync` from 89.5 to 90.5. A retry with the same GUID and details returned the **same** `ledger_transaction_id`, and earned XP stayed at 90.5.

The success response has `"type": "SUCCESS"`. The SDK throws `UnexpectedResponse` for any other value, or if `ledger_transaction_id` is not a GUID. Note that the leaderboard response uses `"type": "OK"` instead (below), so do not share response-checking code between the two.

---

## Leaderboard: `JiweSdk.Leaderboard.GetLeaderboardAsync`

`GetLeaderboardAsync(limit, cancellationToken)` reads `/oidc/leaderboard` and returns a `JiweLeaderboard`. The default limit is 10 and the maximum is 100.

| `JiweLeaderboard` member | Type | Description |
|---|---|---|
| `TotalEntries` | `int` | Number of eligible players |
| `Entries` | `IReadOnlyList<JiweLeaderboardEntry>` | Ranked rows: `Rank`, `Score` (`decimal`), `DisplayName`, `IsCaller` |
| `Caller` | `JiweLeaderboardPlacement` | The signed-in player's `Rank` and `Score`, or `null` when unranked |

The SDK requires `"type": "OK"` and `"period": "all"` and throws `UnexpectedResponse` otherwise.

### Known server-side issue: awarded players missing from the leaderboard

Observed live on 2026-11-14: a test player with **90.5** earned XP (from `GetEarnedXpAsync`) was **not** on the leaderboard. `Caller` was `null` and `TotalEntries` stayed at 2. This held for reads straight after the award and for a read **more than 6 minutes** later, so it is not propagation lag. The response parsed correctly; the exclusion comes from Abacus and is not yet explained.

Until Jiwe resolves it:

- Treat **`GetEarnedXpAsync` as the source of truth** for a player's own XP. Do not derive it from the leaderboard.
- Treat `Caller == null` as "not shown", not as "has no XP".

### Scores can be inexact

Scores are summed on the server in binary floating point. A live read returned `233.19999999999985`. The SDK parses it as `decimal` but cannot recover the exactness the server lost. **Round for display**, for example `score.ToString("0.##")`, and do not compare scores for equality.
