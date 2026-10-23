# Changelog

Compared with the SDK as it stood on `main` before this release.

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
- `docs/use-cases.md` — code from KentePunk, Sheng Word League and JiweChess.

## Documentation

- README restructured around the integration path, with diagrams; redirect-URI and scope
  guidance; a trust model for what a shipped build can protect; a troubleshooting table keyed
  to real failures.

## Not changed / known gaps

- Not compiled against every Unity version and not run against live Jiwe from this change:
  it compiles cleanly against Unity 6000.0.41f1 for Standalone, WebGL, Android and iOS
  define sets. Marked **Docs** (not **Live**) in README §5 where we haven't exercised a call.
- `rewards/cowrie`, `rewards/data`, wallet-funded airtime/data purchases, notifications and
  the subscriptions/campaigns/pots surface are deliberately not wrapped (README §6).
