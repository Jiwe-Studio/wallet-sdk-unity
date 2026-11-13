# Jiwe Unity SDK: Authentication design

Advanced reference for the public-client sign-in path. For getting started, see [quickstart.md](quickstart.md).

**Scope of this document:** how the modern SDK authenticates a player, how it validates every token it receives, and what it guarantees about sessions. The design is taken from the Hermes and Abacus source contracts and the live Hermes discovery document. Items marked **unverified** have not been exercised against production with a provisioned client.

---

## 1. Trust boundary

The SDK may contact exactly two services. Their endpoints are constants in `Runtime/Core/JiweConfig.cs` and cannot be overridden.

| Service | Endpoints | Used for |
|---|---|---|
| Hermes (`https://id.jiwe.io`) | `/auth`, `/token`, `/jwks` | Sign-in, token exchange, ID-token signing keys |
| Abacus (`https://abacus.jiwe.io/rest/api/v1`) | `/oidc/rewards/earned` | Earned XP (the SDK reads only the `XP` field) |

Nothing else is contacted: not Mercatus, `commerce.jiwe.io`, `pay.jiwe.io`, Hasura or Sidekick.

## 2. Client model

The SDK is a **public native client**. It holds only a client ID. It never holds:

- An Abacus API key or API secret (`X-API-KEY` / `X-API-USERNAME`).
- An OAuth client secret. The token request sends no `client_secret`.

The Hermes provisioning contract for public native clients (`db.js`) is an exact allow-list, so the registration must be:

| Field | Required value |
|---|---|
| `application_type` | `native` |
| `token_endpoint_auth_method` | `none` |
| `grant_types` | exactly `authorization_code` |
| `response_types` | exactly `code` |
| `scope` | exactly `openid`, `user-data:read`, `game-data:read`, `rewards:read` |
| `redirect_uris` | exact URIs; no wildcards, no fragment |
| `client_uri`, `user_id`, contact | required |

Because `refresh_token` is not granted to public clients, the SDK cannot refresh silently. This is why expiry means "sign in again".

## 3. Authorization Code with PKCE

`JiweAuthorization.Create` builds the authorization request:

| Parameter | Value | Why |
|---|---|---|
| `response_type` | `code` | Authorization Code flow |
| `client_id` | the public client ID | Identifies the game, not a secret |
| `redirect_uri` | the exact registered loopback URI | Stops the code being sent elsewhere |
| `scope` | `openid rewards:read` | Least privilege: only what the SDK uses |
| `state` | 32 random bytes | Ties the callback to this attempt (CSRF) |
| `nonce` | 32 random bytes | Ties the ID token to this attempt (replay) |
| `code_challenge` / `_method` | S256 of a 43-character verifier | PKCE: a stolen code is useless without the verifier |
| `resource` | `https://api.jiwe.io` | Requests the Abacus-audience access token (RFC 8707) |
| `prompt` | `login`, only for `SwitchAccountAsync` | Forces the account chooser |

Hermes treats `analytics` as the default resource. Omitting `resource` would issue an analytics-audience token that Abacus rejects. The SDK always sends it.

The verifier, state and nonce are generated with `RandomNumberGenerator`, held in memory only, and discarded when the attempt ends.

### Token request

The code is exchanged at `/token` with `grant_type=authorization_code`, the `code_verifier`, the same `redirect_uri`, the `client_id` and `resource`. No client secret is sent.

## 4. Callback validation

The browser returns to `http://127.0.0.1:<port>/callback`. `JiweAuthorization.ValidateCallback` checks, in this order:

1. **Address.** Scheme, host, port and path must equal the registered redirect URI. The SDK rebuilds the callback from the registered URI plus the received query, so a spoofed `Host` header cannot change it.
2. **State.** Must equal this attempt's state, compared in constant time. A mismatch is reported as `LoginSuperseded`, and an error parameter on that callback is not trusted.
3. **Error.** `access_denied` is a cancellation. Any other error is a failure. Only the error code is reported, not the description.
4. **Issuer (RFC 9207).** `iss` must equal `https://id.jiwe.io`. Hermes advertises `authorization_response_iss_parameter_supported`, so a missing or wrong issuer is rejected.
5. **Code.** Must be present and non-empty.

Duplicate query parameters are rejected rather than resolved. The first value is never silently preferred.

## 5. Token selection and separation

Hermes returns two tokens. They have different jobs and the SDK never interchanges them.

| Token | Audience | Used for | How it is trusted |
|---|---|---|---|
| **ID token** (`id_token`) | the client ID | Identifying the player (`sub`) | Verified against the JWKS before any claim is read |
| **Access token** | `https://api.jiwe.io` (Abacus) | Bearer for `/oidc/rewards/earned` | Accepted only from the token response; checked for `Bearer` type and lifetime |

The SDK never uses the ID token as an API bearer, and never sends the access token to Hermes.

### ID-token verification (`JiweIdTokenValidator`)

The ID token is checked before any identity is accepted:

- **Structure:** exactly three base64url parts, and no more than 16 KB.
- **Algorithm:** the header `alg` must be exactly `RS256`. `none` and HMAC downgrades are rejected.
- **Key:** `kid` must match a usable RSA key in the JWKS. Keys with `kty` other than `RSA`, `use` other than `sig`, or `alg` other than `RS256` are ignored.
- **Signature:** verified with RSA PKCS#1 v1.5 over SHA-256.
- **Claims:** `iss` equals the issuer; `aud` contains the client ID; if there are several audiences, `azp` equals the client ID; `exp` is in the future and `nbf`, if present, is in the past (one minute of clock skew allowed); `nonce` equals this attempt's nonce; `sub` is non-empty.

The public scope set does not include `profile`, so the SDK exposes only the verified `sub`. It does not expose a display name, email or phone number.

Hermes' default ID-token signing algorithm is RS256. The discovery document also advertises PS256; the SDK rejects it rather than guess. If Hermes starts issuing PS256 ID tokens for this client, the SDK will fail closed and must be updated.

## 6. Token lifetimes and expiry

- The access-token lifetime is taken from the server's `expires_in`. Nothing is hard-coded.
- Abacus audience tokens are short-lived, but the exact value is Hermes' configuration, not something this SDK sets. The Hermes source does not make it unambiguous which resource each 15-minute setting applies to, so the SDK does not assume one. The SDK applies a **60-second safety margin**, so a token is never used at its edge.
- When the session is expired, the next read throws `SessionExpired`, clears the local session and raises `StatusChanged`.
- A `401` from Abacus also clears the local session, because the server has rejected the token.

There is no silent refresh. The developer decides when to prompt the player again.

## 7. Session lifecycle

`JiweSessionCore` is a state machine with three states and a **generation counter**.

| Transition | Effect |
|---|---|
| `SignInAsync` | Generation advances, status becomes `SigningIn`. Earlier in-flight work is obsolete. |
| Successful sign-in | Commits the new session only if the attempt is still current. |
| Failed or cancelled sign-in | Restores the previous session if its token is still valid. A failed re-login does not sign the player out. |
| `SwitchAccountAsync` | Clears the session, then signs in with `prompt=login`. |
| `SignOutAsync` | Cancels any attempt, advances the generation, clears the session. Local only. |
| Expiry, or `401` | Clears the session and advances the generation. |

Every reader captures the generation before it awaits, and checks it again after the response. A response that arrives under a different account, or after sign-out, is discarded with `SessionChanged`. A callback from an earlier attempt is rejected with `LoginSuperseded`.

Only one sign-in can wait on the browser at a time. A new attempt cancels the previous one's waiting. `StatusChanged` is raised on every change, including expiry, so a UI does not show stale state.

The session is held in memory only. Nothing is written to disk, so a restart always requires sign-in.

## 8. Sign-out and provider logout

`SignOutAsync` is **local only**. It:

- clears the access token and the player identity from memory;
- cancels a waiting browser sign-in;
- makes all outstanding reads obsolete.

It does **not** call Hermes. Hermes advertises an `end_session_endpoint` and a `revocation_endpoint`, but the SDK does not use them, because:

- the public-client registration contract was not exercised against production with a provisioned client, so revocation for this client type is **unverified**; and
- ending the browser session could sign out the player from other Jiwe games unexpectedly.

So after `SignOutAsync`, the browser's own Jiwe session may still exist. A later `SignInAsync` can therefore complete without the player typing a password. `SwitchAccountAsync` is the way to force the account chooser.

Do not describe `SignOutAsync` to players as "signing out of Jiwe". Describe it as "signing out of this game on this device."

## 9. Cancellation

Every async call accepts a `CancellationToken`. Cancellation during sign-in:

- stops the loopback listener and releases the port (the listener is always disposed, on every path, so a retry on the same port works);
- raises `LoginCancelled`;
- leaves a previous valid session in place.

`JiweSdk.Shutdown()` cancels every attempt through a lifetime token and clears the session. The sample calls it from `OnDestroy`.

Sign-in times out after **5 minutes** with a `Timeout` error.

## 10. Application pause and resume

On Standalone desktop the browser runs outside the game, so no special handling is needed for pause. The 5-minute timeout bounds how long a sign-in can wait. **This behaviour is unverified on a real device**, because no Unity Editor run was possible for this release.

Android and iOS redirects are not supported, so no deep-link lifecycle is handled.

## 11. Platform support

| Platform | Redirect mechanism | Status |
|---|---|---|
| Windows standalone | Loopback `http://127.0.0.1:<port>/callback` | Code written; **not verified** |
| macOS standalone | Loopback `http://127.0.0.1:<port>/callback` | Code written; **not verified** |
| Unity Editor | Loopback | Code written; **not verified** |
| Android, iOS | Custom scheme | **Unsupported**; rejected by `JiweConfig` |
| WebGL | Same-tab redirect | **Unsupported** |

Loopback redirects on Windows may need the port's firewall rule or URL ACL to be permitted. This is unverified.

## 12. What is not guaranteed

- **Silent re-authentication.** Not available for public clients.
- **Provider-side logout.** Not performed. See section 8.
- **Protection against a compromised device.** A player with control of the device can read the in-memory session. The SDK limits how long a token is valid and what it can read; it cannot prevent device-level access.
- **Identity beyond `sub`.** Not exposed in this release.
- **Session persistence across restarts.** Deliberately not implemented.
