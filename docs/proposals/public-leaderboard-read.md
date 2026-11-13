# First-party leaderboard read and XP write implementation plan

**Status:** Implementation plan. The listing policy, narrow Sidekick lookup, and basic rate limits and timeouts are approved. This document records code findings and release dependencies.

## 1. Decisions and existing code

- **Public listing is part of game sign-in.** A player who signs in to a game with their Jiwe account agrees to have their nickname displayed on that game's public leaderboard when they have a qualifying score. Use the Jiwe username as the nickname, consistent with the mapping in Hermes `support/account.js`. No separate listing opt-in or leaderboard read consent screen is needed. Reflect this policy in the game's existing sign-in copy. Exclude deleted accounts, accounts without a nickname, and accounts with an applicable visibility suppression. The Unity read uses an authenticated session to select the board and identify the caller.
- **Abacus already authenticates and resolves the application.** `server/rest/middleware/hermes_bearer.ts` verifies the Hermes signature, issuer, Abacus audience, expiry and any `nbf` with RS256, and requires `sub` and `client_id`. `abacus_application.ts` resolves exactly one active `api_clients` row from that client ID. The verifier accepts a nonempty subject and does not classify the grant type. Hermes emits `org` and `project` for configured client-credentials tokens, but absence of those claims alone cannot prove a player grant.
- **Use the existing public-native mapping as the first-party boundary.** Abacus's `provision_android_oidc_client` mutation registers an authorization-code-only native Hermes client and links it to the game as public. Require the verified client ID to be both actively mapped and recorded as public/native through `isPublicOidcClient`. The authorization-code-only registration supplies the player-token guarantee for this path. Treat service claims as a rejection signal and verify the contract in automated tests and production. For a manually registered Unity client, verify the same grant restriction and record the public link through the existing mapping mechanism before it becomes eligible. A primary mapping by itself is insufficient.
- **Provisioning and revocation already have an owner path.** A Jiwe administrator or the API-client owner recorded in `metadata.jiwe_user_id` can provision and unlink a platform client, as enforced by `authorizeApplication` in Abacus `src/client/platform_client.ts`. The current owner check reads one owner ID. For a game with two owners, use the recorded owner or a Jiwe administrator for rollout. `unlink_platform_client` removes Abacus access at the next request, including for an unexpired token. The eligible set comes from active, public-native game mappings. Code establishes those criteria and the authorized actors. The list of deployed mappings and the first game to release against are rollout data to confirm in the target environment.
- **Reuse the current SDK authorization request.** The route checks the first-party mapping and player token. It adds no scope, client secret, API key, registration scope, or token claim. The existing SDK scope request continues to supply the Abacus-audience token.

## 2. Sidekick and game sign-in evidence

1. Add a dedicated internal batch lookup for leaderboard nicknames. Use the existing internal service boundary and require authenticated service access. Validate and deduplicate IDs, cap batches at 100 and return only each eligible user's internal join ID and current nickname. Use a column projection for `id` and `username` and apply deletion and any existing suppression checks in the batch. The legacy `/auth/get-users` handler sends contact fields and stays outside this read path.
2. Use existing Hermes game login records for the linked game client IDs to establish listing eligibility. The supported username/password flow records the client and player through `saveLogin` in `routes/koa.js`. Use the existing records as stored.
3. Resolve names fresh for displayed rows and apply the same eligibility rules to ranking and counts, including players below the cutoff. Use a bounded read path over existing scores and account state. Return a retryable service error if eligibility cannot be resolved reliably.

## 3. Abacus

1. **Read the existing score source.** Use the mapped game’s all-time XP board, the legacy `global` dimension and `points` feature. The award writer stores XP in major units and sync converts ledger values before writing, so use the stored scale directly. The endpoint is read-only and uses the existing data in place. Verify that the mapped board is distinct from other games and `OVERALL_XP` in the route tests.
2. **Add the read endpoint.** Mount `GET /oidc/leaderboard` under the existing Abacus API base (`/rest/api/v1` for the SDK). Use the OIDC boundary plus the public-native mapping check. Resolve the board from the verified application and the caller from the verified subject. Accept only `rewardType=xp`, `period=all` and integer `limit` from 1 through 100, defaulting to 10. Return `400` for invalid, unknown or duplicate parameters. Accept no caller-supplied game or player identifiers.
3. **Read one board.** Fetch top rows, eligible count and caller rank and score from one consistent view of the selected board. Use targeted Redis reads and bounded Sidekick lookups. Apply eligibility before rank, count and limit. Use competition ranks (`1, 1, 3`) with deterministic ordering by internal ID for ties and enforce the row limit through a tie. Return major-unit scores and the following shape. The caller rank is available below the cutoff. A caller with no eligible score gets `null`. An empty board returns zero entries.

   ```json
   {
     "type": "OK",
     "period": "all",
     "totalEntries": 128,
     "entries": [
       { "rank": 1, "displayName": "Amina", "score": 450.5, "isCaller": false }
     ],
     "caller": { "rank": 18, "score": 90 }
   }
   ```

