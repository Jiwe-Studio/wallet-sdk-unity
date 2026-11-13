using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

// Dependency-free test runner for the engine-free SDK core. Exit code 0 only when every check passes.
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private const string ClientId = "unity-client-1234";
    private const string Redirect = "http://127.0.0.1:51772/callback";
    private const string Nonce = "nonce-value-abc";

    private static int Main()
    {
        // PKCE (RFC 7636)
        Test("PKCE: RFC 7636 Appendix B challenge vector", () =>
        {
            Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                JiwePkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        });
        Test("PKCE: generated verifier is 43 unreserved characters and unique", () =>
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < 200; i++)
            {
                var v = JiwePkce.CreateVerifier();
                True(v.Length == 43, "length " + v.Length);
                True(JiwePkce.IsValidVerifier(v), "invalid " + v);
                True(seen.Add(v), "duplicate verifier");
            }
        });
        Test("PKCE: challenge rejects a too-short verifier", () =>
        {
            Throws<ArgumentException>(() => JiwePkce.CreateChallenge("short"));
        });
        Test("PKCE: verifier with reserved characters is invalid", () =>
        {
            True(!JiwePkce.IsValidVerifier(new string('a', 42) + "+"), "plus accepted");
            True(!JiwePkce.IsValidVerifier(new string('a', 42) + "/"), "slash accepted");
        });
        Test("PKCE: state and nonce are unpadded base64url", () =>
        {
            foreach (var s in new[] { JiwePkce.CreateState(), JiwePkce.CreateNonce() })
            {
                True(!s.Contains("="), "padding present");
                True(!s.Contains("+") && !s.Contains("/"), "non-url characters");
            }
        });

        // Configuration
        Test("Config: default loopback redirect and clean client id are valid", () =>
        {
            Equal<string>(null, new JiweConfig { ClientId = ClientId }.Validate());
        });
        Test("Config: empty, whitespace and oversized client ids are rejected", () =>
        {
            True(new JiweConfig { ClientId = "" }.Validate() != null, "empty accepted");
            True(new JiweConfig { ClientId = null }.Validate() != null, "null accepted");
            True(new JiweConfig { ClientId = "has space" }.Validate() != null, "space accepted");
            True(new JiweConfig { ClientId = new string('a', 129) }.Validate() != null, "oversized accepted");
        });
        Test("Config: redirect must be exact loopback http with a port and path", () =>
        {
            True(JiweConfig.ValidateRedirectUri("https://127.0.0.1:51772/callback") != null, "https accepted");
            True(JiweConfig.ValidateRedirectUri("http://localhost:51772/callback") != null, "localhost accepted");
            True(JiweConfig.ValidateRedirectUri("http://127.0.0.1:51772/*") != null, "wildcard accepted");
            True(JiweConfig.ValidateRedirectUri("http://127.0.0.1:51772/cb#frag") != null, "fragment accepted");
            True(JiweConfig.ValidateRedirectUri("http://127.0.0.1:51772/") != null, "root path accepted");
            True(JiweConfig.ValidateRedirectUri("http://127.0.0.1/callback") != null, "missing port accepted");
            True(JiweConfig.ValidateRedirectUri("com.example.app:/callback") != null, "custom scheme accepted on Standalone");
            True(JiweConfig.ValidateRedirectUri(Redirect) == null, "default rejected");
        });
        Test("Config: a redirect URI carrying a query is rejected", () =>
        {
            True(JiweConfig.ValidateRedirectUri("http://127.0.0.1:5/cb?x=1") != null, "query accepted");
        });
        Test("Callback: IsCallbackForState matches only the attempt's state and never throws", () =>
        {
            True(JiweAuthorization.IsCallbackForState("?code=c&state=S1", "S1"), "match rejected");
            True(!JiweAuthorization.IsCallbackForState("?code=c&state=S2", "S1"), "mismatch accepted");
            True(!JiweAuthorization.IsCallbackForState("?state=S1&state=S1", "S1"), "duplicate accepted");
            True(!JiweAuthorization.IsCallbackForState(null, "S1"), "null query accepted");
        });
        Test("Config: endpoints are the two permitted hosts only", () =>
        {
            True(JiweEndpoints.Authorization.StartsWith("https://id.jiwe.io/", StringComparison.Ordinal), "auth host");
            True(JiweEndpoints.Token.StartsWith("https://id.jiwe.io/", StringComparison.Ordinal), "token host");
            True(JiweEndpoints.Jwks.StartsWith("https://id.jiwe.io/", StringComparison.Ordinal), "jwks host");
            True(JiweEndpoints.AbacusApi.StartsWith("https://abacus.jiwe.io/", StringComparison.Ordinal), "abacus host");
            Equal("https://api.jiwe.io", JiweEndpoints.ResourceIndicator);
        });

        // Authorization request and callback
        Test("Authorize: URL carries PKCE S256, resource, least-privilege scope, no secret", () =>
        {
            var req = JiweAuthorization.Create(ClientId, Redirect, forceAccountChooser: false);
            True(req.Url.StartsWith("https://id.jiwe.io/auth?", StringComparison.Ordinal), "endpoint");
            True(req.Url.Contains("code_challenge_method=S256"), "not S256");
            True(req.Url.Contains("resource=https%3A%2F%2Fapi.jiwe.io"), "resource missing");
            True(req.Url.Contains("scope=openid%20rewards%3Aread"), "scope wrong: " + req.Url);
            True(!req.Url.Contains("client_secret"), "secret present");
            True(!req.Url.Contains("prompt="), "unexpected prompt");
            True(req.Url.Contains("state=" + Uri.EscapeDataString(req.State)), "state missing");
            True(req.Url.Contains("nonce=" + Uri.EscapeDataString(req.Nonce)), "nonce missing");
        });
        Test("Authorize: challenge in the URL is derived from the retained verifier", () =>
        {
            var req = JiweAuthorization.Create(ClientId, Redirect, false);
            True(req.Url.Contains("code_challenge=" + JiwePkce.CreateChallenge(req.CodeVerifier)), "challenge mismatch");
        });
        Test("Authorize: account switching adds prompt=login", () =>
        {
            var req = JiweAuthorization.Create(ClientId, Redirect, forceAccountChooser: true);
            True(req.Url.Contains("prompt=login"), "prompt missing");
        });
        Test("Token body: authorization_code with verifier and resource, no client secret", () =>
        {
            var body = JiweAuthorization.BuildTokenRequestBody(ClientId, "code-1", Redirect, "verifier-1");
            True(body.StartsWith("grant_type=authorization_code&", StringComparison.Ordinal), "grant");
            True(body.Contains("code_verifier=verifier-1"), "verifier");
            True(body.Contains("resource=https%3A%2F%2Fapi.jiwe.io"), "resource");
            True(!body.Contains("client_secret"), "secret present");
        });

        Test("Callback: valid callback yields the code", () =>
        {
            Equal("code-abc", JiweAuthorization.ValidateCallback(Callback("code=code-abc&state=S1&iss=" + Iss()), Redirect, "S1"));
        });
        Test("Callback: state mismatch is superseded, not trusted", () =>
        {
            Throws(JiweErrorKind.LoginSuperseded, () => JiweAuthorization.ValidateCallback(Callback("code=c&state=OTHER&iss=" + Iss()), Redirect, "S1"));
        });
        Test("Callback: missing state is superseded", () =>
        {
            Throws(JiweErrorKind.LoginSuperseded, () => JiweAuthorization.ValidateCallback(Callback("code=c&iss=" + Iss()), Redirect, "S1"));
        });
        Test("Callback: error with matching state but access_denied is a cancellation", () =>
        {
            Throws(JiweErrorKind.LoginCancelled, () => JiweAuthorization.ValidateCallback(Callback("error=access_denied&state=S1&iss=" + Iss()), Redirect, "S1"));
        });
        Test("Callback: other error codes are a failure", () =>
        {
            Throws(JiweErrorKind.LoginFailed, () => JiweAuthorization.ValidateCallback(Callback("error=server_error&state=S1&iss=" + Iss()), Redirect, "S1"));
        });
        Test("Callback: wrong path, host or port is rejected", () =>
        {
            Throws(JiweErrorKind.LoginFailed, () => JiweAuthorization.ValidateCallback("http://127.0.0.1:51772/other?code=c&state=S1&iss=" + Iss(), Redirect, "S1"));
            Throws(JiweErrorKind.LoginFailed, () => JiweAuthorization.ValidateCallback("http://localhost:51772/callback?code=c&state=S1&iss=" + Iss(), Redirect, "S1"));
            Throws(JiweErrorKind.LoginFailed, () => JiweAuthorization.ValidateCallback("http://127.0.0.1:9999/callback?code=c&state=S1&iss=" + Iss(), Redirect, "S1"));
        });
        Test("Callback: missing or wrong issuer is rejected (RFC 9207)", () =>
        {
            Throws(JiweErrorKind.LoginFailed, () => JiweAuthorization.ValidateCallback(Callback("code=c&state=S1"), Redirect, "S1"));
            Throws(JiweErrorKind.LoginFailed, () => JiweAuthorization.ValidateCallback(Callback("code=c&state=S1&iss=https%3A%2F%2Fevil.example"), Redirect, "S1"));
        });
        Test("Callback: missing code is rejected", () =>
        {
            Throws(JiweErrorKind.LoginFailed, () => JiweAuthorization.ValidateCallback(Callback("state=S1&iss=" + Iss()), Redirect, "S1"));
        });
        Test("Callback: duplicated parameter is rejected rather than resolved", () =>
        {
            Throws(JiweErrorKind.LoginFailed, () => JiweAuthorization.ValidateCallback(Callback("code=a&code=b&state=S1&iss=" + Iss()), Redirect, "S1"));
        });
        Test("Callback: constant-time comparison distinguishes unequal strings", () =>
        {
            True(JiweAuthorization.ConstantTimeEquals("abc", "abc"), "equal");
            True(!JiweAuthorization.ConstantTimeEquals("abc", "abd"), "differs");
            True(!JiweAuthorization.ConstantTimeEquals("abc", "ab"), "length");
            True(!JiweAuthorization.ConstantTimeEquals(null, "a"), "null");
        });

        // Token response
        Test("Token: valid bearer response parses", () =>
        {
            var t = JiweTokenResponse.Parse("{\"access_token\":\"at\",\"id_token\":\"it\",\"token_type\":\"Bearer\",\"expires_in\":900,\"scope\":\"openid rewards:read\"}");
            Equal("at", t.AccessToken);
            Equal("it", t.IdToken);
            Equal(900, t.ExpiresInSeconds);
        });
        Test("Token: missing access token is an unexpected response", () =>
        {
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweTokenResponse.Parse("{\"id_token\":\"it\",\"token_type\":\"Bearer\",\"expires_in\":900}"));
        });
        Test("Token: missing id token is an unexpected response", () =>
        {
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweTokenResponse.Parse("{\"access_token\":\"at\",\"token_type\":\"Bearer\",\"expires_in\":900}"));
        });
        Test("Token: non-Bearer token type is rejected", () =>
        {
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweTokenResponse.Parse("{\"access_token\":\"a\",\"id_token\":\"i\",\"token_type\":\"MAC\",\"expires_in\":900}"));
        });
        Test("Token: expires_in must be a positive integer in range", () =>
        {
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweTokenResponse.Parse("{\"access_token\":\"a\",\"id_token\":\"i\",\"token_type\":\"Bearer\",\"expires_in\":\"900\"}"));
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweTokenResponse.Parse("{\"access_token\":\"a\",\"id_token\":\"i\",\"token_type\":\"Bearer\",\"expires_in\":0}"));
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweTokenResponse.Parse("{\"access_token\":\"a\",\"id_token\":\"i\",\"token_type\":\"Bearer\",\"expires_in\":999999}"));
        });
        Test("Token: non-JSON body is an unexpected response", () =>
        {
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweTokenResponse.Parse("<html>gateway</html>"));
        });

        // ID token: real RS256 signatures against a generated key
        var rsa = RSA.Create(2048);
        var otherRsa = RSA.Create(2048);
        var jwks = JiweJwks.Parse(JwksFor(rsa, "k1", extra: true));
        var now = DateTime.UtcNow;

        Test("ID token: valid RS256 token yields the verified subject", () =>
        {
            var id = Validate(Jwt(rsa, "k1", Claims()), jwks);
            Equal("player-123", id.Subject);
        });
        Test("ID token: tampered payload fails the signature", () =>
        {
            var token = Jwt(rsa, "k1", Claims());
            var parts = token.Split('.');
            var forged = Base64UrlJson(new JObject { ["sub"] = "attacker", ["iss"] = JiweEndpoints.Issuer, ["aud"] = ClientId, ["exp"] = Exp(), ["nonce"] = Nonce });
            Throws(JiweErrorKind.LoginFailed, () => Validate(parts[0] + "." + forged + "." + parts[2], jwks));
        });
        Test("ID token: signature from another key is rejected", () =>
        {
            Throws(JiweErrorKind.LoginFailed, () => Validate(Jwt(otherRsa, "k1", Claims()), jwks));
        });
        Test("ID token: alg none is rejected", () =>
        {
            var h = Base64UrlJson(new JObject { ["alg"] = "none", ["kid"] = "k1" });
            var p = Base64UrlJson(Claims());
            Throws(JiweErrorKind.LoginFailed, () => Validate(h + "." + p + ".", jwks));
        });
        Test("ID token: HS256 downgrade is rejected", () =>
        {
            var h = Base64UrlJson(new JObject { ["alg"] = "HS256", ["kid"] = "k1" });
            var p = Base64UrlJson(Claims());
            Throws(JiweErrorKind.LoginFailed, () => Validate(h + "." + p + ".c2ln", jwks));
        });
        Test("ID token: unknown kid is rejected", () =>
        {
            Throws(JiweErrorKind.LoginFailed, () => Validate(Jwt(rsa, "unknown", Claims()), jwks));
        });
        Test("ID token: wrong issuer is rejected", () =>
        {
            var c = Claims(); c["iss"] = "https://evil.example";
            Throws(JiweErrorKind.LoginFailed, () => Validate(Jwt(rsa, "k1", c), jwks));
        });
        Test("ID token: wrong audience is rejected (another game's token)", () =>
        {
            var c = Claims(); c["aud"] = "some-other-game";
            Throws(JiweErrorKind.LoginFailed, () => Validate(Jwt(rsa, "k1", c), jwks));
        });
        Test("ID token: multiple audiences require azp equal to this client", () =>
        {
            var c = Claims(); c["aud"] = new JArray(ClientId, "other");
            Throws(JiweErrorKind.LoginFailed, () => Validate(Jwt(rsa, "k1", c), jwks));
            c["azp"] = ClientId;
            Equal("player-123", Validate(Jwt(rsa, "k1", c), jwks).Subject);
        });
        Test("ID token: expired token is rejected", () =>
        {
            var c = Claims(); c["exp"] = new DateTimeOffset(now.AddHours(-1)).ToUnixTimeSeconds();
            Throws(JiweErrorKind.LoginFailed, () => Validate(Jwt(rsa, "k1", c), jwks));
        });
        Test("ID token: token not yet valid (nbf in the future) is rejected", () =>
        {
            var c = Claims(); c["nbf"] = new DateTimeOffset(now.AddHours(1)).ToUnixTimeSeconds();
            Throws(JiweErrorKind.LoginFailed, () => Validate(Jwt(rsa, "k1", c), jwks));
        });
        Test("ID token: nonce must match this attempt", () =>
        {
            var c = Claims(); c["nonce"] = "replayed-nonce";
            Throws(JiweErrorKind.LoginFailed, () => Validate(Jwt(rsa, "k1", c), jwks));
            var missing = Claims(); missing.Remove("nonce");
            Throws(JiweErrorKind.LoginFailed, () => Validate(Jwt(rsa, "k1", missing), jwks));
        });
        Test("ID token: missing subject is rejected", () =>
        {
            var c = Claims(); c.Remove("sub");
            Throws(JiweErrorKind.LoginFailed, () => Validate(Jwt(rsa, "k1", c), jwks));
        });
        Test("ID token: oversized or non-three-part input is rejected", () =>
        {
            Throws(JiweErrorKind.LoginFailed, () => Validate(new string('a', JiweIdTokenValidator.MaxTokenLength + 1), jwks));
            Throws(JiweErrorKind.LoginFailed, () => Validate("a.b", jwks));
        });
        Test("JWKS: non-RSA, encryption-use and non-RS256 keys are ignored", () =>
        {
            // The EC and enc-use entries in JwksFor are not trusted: a token signed under their kid fails.
            Throws(JiweErrorKind.LoginFailed, () => Validate(Jwt(rsa, "ec-key", Claims()), jwks));
            Throws(JiweErrorKind.LoginFailed, () => Validate(Jwt(rsa, "enc-key", Claims()), jwks));
        });
        Test("JWKS: live-shaped key (use=sig, no alg field) is accepted", () =>
        {
            // Shape observed on https://id.jiwe.io/jwks on 2026-11-13: RSA, use=sig, kid, n, e, and no alg.
            var p = rsa.ExportParameters(false);
            var live = new JObject { ["keys"] = new JArray(new JObject
            {
                ["kty"] = "RSA", ["use"] = "sig", ["kid"] = "live-kid", ["e"] = B64(p.Exponent), ["n"] = B64(p.Modulus),
            }) }.ToString(Newtonsoft.Json.Formatting.None);
            Equal(1, JiweJwks.Parse(live).KeyCount);
            Equal("player-123", Validate(Jwt(rsa, "live-kid", Claims()), JiweJwks.Parse(live)).Subject);
        });
        Test("JWKS: no usable RSA keys is an unexpected response", () =>
        {
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweJwks.Parse("{\"keys\":[{\"kty\":\"EC\",\"kid\":\"e\",\"crv\":\"P-256\"}]}"));
        });
        Test("JWKS: non-JSON and missing keys array are unexpected responses", () =>
        {
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweJwks.Parse("not json"));
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweJwks.Parse("{}"));
        });

        // Earned rewards
        Test("Rewards: valid XP parses with exact decimal precision", () =>
        {
            Equal(123.45m, JiweRewardsParser.ParseEarnedXp("{\"XP\":123.45}"));
        });
        Test("Rewards: zero XP is a real value", () =>
        {
            Equal(0m, JiweRewardsParser.ParseEarnedXp("{\"XP\":0}"));
        });
        Test("Rewards: points are ignored and never exposed", () =>
        {
            Equal(70m, JiweRewardsParser.ParseEarnedXp("{\"points\":250,\"XP\":70,\"airtimeEarned\":5}"));
        });
        Test("Rewards: missing XP is a schema error, never zero, even when points are present", () =>
        {
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweRewardsParser.ParseEarnedXp("{\"points\":10}"));
        });
        Test("Rewards: string, null and negative XP are rejected", () =>
        {
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweRewardsParser.ParseEarnedXp("{\"XP\":\"70\"}"));
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweRewardsParser.ParseEarnedXp("{\"XP\":null}"));
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweRewardsParser.ParseEarnedXp("{\"XP\":-5}"));
        });
        Test("Rewards: lowercase xp is not accepted as XP", () =>
        {
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweRewardsParser.ParseEarnedXp("{\"xp\":5}"));
        });
        Test("Rewards: non-JSON is an unexpected response", () =>
        {
            Throws(JiweErrorKind.UnexpectedResponse, () => JiweRewardsParser.ParseEarnedXp("oops"));
        });

        // HTTP mapping
        Test("HTTP: 2xx maps to no error", () =>
        {
            True(JiweHttpStatus.ToException(200, null) == null, "200");
            True(JiweHttpStatus.ToException(204, null) == null, "204");
        });
        Test("HTTP: status codes map to distinct, recoverable kinds", () =>
        {
            Equal(JiweErrorKind.Network, JiweHttpStatus.ToException(0, null).Kind);
            Equal(JiweErrorKind.InvalidRequest, JiweHttpStatus.ToException(400, null).Kind);
            Equal(JiweErrorKind.Unauthorized, JiweHttpStatus.ToException(401, null).Kind);
            Equal(JiweErrorKind.InsufficientScope, JiweHttpStatus.ToException(403, null).Kind);
            Equal(JiweErrorKind.ServiceUnavailable, JiweHttpStatus.ToException(503, null).Kind);
            Equal(JiweErrorKind.UnexpectedResponse, JiweHttpStatus.ToException(418, null).Kind);
        });
        Test("HTTP: 429 carries Retry-After seconds; HTTP-date is ignored", () =>
        {
            var e = JiweHttpStatus.ToException(429, "30");
            Equal(JiweErrorKind.RateLimited, e.Kind);
            Equal(30.0, e.RetryAfter.Value.TotalSeconds);
            True(JiweHttpStatus.ToException(429, "Wed, 21 Oct 2026 07:28:00 GMT").RetryAfter == null, "date parsed");
        });

        // Session state
        Test("Session: begin then complete makes the player signed in", () =>
        {
            var s = new JiweSessionCore();
            var g = s.BeginSignIn();
            True(s.CompleteSignIn(g, Snapshot("p1", 900)), "complete refused");
            Equal(JiweSessionStatus.SignedIn, s.Status);
            Equal("p1", s.Current.Subject);
        });
        Test("Session: obsolete attempt cannot commit after sign-out", () =>
        {
            var s = new JiweSessionCore();
            var g = s.BeginSignIn();
            s.SignOut();
            True(!s.CompleteSignIn(g, Snapshot("stale", 900)), "stale committed");
            Equal(JiweSessionStatus.SignedOut, s.Status);
            True(s.Current == null, "stale session visible");
        });
        Test("Session: failed re-login restores a still-valid session", () =>
        {
            var s = new JiweSessionCore();
            s.CompleteSignIn(s.BeginSignIn(), Snapshot("p1", 900));
            var g = s.BeginSignIn();
            True(s.AbortSignIn(g), "abort refused");
            Equal(JiweSessionStatus.SignedIn, s.Status);
            Equal("p1", s.Current.Subject);
        });
        Test("Session: failed sign-in with an expired prior session clears it", () =>
        {
            var s = new JiweSessionCore();
            s.CompleteSignIn(s.BeginSignIn(), Snapshot("p1", -10));
            var g = s.BeginSignIn();
            s.AbortSignIn(g);
            Equal(JiweSessionStatus.SignedOut, s.Status);
        });
        Test("Session: account switch makes earlier account reads obsolete", () =>
        {
            var s = new JiweSessionCore();
            s.CompleteSignIn(s.BeginSignIn(), Snapshot("alice", 900));
            var readGeneration = s.Generation;
            s.SignOut();
            s.CompleteSignIn(s.BeginSignIn(), Snapshot("bob", 900));
            True(!s.IsCurrent(readGeneration), "alice's read still current");
            Equal("bob", s.Current.Subject);
        });
        Test("Session: reads require a signed-in, unexpired session", () =>
        {
            var s = new JiweSessionCore();
            Throws(JiweErrorKind.NotSignedIn, () => s.RequireUsable(DateTime.UtcNow));
            s.CompleteSignIn(s.BeginSignIn(), Snapshot("p1", -10));
            Throws(JiweErrorKind.SessionExpired, () => s.RequireUsable(DateTime.UtcNow));
            Equal(JiweSessionStatus.SignedOut, s.Status);
        });
        Test("Session: status changes are announced for sign-in, sign-out and expiry", () =>
        {
            var s = new JiweSessionCore();
            int events = 0;
            s.StatusChanged += () => events++;
            s.CompleteSignIn(s.BeginSignIn(), Snapshot("p1", 900)); // 2 transitions
            s.SignOut();                                              // 1
            s.CompleteSignIn(s.BeginSignIn(), Snapshot("p2", -10));   // 2
            try { s.RequireUsable(DateTime.UtcNow); } catch (JiweException) { }  // 1 (expiry)
            Equal(6, events);
        });
        Test("Session: status reports SignedOut once the token has lapsed, without a read", () =>
        {
            var s = new JiweSessionCore();
            int events = 0;
            s.StatusChanged += () => events++;
            s.CompleteSignIn(s.BeginSignIn(), Snapshot("p1", -10));
            Equal(JiweSessionStatus.SignedOut, s.Status);
            True(s.Current == null, "expired session still exposed");
            True(events >= 3, "expiry not announced");
        });
        Test("Session: a throwing StatusChanged subscriber does not break sign-in", () =>
        {
            var s = new JiweSessionCore();
            s.StatusChanged += () => throw new InvalidOperationException("boom");
            var g = s.BeginSignIn();
            True(s.CompleteSignIn(g, Snapshot("p1", 900)), "commit refused");
            Equal(JiweSessionStatus.SignedIn, s.Status);
        });
        Test("Session: sign-out invalidates an in-flight attempt generation", () =>
        {
            var s = new JiweSessionCore();
            var g = s.BeginSignIn();
            s.SignOut();
            True(!s.IsCurrent(g), "attempt still current");
        });

        // Errors
        Test("Errors: every kind has a non-empty recovery message", () =>
        {
            foreach (JiweErrorKind kind in Enum.GetValues(typeof(JiweErrorKind)))
            {
                var e = new JiweException(kind, "m");
                True(!string.IsNullOrEmpty(e.Recovery), "no recovery for " + kind);
            }
        });

        // Loopback listener against the real HttpListener
        Test("Loopback: delivers the callback, answers other paths with 404, releases the port", () =>
            LoopbackDelivers().GetAwaiter().GetResult());
        Test("Loopback: timeout raises Timeout and frees the port for a retry", () =>
            LoopbackTimeoutFreesPort().GetAwaiter().GetResult());
        Test("Loopback: cancellation raises OperationCanceled and frees the port", () =>
            LoopbackCancelFreesPort().GetAwaiter().GetResult());
        Test("Loopback: a wrong-state request gets 400 and does not end the attempt", () =>
            LoopbackIgnoresWrongState().GetAwaiter().GetResult());
        Test("Loopback: a busy redirect port is reported as LoginFailed", () =>
            LoopbackBusyPort().GetAwaiter().GetResult());

        Console.WriteLine();
        Console.WriteLine("Passed: " + _passed + "  Failed: " + _failed);
        return _failed == 0 ? 0 : 1;
    }

    // ---- loopback (real HttpListener) ---------------------------------------------------

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task LoopbackDelivers()
    {
        int port = FreePort();
        var redirect = "http://127.0.0.1:" + port + "/callback";
        using (var listener = JiweLoopbackListener.Start(redirect))
        {
            var waiting = listener.WaitForCallbackAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "s");
            using (var http = new HttpClient())
            {
                var wrong = await http.GetAsync("http://127.0.0.1:" + port + "/other?code=x");
                True(wrong.StatusCode == HttpStatusCode.NotFound, "wrong path not 404: " + wrong.StatusCode);

                var ok = await http.GetAsync("http://127.0.0.1:" + port + "/callback?code=abc&state=s");
                True(ok.StatusCode == HttpStatusCode.OK, "callback not 200: " + ok.StatusCode);
                var page = await ok.Content.ReadAsStringAsync();
                True(page.Contains("Return to the game"), "neutral page missing");
                True(!page.Contains("complete"), "page claims success before validation");
            }
            var received = await waiting;
            Equal(redirect + "?code=abc&state=s", received);
        }
        using (JiweLoopbackListener.Start(redirect)) { }  // port reusable after dispose
    }

    private static async Task LoopbackIgnoresWrongState()
    {
        int port = FreePort();
        var redirect = "http://127.0.0.1:" + port + "/callback";
        using (var listener = JiweLoopbackListener.Start(redirect))
        {
            var waiting = listener.WaitForCallbackAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "good-state");
            using (var http = new HttpClient())
            {
                var stray = await http.GetAsync("http://127.0.0.1:" + port + "/callback?code=x&state=forged");
                True(stray.StatusCode == HttpStatusCode.BadRequest, "stray not 400: " + stray.StatusCode);
                var real = await http.GetAsync("http://127.0.0.1:" + port + "/callback?code=abc&state=good-state");
                True(real.StatusCode == HttpStatusCode.OK, "real not 200");
            }
            Equal(redirect + "?code=abc&state=good-state", await waiting);
        }
    }

    private static async Task LoopbackTimeoutFreesPort()
    {
        int port = FreePort();
        var redirect = "http://127.0.0.1:" + port + "/callback";
        using (var listener = JiweLoopbackListener.Start(redirect))
        {
            try
            {
                await listener.WaitForCallbackAsync(CancellationToken.None, TimeSpan.FromMilliseconds(300), "s");
                throw new Exception("no timeout raised");
            }
            catch (JiweException e)
            {
                Equal(JiweErrorKind.Timeout, e.Kind);
            }
        }
        using (JiweLoopbackListener.Start(redirect)) { }
    }

    private static async Task LoopbackCancelFreesPort()
    {
        int port = FreePort();
        var redirect = "http://127.0.0.1:" + port + "/callback";
        using (var listener = JiweLoopbackListener.Start(redirect))
        using (var cts = new CancellationTokenSource(200))
        {
            try
            {
                await listener.WaitForCallbackAsync(cts.Token, TimeSpan.FromSeconds(30), "s");
                throw new Exception("no cancellation raised");
            }
            catch (OperationCanceledException)
            {
            }
        }
        using (JiweLoopbackListener.Start(redirect)) { }
    }

    private static Task LoopbackBusyPort()
    {
        int port = FreePort();
        var redirect = "http://127.0.0.1:" + port + "/callback";
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        try
        {
            Throws(JiweErrorKind.LoginFailed, () => JiweLoopbackListener.Start(redirect).Dispose());
        }
        finally
        {
            blocker.Stop();
        }
        return Task.CompletedTask;
    }

    // ---- helpers -------------------------------------------------------------------------

    private static void Test(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception e)
        {
            _failed++;
            Console.WriteLine("FAIL " + name + " :: " + e.Message);
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception("expected [" + expected + "] but was [" + actual + "]");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception("assertion failed: " + message);
    }

    private static void Throws(JiweErrorKind kind, Action action)
    {
        try
        {
            action();
        }
        catch (JiweException e)
        {
            if (e.Kind != kind)
                throw new Exception("expected " + kind + " but was " + e.Kind + " (" + e.Message + ")");
            return;
        }
        throw new Exception("expected JiweException " + kind + " but nothing was thrown");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("expected " + typeof(T).Name);
    }

    private static string Callback(string query) => Redirect + "?" + query;

    private static string Iss() => Uri.EscapeDataString(JiweEndpoints.Issuer);

    private static long Exp() => new DateTimeOffset(DateTime.UtcNow.AddMinutes(10)).ToUnixTimeSeconds();

    private static JObject Claims() => new JObject
    {
        ["iss"] = JiweEndpoints.Issuer,
        ["aud"] = ClientId,
        ["sub"] = "player-123",
        ["exp"] = Exp(),
        ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["nonce"] = Nonce,
    };

    private static JiweSessionSnapshot Snapshot(string subject, int lifetimeSeconds) =>
        new JiweSessionSnapshot(subject, "token", DateTime.UtcNow.AddSeconds(lifetimeSeconds));

    private static JiweIdentity Validate(string token, JiweJwks keys) =>
        JiweIdTokenValidator.Validate(token, keys, JiweEndpoints.Issuer, ClientId, Nonce, DateTime.UtcNow, TimeSpan.FromMinutes(1));

    private static string B64(byte[] bytes) => JiwePkce.Base64UrlEncode(bytes);

    private static string Base64UrlJson(JObject obj) => B64(Encoding.UTF8.GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None)));

    private static string Jwt(RSA key, string kid, JObject payload)
    {
        var header = Base64UrlJson(new JObject { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = kid });
        var body = Base64UrlJson(payload);
        var input = Encoding.ASCII.GetBytes(header + "." + body);
        var signature = key.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + body + "." + B64(signature);
    }

    private static string JwksFor(RSA key, string kid, bool extra)
    {
        var p = key.ExportParameters(false);
        var keys = new JArray(new JObject
        {
            ["kty"] = "RSA", ["use"] = "sig", ["alg"] = "RS256", ["kid"] = kid,
            ["n"] = B64(p.Modulus), ["e"] = B64(p.Exponent),
        });
        if (extra)
        {
            keys.Add(new JObject { ["kty"] = "EC", ["kid"] = "ec-key", ["crv"] = "P-256", ["x"] = "x", ["y"] = "y" });
            keys.Add(new JObject { ["kty"] = "RSA", ["use"] = "enc", ["kid"] = "enc-key", ["n"] = B64(p.Modulus), ["e"] = B64(p.Exponent) });
        }
        return new JObject { ["keys"] = keys }.ToString(Newtonsoft.Json.Formatting.None);
    }
}
