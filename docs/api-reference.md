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
| `Rewards` | `JiweRewardsApi` | Reads of earned totals for the signed-in player |
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
- **Leaderboards.**
- **Profile fields** (name, picture, email, phone).
- **Token refresh**, and **provider-side logout or revocation**.
- **Android, iOS, WebGL and Linux.**

---

## Internal types

`JiweSessionCore`, `JiweAuthorization`, `JiwePkce`, `JiweTokenResponse`, `JiweJwks`, `JiweIdTokenValidator`, `JiweRewardsParser`, `JiweHttpStatus` and `JiweSessionSnapshot` are compiled as part of `Jiwe.Core`. They are `public` only because `Jiwe.Runtime` is a separate assembly and must reach them. **They are not part of the supported API.** Do not depend on them; they may change without notice.

## Leaderboard and XP awards

- `JiweSdk.Leaderboard.GetLeaderboardAsync(limit, cancellationToken)` returns the eligible count, ranked rows, and optional caller placement. The default limit is 10 and the maximum is 100.
- `JiweSdk.Rewards.AwardXpAsync(xpEarned, description, awardId, gameSessionId, cancellationToken)` returns the ledger transaction ID. Bearer writes must be enabled for the game. Reuse the award GUID and details when retrying.