4. **Reuse operational controls.** Apply the existing Redis rate-limit helper before board work with a route-specific bucket keyed by game and verified player. Start with a simple per-minute limit and the existing API-wide limiter. Use the existing dependency timeout conventions or a short route-level timeout where needed. Return `429` with `Retry-After` when throttled, `401` for invalid or mixed credentials, `404` for unresolved mappings, `403` for an ineligible mapped client, and a generic `5xx` for dependency faults. Set `Cache-Control: no-store` on route responses and keep names, scores and tokens out of logs.
5. **Verify.** Exercise existing sign-in without a new prompt, wrong-audience and service-token rejection, mapping unlink with an existing token, two-game isolation, fractional XP, deleted or suppressed top and off-page players, empty boards, out-of-page caller rank, ties at the cutoff, timeouts and throttling. Test that neither Sidekick's narrow response nor the public response exposes contact fields.

## 4. Unity SDK

1. Add `JiweSdk.Leaderboard.GetLeaderboardAsync(limit, cancellationToken)` following `Runtime/Unity/JiweRewardsApi.cs`. Reuse the existing bearer HTTP transport and session-generation check so a response for a previous account is discarded after account switching. Use the existing sign-out behavior on `401`. Map `403` and `404` to a leaderboard availability error with guidance about the application mapping. Reuse `429` handling and retry timing.
2. Add typed results for nickname, rank, decimal XP score, caller marker, eligible count and nullable caller placement. Keep the legacy API-key leaderboard method independent of this path. Update the public SDK Quickstart and sample to sign in, show the ranked rows and show the player's own rank below the cutoff.
3. Test success and failure fixtures, the fractional score, an empty board, a caller outside the top rows, and a session switch during a request. After deployment, run the sample against the production route with the existing native client and SDK scopes.

## 5. Bearer-authenticated XP writes

1. **Add one Abacus endpoint.** Mount `POST /oidc/rewards/xp` under the same API base and first-party OIDC boundary as the leaderboard read. Require the existing Abacus-audience bearer token and derive the game and recipient from the verified application mapping and subject. Reject API-key headers and caller-supplied game or recipient IDs. Reuse the existing sign-in flow and scopes.
2. **Apply the game's XP policy.** Enable client-reported awards explicitly per game in server-owned configuration. Accept `xpEarned` as a positive finite number in major units at the existing XP precision, plus the existing description and optional `gameSessionId` metadata. XP has no monetary value. Accept client-reported XP without per-award or daily caps. Apply the existing request rate limiter and idempotency checks.
3. **Share the existing XP award logic.** Extract an internal operation called by both the bearer route and `POST /rewards/xp`. Pass the authenticated application, verified player, and validated award into it. Reuse the existing wallet and ledger selection, unit conversion, ledger transfer, and game and overall leaderboard updates. Resolve the username from the authenticated application for the bearer path. Preserve the legacy route's credentials and request contract.
4. **Make ledger and leaderboard retries safe.** Require an `Idempotency-Key` on bearer writes, scoped to the game, player and XP award operation. An identical retry returns the original transaction ID. Reject reuse of the same key with different award data. Use `transferFundsDetailed` so the shared operation knows when the ledger returns a replay. The current handler treats any returned transaction ID as a fresh award and can increment Redis twice. For each board, atomically apply the leaderboard update and record the ledger transaction ID as applied. A retry can then complete a missed update after a ledger commit or partial leaderboard failure, while an already-applied transaction contributes zero additional XP. Use the same replay-safe update for the legacy route. Start this protection with new awards and preserve existing board data.
5. **Expose the write in Unity.** Add `JiweSdk.Rewards.AwardXpAsync` using the existing bearer transport and account-switch protection. Reuse one idempotency key for all attempts to submit the same award, including a retry after a timeout. Return the ledger transaction ID. Update the Quickstart, README, use-case guide and leaderboard sample to show the bearer write for enabled games. Replace direct legacy XP calls in the recommended Unity samples and remove their wallet key setup. Keep `JiweWallet.GiveXpReward` available for legacy compatibility and document that it sends the API key from Unity. Developers moving an existing shipped build to bearer writes remove and rotate the shipped key.
6. **Verify retries and access.** Test a duplicate request, a conflicting payload with the same key, concurrent retries of the same award, and a retry after the ledger commits but only one board updates. Check that both boards receive the award exactly once. Exercise disabled mappings, invalid amounts and the Unity account-switch behavior. Verify the public sample build uses only the public client ID and player session.

## 6. Dependencies and release approvals

| Step | Dependency or approval |
|---|---|
| Implement the Sidekick lookup, Abacus route and SDK client | Approved. Sign-in listing policy, narrow lookup, freshness work and basic operational controls are approved. |
| Enable bearer XP writes for a game | The game owner or Jiwe administrator enables client-reported XP for the game. Include the bearer SDK method and key-free examples in the Unity release. |
| Release directly to production | The recorded API-client owner or a Jiwe administrator confirms the initial game’s public-native mapping. Deploy Sidekick first, then Abacus, then ship the Unity SDK. Verify bearer reads and writes in production using the initial game. Cover mapping revocation in automated tests. |
