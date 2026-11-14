# Jiwe Unity SDK: Release verification record

**Release candidate:** `2.0.0-rc.1` on branch `feature/unity-public-client-sdk`, based on `origin/main` at `9fb721f`.

**Release state: READY FOR HUMAN REVIEW.** The code and the feasible local checks are complete. The package has **not** been verified in the Unity Editor, on any platform, or against a provisioned production client. It is **not** ready for a pilot or a public release. See the gates below.

---

## 1. Verification states

Each state is separate. A passing state does not imply the next.

| State | Status | Evidence |
|---|---|---|
| **Code ready** | Yes | Runtime, sample, tests, package manifest and documentation are written. Uncommitted on the branch (see section 6) |
| **Locally verified** | Partly | Core logic: 75/75 tests pass under .NET 11. Loopback listener: exercised against the real `HttpListener`. Legacy API snapshot: passes. The Unity layer is **not** run; see below |
| **Contract verified (source)** | Yes, from source and live metadata | Hermes and Abacus source plus the live discovery and JWKS documents match the implementation. See section 4 |
| **Contract verified (live, authenticated)** | **No** | No provisioned client, no player and no bearer token. Nothing authenticated was attempted |
| **Production authenticated** | **No** | Not attempted. Requires a provisioned public client and a test player, which do not exist yet |
| **Platform verified** | **No** | No platform has run the sign-in journey. The Unity Editor is not installed |
| **Release ready** | **No** | Blocked by the gates in section 7 |

## 2. Commands run and results

Environment: macOS (darwin 27.2), Homebrew .NET SDK 11.0.100 (runtime 11.0.0), Newtonsoft.Json 13.0.3 from NuGet. The Unity Editor is **not available**: Unity Hub is installed, but no editor is.

| Command | Result |
|---|---|
| `dotnet run --project "Tests~/CoreTests"` | **75 passed, 0 failed.** Covers PKCE (RFC 7636 vector), config and redirect validation, authorize and token request shape, callback validation, token parsing, ID-token RS256 verification (valid, tampered, `alg none`, HS256 downgrade, unknown kid, wrong issuer, audience, `azp`, expiry, `nbf`, nonce, subject, oversize), live-shaped JWKS, XP parsing (missing field, string, negative, extra fields), HTTP status mapping, session generation and account-switch rules, and four real-`HttpListener` loopback tests (delivery and 404, timeout frees the port, cancellation frees the port, busy port) |
| `dotnet build "Tests~/UnityCompileCheck"` | **Build succeeded**, against UnityEngine **stubs**. Shows the Unity layer and the sample compile against the members they call. This is **not** an Editor compile |
| `Tests~/compat/snapshot-legacy-api.sh` | **All 112 golden members present.** This is a regression guard generated from the same baseline, not an independent oracle |
| `git diff origin/main -- Jiwe/ Samples~/` | **Empty.** Legacy code unchanged |
| `curl https://id.jiwe.io/.well-known/openid-configuration` (anonymous) | HTTP 200. S256 supported; `token_endpoint_auth_methods_supported` includes `none`; `refresh_token`, `end_session_endpoint` and `revocation_endpoint` advertised |
| `curl https://id.jiwe.io/jwks` (anonymous) | HTTP 200. One RSA key, `use: sig`, no `alg` field, 2048-bit modulus. The SDK's parser accepts this shape (regression test added) |
| `curl https://abacus.jiwe.io/rest/api/v1/oidc/rewards/earned` (anonymous) | HTTP 401 `"A Bearer access token is required"`. Consistent with the bearer contract |

**Not run:** the legacy Unity test suite (`Tests~/UnityTests`), the Unity PlayMode tests, the WebGL suite, any desktop sign-in, any authenticated Abacus call, and any Android or iOS build. Reasons: no Unity Editor, no provisioned client and no test player.

## 3. Test-coverage gaps (from the mission's list)

The mission asks for coverage of the new path and the legacy path. This table shows which items are covered, which are only partly covered, and which are not run.

