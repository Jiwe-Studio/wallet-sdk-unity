# Jiwe Unity SDK: Legacy migration and deprecation

This document covers the existing credential-based SDK (`Jiwe/JiweAuth.cs`, `Jiwe/JiweWallet.cs` and the WebGL plugin `Jiwe/Plugins/WebGL/JiweStorage.jslib`) and the path away from it.

**Summary.** The legacy SDK keeps working. Nothing in the `Jiwe/` folder was changed in this release, so existing games compile and behave exactly as before. New integrations should use the public-client API in `Runtime/`. Some legacy operations have **no safe direct-client replacement** and must move to a trusted game server; see section 3.

---

## 1. Two kinds of compatibility

Preserving an old credential format is not the same as preserving the authority it grants.

| | Legacy **authentication** compatibility | Legacy **authority** compatibility |
|---|---|---|
| What it means | An old game can still sign players in, and its existing requests still authenticate | An old game can still perform the financial and administrative actions its key allows |
| Status in this release | **Preserved** | **Not extended.** Each operation is documented below. None is promoted to the new API |
| Why | Avoids breaking deployed games | Client-side writes to rewards, purchases and app-wide config cannot be trusted. The review found amounts, prices and recipients are accepted from the client |

The SDK does not promote any legacy financial or administrative operation into the new beginner-facing API. Preserving a call does not endorse it.

## 2. What is preserved, byte for byte

- `Jiwe/JiweAuth.cs`, `Jiwe/JiweWallet.cs`, the WebGL `Jiwe/Plugins/WebGL/JiweStorage.jslib`, and the `Samples~/` tree are **unchanged** from the `origin/main` baseline (`9fb721f`). Verified with `git diff 9fb721f d4f4c09 -- Jiwe/ Samples/Unity/ Samples/server-node/`, which is empty.
- Every public member in the baseline is recorded in `Tests~/compat/legacy-public-api.golden`. `Tests~/compat/snapshot-legacy-api.sh` fails if any is removed or changed. This is a **regression guard**, not an independent oracle. It was generated from the same baseline.
- **No `[Obsolete]` attributes were added.** A deprecation warning on every legacy call would be noise for games that cannot migrate yet, and the mission asks for no noisy warnings. Deprecation is documented here and in the compatibility matrix.
- The legacy classes stay in `namespace Jiwe` with their existing names, so existing scenes and prefab references do not change. The new entry point is `JiweSdk` in the global namespace, deliberately not `Jiwe`, which would collide with that namespace.

**Not verified:** that a real legacy game still compiles and runs after this release. That needs a Unity Editor run of the legacy `Tests~/UnityTests` suite, which has not been done. See [release-verification.md](release-verification.md).

### Coexistence

New and legacy code can live in one project. The new `Jiwe` class, the `Jiwe.Runtime` assembly and the legacy `JiweAuth` / `JiweWallet` components do not share state. They use different default redirect ports (legacy: dynamic; new: `51772`). Running a legacy sign-in and a new sign-in at the same moment is **unverified**.

## 3. Operations with no safe direct-client replacement

These legacy operations must not be called from a shipped Unity build for new work. Each needs a trusted server, a hosted flow, or a decision to remove it from the game.

| Legacy operation | Why it has no direct replacement | Required destination |
|---|---|---|
| `JiweWallet.GiveXpReward`, `GiveAirtimeReward` | The client supplies the amount and, for airtime, the recipient. No idempotency header is sent. | Trusted game server, deriving the reward from authoritative game events |
| `JiweWallet.PurchaseWithWallet` | The client supplies the price. The response is not validated against a catalogue. | Server-owned catalogue and hosted checkout (not admitted for third parties in this release) |
| `JiweWallet.GetPurchaseStatus` | Recovery of a Cowrie purchase is not durable: the order ID can be absent. | Server-side reconciliation by ledger identity |
| `JiweWallet.SetGameData`, `SetUserData` | App-wide or per-player writes from the client are not a trust boundary | Trusted server, or none |
| `JiweWallet.GetWalletBalance` | Returns the **developer's app float**, not a player balance. The unit is dropped | Server-side only, if the developer needs the float |
| `JiweWallet.GetLeaderboard` | Cowrie scores are mapped to the wrong fields (the response carries `points`, not `XP`) and contact fields are requested | A server-side projection with a display-name-only score |
| `JiweWallet.GetTransactionStatus` | Checks organisation ownership, not player ownership | Server reconciliation |
| `JiweWallet.CheckCredentialsValid` | Returns `true` during outages and 404s, so it cannot prove credentials are valid | Use the new session API, or a release policy endpoint |
| `JiweWallet.GetGameData` | Requires the app key in the build | A scoped bearer read, planned but not in this release |

### Operations that are not yet in the new SDK but have a planned path

| Legacy operation | Planned path | Status |
|---|---|---|
| `GetUserData` | Bearer read with `user-data:read` | Permitted for public clients; not implemented in this release |
| `GetGameData` | Bearer read with `game-data:read` | Permitted for public clients; not implemented in this release |
| `GetRewardsEarned`, `GetRewardsEarnedBearer` | `JiweSdk.Rewards.GetEarnedXpAsync()` | **Implemented** |
| `Login` (legacy PKCE with `apiSecret`) | `JiweSdk.Auth.SignInAsync()` | **Implemented** |

## 4. Credential exposure: immediate assessment

The legacy setup puts credentials **inside the game build**:

- `JiweWallet.apiKey` and `apiUsername`, set in the Inspector (`Jiwe/JiweWallet.cs`).
- `JiweAuth.apiSecret`, which is also sent as an OAuth **client secret** in the legacy token request (`Jiwe/JiweAuth.cs`).
- `Samples~/Unity/JiweCredentialsConfig.cs`, which shows the same pattern.

