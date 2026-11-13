# Changelog

Compared with the SDK as it stood on `main` before this release.

## 2.0.0-rc.1: public-client SDK (release candidate, not released)

**Nothing in the legacy `Jiwe/` folder or `Samples/` changed.** Existing games keep working without action.

### Added

- A public-client path for new integrations: `JiweSdk.InitializeAsync`, `JiweSdk.Auth` (sign-in with
  Authorization Code + PKCE, account switching, local sign-out) and `JiweSdk.Rewards`
  (`GetEarnedXpAsync`). It needs a public client ID only, no API key and no client secret.
- Verified ID-token handling: RS256 signature against the Hermes JWKS, plus issuer, audience, expiry,
  `nbf`, nonce and subject checks.
- A session model with generation tracking, so obsolete callbacks and responses from a previous account are rejected.
- A Package Manager layout (`package.json`, `Runtime/Core` and `Runtime/Unity` assemblies) and one canonical sample (`Samples/Quickstart`).
- Documentation: `docs/quickstart.md`, `docs/authentication.md`, `docs/api-reference.md`,
  `docs/legacy-migration.md`, `docs/sdk-compatibility-matrix.md`, `docs/release-verification.md`.

### Not included

- Cowrie balances (earned or spendable), purchases, reward or airtime writes, game data and user data writes,
  leaderboards, profile fields, token refresh, and provider logout. See `docs/api-reference.md`.
- Android, iOS, WebGL and Linux. Windows and macOS standalone are the intended targets and are not yet verified on a device.

### Status

Not verified in the Unity Editor. Core logic passes 75 tests under .NET. See `docs/release-verification.md`.

## Behaviour changes — read these when upgrading

- **Token exchange now tries `Authorization: Basic` first**, then falls back once to the
  secret in the form body (`JiweAuth.clientAuth = Auto`). Previously the secret was always
  sent in the body. Pin `BasicHeader` or `RequestBody` to skip the fallback. See README §7.
- **WebGL needs `Jiwe/Plugins/WebGL/JiweStorage.jslib`.** Login now navigates the same tab
  and keeps the PKCE verifier in `localStorage`; `Application.OpenURL` and `PlayerPrefs`
  were unreliable for this flow. Copy the whole `Jiwe/` folder, including `Plugins/`.
- **`JiweAuth.UserInfo` is read from the ID token's claims**, with `/me` only filling gaps
  (Jiwe's `/me` has been seen rejecting every token).
- **`Login()` while a login is running is ignored** (with a warning); a deep link that
  arrives after `Cancel()` is ignored. New `IsLoggingIn`.
- Error text from the token step is capped at 300 characters.
- **`JiweWallet` sends `X-API-TOKEN` as before (ID token) unless `JiweAuth.walletResource`
  is set**, in which case it sends the access token. Override with `playerTokenKind`.

- **Removed `GivePointsReward`** (`POST /rewards`). For apps whose float is on the Cowrie reward
  ledger the route fails with "This operation moves money…", and Abacus has dropped it from the
  public OpenAPI contract. Use `GiveXpReward` or `GiveAirtimeReward`. Same change as PR #6.

## Added

- `PurchaseWithWallet`, `GetPurchaseStatus` — in-app purchases with a required idempotency key
  (README §6). Request fields: `amount`, `itemId`, `description`, `metadata`.
- `GetRewardsEarned` (API key + player token; lifetime earned totals, not a balance) and
  `GetRewardsEarnedBearer` (OIDC bearer, no API key).
- `GetUserData` / `SetUserData`, `GetGameData` / `SetGameData`.
- Results now carry `HttpStatus` and `Retryable`; `JiweWalletResult` also carries
  `LedgerTransactionId` and `Status`.
- `JiweLeaderboardEntry.XP` and `.Score` (period boards send an uppercase `XP`).
- `JiweWalletBalanceResult.*Amount` (exact values; the balance may be fractional).
- `GiveXpReward` now sends `transactionId` when you supply one.
- `JiweAuth`: `loopbackPort`, `webglRedirectUri`, `extraScopes`, `walletResource`,
  `clientAuth`, `AccessToken`, `AccessTokenExpiresAtUtc`, `IsAccessTokenExpired`,
  `IsLoggingIn`, and endpoint overrides for non-production environments.
- `JiweWallet.walletBaseUrl` for non-production environments.
- `JiweAuth.UrlOpener` — take over opening the login URL (an in-app browser tab, or a test).
- `PurchaseWithWallet` fails fast, with a clear message and no request, if `idempotencyKey` is empty or over 200 characters.
- `Samples/` — seven drop-in starters (guest-first bootstrap, XP + leaderboard, update gate, cosmetic shop, cloud
  save, airtime claim, server-in-front-of-Jiwe with a dependency-free Node server).
- `docs/use-cases.md` — code from KentePunk, Sheng Word League and JiweChess.
- `Tests~/` — a mock Jiwe server, a PlayMode suite for the SDK and every sample, a Node test for the WebGL plugin,
  a headless-Chrome WebGL end-to-end, and an opt-in read-only live smoke test.

## Documentation

- README restructured around the integration path, with diagrams; redirect-URI and scope
  guidance; a trust model for what a shipped build can protect; a troubleshooting table keyed
  to real failures.

## Not changed / known gaps

- **Tested, with limits.** The suite runs on Unity 6000.0.41f1 against a mock of Jiwe (see `Tests~/README.md`
  for what that does and doesn't prove). The code also compiles cleanly for the Standalone, WebGL, Android and iOS
  define sets. Not tested: login against the real `id.jiwe.io`, Android/iOS deep-link login, other Unity versions.
  Methods marked **Docs** (not **Live**) in README §5 are the ones no shipped game has exercised.
- `rewards/cowrie`, `rewards/data`, wallet-funded airtime/data purchases, notifications and
  the subscriptions/campaigns/pots surface are deliberately not wrapped (README §6).