| Mission item | Status |
|---|---|
| Initialisation, valid and invalid configuration | Covered (core tests) |
| PKCE generation and redirect validation | Covered |
| Login success | **Partly.** The callback and token parsing are covered. The full browser journey is not |
| Login cancellation | **Partly.** Covered by the loopback cancellation test and the `access_denied` callback. Not run end to end |
| Expired tokens | Covered (session and ID-token expiry) |
| Wrong audience | Covered (ID token) |
| Wrong client or game | Covered (ID-token audience and `azp`) |
| Missing scope | **Not covered.** A 403 maps to `InsufficientScope`, but no live or fixture response was tested through the Unity layer |
| Authenticated Abacus reads | **Not run.** No credential or client. The parser is covered |
| Missing response fields | Covered (XP parser) |
| Unexpected response values | Covered (XP parser: string, null, negative) |
| Rate limiting | Covered at the mapping level (429 and Retry-After). Not run live |
| Network interruption | Covered at the mapping level (status 0). Not run live |
| Account switching | Covered at the session level. Not run in the Unity layer |
| Sign-out | Covered at the session level |
| Obsolete callback rejection | Covered (state mismatch, and generation after sign-out) |
| Unity object lifecycle | **Not covered by any runnable test.** `JiweSdk.Shutdown` and the sample's `OnDestroy` are reviewed but not exercised |
| Legacy configuration loads | **Not run** (needs the Unity Editor) |
| Legacy public methods callable | **Not run** (needs the Unity Editor). The API snapshot shows the signatures are present |
| Legacy headers unchanged | Verified by source identity (`git diff` is empty) |
| Modern and legacy coexist | **Not run** |

## 4. Contract verification

| Contract | Source checked | Live checked | Result |
|---|---|---|---|
| Hermes authorization endpoint, PKCE S256, `resource` (RFC 8707), `prompt` | `hermes/support/configuration.js`, `routes/koa.js` | Discovery document | Matches the implementation |
| Public-native client allow-list (grant, response, scopes, auth method, exact redirect) | `hermes/db.js` lines 54–160 | — | The registration in the Quickstart must match this exactly |
| Abacus bearer audience and scope | `hermes/support/configuration.js` (`ABACUS_RESOURCE`, `rewards:read`); `hermes/k8s/prod/deployment.yaml` (`https://api.jiwe.io`) | — | Matches the implementation |
| XP response shape `{points, XP}` in major units | `abacus/server/rest/routes/oidc.ts`; `abacus/server/rest/routes/oidc_rewards_test.ts` | Anonymous 401 only | Matches the implementation. **Live shape not observed** |
| ID-token signing algorithm | Hermes default `RS256` | JWKS: RSA keys | Matches the implementation. PS256 is rejected by design |
| Refresh not granted to public clients | `hermes/db.js` | — | The SDK does not refresh |

**The commit pins:** the review cited Abacus `129f846` and Hermes `aa97571`. The checkouts on this machine are at different commits (`a5f6c3c` and `6c63456`). The contracts above were read from these local checkouts, not from the pinned production commits. This is a **gap**: the production commits should be re-read before release.

## 5. Adversarial review

See section 5a.

### 5a. Findings and remediation

An independent subagent reviewed the code and documents with instructions to break them. It returned ten findings and found no way to forge or replay an ID token. Each finding was checked against the code before being fixed.

| # | Severity | Finding | Status |
|---|---|---|---|
| 1 | Blocker | The global `Jiwe` entry class collided with the legacy `namespace Jiwe` (`Jiwe/JiweAuth.cs`, `Jiwe/JiweWallet.cs`). Confirmed | **Fixed.** Entry class renamed to `JiweSdk` (file `Runtime/Unity/JiweSdk.cs`); all code and docs updated. Note this differs from the illustrative `Jiwe.` in the brief |
| 2 | Major | A stray or forged request to the callback path with a wrong state ended the sign-in | **Fixed.** The listener now answers 400 to a wrong-state request and keeps waiting. Regression test added (real `HttpListener`) |
| 3 | Minor | Redirects were followed, so the token body and bearer header could leave the fixed host | **Fixed.** `redirectLimit = 0` on every request |
| 4 | Minor | Cancellation from a non-main thread skipped the abort | **Fixed.** Cancellation is polled on the main thread with `Task.Yield`, and the abort happens there. Not run in Unity |
| 5 | Minor | A redirect URI with a query passed validation but could never complete sign-in | **Fixed.** Rejected by `JiweConfig.ValidateRedirectUri`. Regression test added |
| 6 | Minor | An offline token exchange reported "refused (HTTP 0)" | **Fixed.** Status 0 maps to `Network` |
| 7 | Minor | An expired session still read as signed in until the next call | **Fixed.** `Status` and `Current` check expiry. Regression test added. `docs/api-reference.md` describes it |
| 8 | Minor | The legacy docs named files that do not exist at the baseline (`JiweReward.cs`, `JiweOAuth2.cs`) | **Fixed.** Corrected to the real baseline files |
| 9 | Minor | A throwing `StatusChanged` subscriber broke the state machine | **Fixed.** Each subscriber is isolated. Regression test added |
| 10 | Minor | The sample allowed Sign in before initialisation | **Fixed.** Buttons are gated on `JiweSdk.IsInitialized` |