Anyone with the binary can extract these. The review found that the key gates app-wide metadata writes and financial operations, subject to the app's configured limits, which were not verified. **No leak or exploitation was observed.** This is a risk assessment from source, not from an incident.

**What this release does and does not do about it:**

- Does **not** rotate, revoke or reissue any credential. Rotation needs separate authorisation from the credential owners.
- Does **not** change any production configuration or backend limit.
- Does **not** rely on a new SDK release to protect games already shipped. Older binaries keep their embedded credentials.

**Recommended actions for the Jiwe owners (not performed here):**

1. Inventory which shipped games embed `apiKey` / `apiSecret`, from the developer app list.
2. Decide per game whether to rotate, with the owner's agreement, after its migration path is ready.
3. Apply backend limits to each key's monthly and per-operation budget, so a leaked key is bounded.
4. Monitor anomalous write volume per app key.
5. Record the decision for each game in the migration tracker.

## 5. Phased migration plan

Each phase lists prerequisites, SDK and backend changes, developer actions, compatibility requirements, verification, rollback and human approvals. No phase has a fixed deadline. **Do not announce a removal date** until adoption and readiness evidence exists.

### Phase 0: Legacy inventory and safety

| | |
|---|---|
| **Prerequisites** | Access to the developer app list and build records. None are available to this review. |
| **SDK changes** | None |
| **Hermes / Abacus changes** | None to production. Read-only inventory only |
| **Developer actions** | List each game's legacy calls (`GetRewardsEarned`, `PurchaseWithWallet`, etc.) and whether it uses the key for writes |
| **Compatibility** | Nothing changes for players |
| **Verification** | Inventory reviewed by the game owners |
| **Rollback** | Not applicable |
| **Human approvals** | Owner sign-off on the inventory. Any credential rotation needs separate approval |

### Phase 1: Modern public-client SDK

| | |
|---|---|
| **Prerequisites** | Client registered per game through the public-native contract (see [authentication.md](authentication.md#2-client-model)). Platform verification done (see [release-verification.md](release-verification.md)) |
| **SDK changes** | This release: `Runtime/` and `Samples~/Quickstart/`. Legacy untouched |
| **Hermes / Abacus changes** | Provisioning of public native clients through the existing `db.js` contract. Verifying `rewards:read` on a test client |
| **Developer actions** | New games use `JiweSdk.InitializeAsync` / `JiweSdk.Auth` / `JiweSdk.Rewards`. Existing games need no change |
| **Compatibility** | All legacy members remain |
| **Verification** | Core tests (`Tests~/CoreTests`), legacy snapshot, Unity Editor import, platform sign-in on a provisioned client. **Only the first two have run** |
| **Rollback** | Remove the package. Legacy `Jiwe/` is unaffected |
| **Human approvals** | Release decision; client provisioning; platform sign-off |

### Phase 2: Assisted migration

| | |
|---|---|
| **Prerequisites** | Phase 1 complete. Each game's inventory from Phase 0 |
| **SDK changes** | Bearer reads for user data and game data (`user-data:read`, `game-data:read`), once specified and tested |
| **Hermes / Abacus changes** | Verify those scopes for public clients on a test client |
| **Developer actions** | Move the operations in section 3 to the game's server. Replace the sign-in with the public client |
| **Compatibility** | Legacy continues to work during migration. Operations moving to trusted servers need explicit handling in each game (a server route, or removal) |
| **Verification** | Each migrated game repeats its own platform test |
| **Rollback** | Keep the legacy build available until the migrated build is proven |
| **Human approvals** | Game owner per game. Server-side financial operations need their own approval |

### Phase 3: Restrict new legacy adoption

| | |
|---|---|
| **Prerequisites** | Phase 1 proven on at least one live game |
| **SDK changes** | Optionally add a documented warning, not a runtime one |
| **Hermes / Abacus changes** | Stop issuing new privileged app keys for **player-side** integrations |
| **Developer actions** | New games use only the public-client path |
| **Compatibility** | Existing keys keep working |
| **Verification** | Confirm no new player-side key issued after the policy date |
| **Rollback** | Reinstate issuance under documented exception |
| **Human approvals** | **Policy change needs explicit approval.** Not applied in this release |

### Phase 4: Retire legacy interfaces

| | |
|---|---|
| **Prerequisites** | Usage evidence shows the legacy interfaces are no longer used. Migration readiness, communication and rollback are all in place |
| **SDK changes** | Remove `Jiwe/` only in a major version, after a notice period |
| **Hermes / Abacus changes** | Retire the key-based player endpoints, only after their usage is zero |
| **Developer actions** | Migrate every remaining game |
| **Compatibility** | Breaking. Requires a major version |
| **Verification** | Usage metrics, plus the migration tracker showing every game moved |
| **Rollback** | Restore the legacy package from its tagged version |
| **Human approvals** | Explicit approval from the SDK owner and the backend owners. **No deadline is set** |

## 6. Developer checklist: moving a game

1. Remove the legacy `JiweWallet` and `JiweAuth` components from the scene.
2. Remove `apiKey`, `apiSecret` and any credential asset from the build. Confirm with a search of the project.
3. Add the new package and the public client ID.
4. Use `JiweSdk.Auth.SignInAsync()` and `JiweSdk.Rewards.GetEarnedXpAsync()` for sign-in and reads.
5. Move every reward, purchase and config write to your server. Do not call a replacement from the client.
6. Test sign-in, cancellation, sign-out and account switching on each platform you ship.
7. Keep the legacy build available until the new build is verified.