**Still unverified after review:** whether `HttpListener` works on IL2CPP, and on Windows without administrator rights for a `127.0.0.1` prefix. Whether Hermes sends `iss` on the callback. The code requires it, so if Hermes omits it every sign-in fails. This is a live gate (G4).

## 6. Working-tree state

- Branch: `feature/unity-public-client-sdk`, created from `origin/main` in a separate worktree at `~/Projects/wallet-sdk-unity-rc`.
- The local checkout `~/Projects/wallet-sdk-unity` is untouched. Its untracked `docs/production-capability-review-2026-11-13.md` is referenced by name here but is not part of this branch.
- **Nothing is committed.** The working tree holds the changes. Commit only when the user asks.
- `.dotnet-install.log` is a local install log in the worktree and must be deleted before any commit.

## 7. Release gates

| # | Gate | Owner | Blocks |
|---|---|---|---|
| G1 | **Unity Editor import and compile** of `package.json`, both asmdefs, the legacy `Jiwe/` folder and the sample. Required before any packaging claim | SDK release owner | Limited pilot, public release |
| G2 | **Legacy Unity test suite** (`Tests~/UnityTests`) run in the Editor, showing the legacy path still works | SDK release owner | Limited pilot, public release |
| G3 | **Provisioned public client** from Jiwe, with exactly the four scopes and the exact redirect URI | Jiwe Developer Relations | Any live run |
| G4 | **Authenticated sign-in and XP read** with a test player on the provisioned client | SDK owner with G3 | Limited pilot |
| G5 | **Platform verification** of the browser redirect on Windows and macOS, including cancellation, a busy port, and a retry | SDK owner | Limited pilot |
| G6 | **Missing-scope and cross-account behaviour** observed live (403 and wrong-subject cases), using owned fixtures | Jiwe with SDK owner | Limited pilot |
| G7 | **Production commit re-read.** Re-read the Hermes and Abacus contracts at the commits actually deployed, rather than the local checkouts | Jiwe backend owners | Public release |
| G8 | **Legacy credential inventory and owner decisions** (section 4 of the migration guide). No rotation without separate authorisation | Jiwe security and game owners | Public release, and any policy change |
| G9 | **Sample run in the Editor**, reading XP, switching account and signing out | SDK owner | Limited pilot |
| G10 | **Independent developer onboarding test:** someone who has not seen the code follows the Quickstart start to finish | SDK release owner | Public release |

## 8. Remaining release blockers

1. **G1 is open.** The package is not verified as a Unity package until it has been imported and compiled in the Editor. Every claim about the Unity layer rests on the stub compile, which is weaker.
2. **G3 and G4 are open.** No provisioned client exists, so no authenticated journey has run in production. Nothing in this release is production-authenticated.
3. **Platform support is unproven.** Windows and macOS are the intended targets; neither has been run.
4. **Cowrie is excluded.** There is no verified public-client Cowrie contract. The OIDC route returns only `points` and `XP`, and the Abacus GraphQL balance is unverified for third parties. The precise blocker is: *the permitted public scope set has no Cowrie read; a Cowrie read needs a new, verified Abacus contract and a scope decision from Jiwe.*
5. **Rewards, purchases and config writes are excluded** by design. They belong on a trusted server.
6. **Legacy credentials remain in shipped games.** This release does not change that. Section 4 of [legacy-migration.md](legacy-migration.md) sets out the owner decisions.
7. **No version tag or package archive exists** for `v2.0.0-rc.1`.
8. **The licence is GPL-3.0.** Whether that is acceptable for third-party game developers is a decision for Jiwe, not this release.

## 9. Recommended release decision

**Human review of the branch: yes, now.** The code, the core tests and the documentation are ready to read, challenge and change.

**Limited pilot: no.** Blocked by G1 through G6 and G9.

**Public release: no.** Blocked by every gate above, and by the legacy credential decisions in G8.

---

## Appendix: What this release does not do

- It does not rotate, revoke or reissue any credential.
- It does not change any production configuration, backend limit or policy.
- It does not publish a package, create a tag or create a GitHub release.
- It does not execute any financial operation.
- It does not contact any host other than `id.jiwe.io` and `abacus.jiwe.io`, and then only for the anonymous read-only checks in section 2.
