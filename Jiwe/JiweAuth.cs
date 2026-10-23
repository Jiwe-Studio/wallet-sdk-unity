using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Jiwe
{
    /// <summary>
    /// Player login for the Jiwe Wallet (OAuth2 Authorization Code + PKCE).
    /// This produces the player token JiweWallet sends as X-API-TOKEN on
    /// reward/purchase calls — it does NOT carry the X-API-USERNAME/X-API-KEY
    /// wallet credentials, since those are static per-app values used on every
    /// wallet call (including ones that don't need a logged-in player, like
    /// the leaderboard) — see JiweWallet for those.
    ///
    /// Endpoints default to production (id.jiwe.io) and don't need to be typed
    /// into the Inspector; override them only for a non-production environment.
    /// Only your OAuth client credentials (from your Jiwe profile > Apps) are
    /// required:
    ///   - clientId, and apiSecret if your app is a confidential client (how
    ///     it's presented to the token endpoint is controlled by clientAuth)
    ///
    /// The redirect flow is different per platform (this is the one part of
    /// login that genuinely can't be unified — each platform requires a
    /// different way to get the browser's result back into the app):
    ///   - Standalone/Editor: a local loopback HTTP listener catches the
    ///     redirect after a system-browser login, same approach as before,
    ///     just without the Windows-only console-focus hack.
    ///   - Android/iOS: the system browser redirects to a custom URI scheme
    ///     (mobileRedirectScheme, e.g. "kentepunk://oauth-callback") which
    ///     reopens the app; requires registering that redirect_uri with Jiwe.
    ///   - WebGL: there's no local server or deep link in a browser, so the
    ///     Jiwe login page redirects back to the SAME hosted page with
    ///     ?code=... in the URL; the game reloads and resumes from there.
    ///     Your Jiwe app's token endpoint must allow CORS from your hosted
    ///     domain for the in-browser token exchange to succeed.
    /// </summary>
    public class JiweAuth : MonoBehaviour
    {
        private const string DefaultAuthEndpoint = "https://id.jiwe.io/auth";
        private const string DefaultTokenEndpoint = "https://id.jiwe.io/token";
        private const string DefaultUserInfoEndpoint = "https://id.jiwe.io/me";
        private string AuthEndpoint => string.IsNullOrWhiteSpace(authorizationEndpoint) ? DefaultAuthEndpoint : authorizationEndpoint.Trim();
        private string TokenEndpoint => string.IsNullOrWhiteSpace(tokenEndpoint) ? DefaultTokenEndpoint : tokenEndpoint.Trim();
        private string UserInfoEndpoint => string.IsNullOrWhiteSpace(userInfoEndpoint) ? DefaultUserInfoEndpoint : userInfoEndpoint.Trim();
        private const string Scope = "openid profile in-app-purchases rewards";

        [Header("Your Jiwe OAuth client credentials (Jiwe profile > Apps)")]
        public string clientId;
        // Your OAuth client secret. How it is presented to the token endpoint is controlled by clientAuth
        // below, because Jiwe's server has been seen to want DIFFERENT things at different times and for
        // different clients: Android (KentePunk, July) needed it in the form body and returned 401
        // invalid_client without it; a hosted WebGL deploy (JiweChess, 2026-09-09) and a Node backend
        // (Sheng Word League) both got a 400 "client authentication must only be provided using one
        // mechanism" until it moved to an Authorization: Basic header with client_id/client_secret OUT of
        // the body. Jiwe's docs only say a confidential client "authenticates itself with its client
        // secret" and that a device-resident app "holds no secret". Leave this empty to send none (public
        // client + PKCE, per the docs). A secret you set here ships inside your compiled build — keep it
        // out of source control via a gitignored config asset (see the README).
        public string apiSecret;

        public enum TokenClientAuth
        {
            /// <summary>Try an Authorization: Basic header first; if Jiwe rejects the client authentication, retry ONCE with the secret in the form body. Costs an extra request only on environments that want the body form.</summary>
            Auto,
            /// <summary>Authorization: Basic base64(clientId:apiSecret); client_id/client_secret are NOT also put in the body (sending both is rejected).</summary>
            BasicHeader,
            /// <summary>client_id and client_secret in the form body.</summary>
            RequestBody
        }
        [Tooltip("How apiSecret is sent to the token endpoint. Auto tries Basic then falls back to the body once. Set it explicitly once you know which your environment wants — see the README troubleshooting table.")]
        public TokenClientAuth clientAuth = TokenClientAuth.Auto;

        [Header("Android/iOS only — must be registered with Jiwe as a redirect_uri")]
        public string mobileRedirectScheme = "jiwewallet";

        [Header("Standalone/Editor only — must be registered with Jiwe as a redirect_uri")]
        [Tooltip("0 = pick a free port each login (the redirect_uri then changes every time, so it can't match a registered one). Set a fixed port, e.g. 53682, and register exactly http://127.0.0.1:53682/ with Jiwe. Must be a port that is free on the player's machine.")]
        public int loopbackPort = 0;

        [Header("Optional — only for GetRewardsEarned and other OIDC-bearer calls")]
        [Tooltip("Extra space-separated scopes appended to the login request, e.g. \"rewards:read\". Leave empty to keep today's behaviour exactly. Every scope you list must ALSO be enabled on your app's registration form, or login can fail with access_denied.")]
        public string extraScopes = "";
        [Tooltip("Your Wallet API resource identifier (issued by Jiwe during onboarding). When set, it's sent as &resource= so the access token is audienced to the Wallet API — per Jiwe's docs, a token minted without it is for the default resource and Wallet calls return 401. Leave empty if you only use API-key calls.")]
        public string walletResource = "";

        [Header("Optional — WebGL redirect URI override")]
        [Tooltip("WebGL only. Leave empty to use the page's own URL without its query string. Set this when your host serves the build under a path that changes per release, so the redirect URI you registered with Jiwe stays stable (it must match exactly, scheme/host/port/path). Must be a page that loads this game.")]
        public string webglRedirectUri = "";

        [Header("Optional — non-production environments only")]
        [Tooltip("Leave all three empty for production (id.jiwe.io). Jiwe runs separate non-production environments with different issuers and credentials — use the endpoints Jiwe gives you for it. Jiwe's docs say to read these from {issuer}/.well-known/openid-configuration rather than hardcode them.")]
        public string authorizationEndpoint = "";
        public string tokenEndpoint = "";
        public string userInfoEndpoint = "";

        [Header("Behaviour")]
        [Tooltip("Start login automatically on Awake. Turn off to call Login() yourself (e.g. from a menu button).")]
        public bool loginOnStart = true;

        /// <summary>The id_token. Empty until login succeeds. JiweWallet sends it as X-API-TOKEN on API-key calls
        /// unless <c>walletResource</c> is set (then the access token is sent instead — see
        /// JiweWallet.playerTokenKind). Never send an ID token as an <c>Authorization: Bearer</c> value: Jiwe's
        /// docs are explicit that it isn't audienced to an API.</summary>
        public string IdToken { get; private set; } = "";
        /// <summary>The access_token from login. Used as the bearer for the "Hermes OIDC" calls
        /// (<c>GetRewardsEarnedBearer</c>), and as X-API-TOKEN when <c>walletResource</c> is set. Empty until
        /// login succeeds. Short-lived for the Wallet API audience (documented as 15 minutes) and a public
        /// client can't get a refresh token, so this SDK does not refresh it — see
        /// <see cref="IsAccessTokenExpired"/>.</summary>
        public string AccessToken { get; private set; } = "";
        /// <summary>When the access token expires (UTC), from the token response's expires_in. DateTime.MinValue if Jiwe didn't say.</summary>
        public DateTime AccessTokenExpiresAtUtc { get; private set; } = DateTime.MinValue;
        /// <summary>True once the access token's lifetime has passed. When it is, the right move is to send the player back through Login() — not to retry the failed call.</summary>
        public bool IsAccessTokenExpired => AccessTokenExpiresAtUtc != DateTime.MinValue && DateTime.UtcNow >= AccessTokenExpiresAtUtc;
        public bool IsLoggedIn => !string.IsNullOrEmpty(IdToken);
        public JiweUserInfo UserInfo { get; private set; }

        public event Action OnLoginSuccess;
        public event Action<string> OnLoginFailed;

        [Header("Network safety")]
        [Tooltip("A hung browser/redirect or a slow token exchange previously had NO way out at all — this bounds every network wait in the login flow so it always eventually fails visibly instead of hanging forever. See Cancel().")]
        public float networkTimeoutSeconds = 20f;

        private string _codeVerifier;
        private string _state;
        private System.Net.HttpListener _activeListener; // set only while the Standalone/Editor loopback path is waiting — lets Cancel() unblock it
        private bool _awaitingDeepLink; // set only while the Android/iOS deep-link path is waiting — same role as _activeListener above, but there's no object to Stop(); HandleDeepLink checks this instead
        private bool _cancelled;
        private bool _loginInProgress; // re-entrancy guard — calling Login() again while one is already waiting on a redirect would start a second loopback listener/deep-link wait on top of the first, leaking state instead of replacing it

        /// <summary>True from the moment Login() is called until it succeeds, fails, or is cancelled. Not required to use the SDK, but useful if your own UI wants to disable a "Log In" button (while still keeping Skip/Cancel live — see Cancel()) rather than relying on this SDK to silently ignore a double-tap.</summary>
        public bool IsLoggingIn => _loginInProgress;

        /// <summary>
        /// Optional: take over opening the login URL, e.g. to show it in an in-app browser tab (Jiwe's docs
        /// say a system or in-app browser tab — never an embedded web view, which breaks federated sign-in),
        /// or to capture it in an automated test. Static, so set it once at startup. Null = Application.OpenURL.
        /// Not used on WebGL, which navigates the current tab itself.
        /// </summary>
        public static Action<string> UrlOpener;
        private static void OpenBrowser(string url) { if (UrlOpener != null) UrlOpener(url); else Application.OpenURL(url); }

        private const string WebGlVerifierKey = "jiwe_pkce_verifier";
        private const string WebGlStateKey = "jiwe_pkce_state";

        private void Awake()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // A WebGL login resumes here after the full-page redirect back from Jiwe.
            if (TryResumeWebGlRedirect()) return;
#endif
            if (loginOnStart) Login();
        }

#if UNITY_ANDROID || UNITY_IOS
        private void OnEnable() => Application.deepLinkActivated += HandleDeepLink;
        private void OnDisable() => Application.deepLinkActivated -= HandleDeepLink;
#endif

        /// <summary>
        /// Escape hatch for a login that never comes back — a hung browser, a dropped redirect, or a slow
        /// server all previously had NO way out short of restarting the app. Always give the player a
        /// "Skip"/"Cancel" button that stays clickable and calls this the entire time login is in
        /// progress, not just before it starts (a login screen that disables its OWN escape route while
        /// waiting is the single most costly mistake found building with this SDK — see the README).
        /// Safe to call even if no login is in progress.
        /// </summary>
        public void Cancel()
        {
            _cancelled = true;
            _loginInProgress = false;
            _awaitingDeepLink = false; // see HandleDeepLink — a redirect that arrives after this point is treated as stale, not completed
            try { _activeListener?.Stop(); _activeListener?.Close(); } catch { /* already stopped/disposed — fine */ }
        }

        /// <summary>
        /// Starts (or restarts) the login flow. Safe to call from a "Log in" button instead of relying on
        /// loginOnStart. Ignored (with a warning) if a login is already in progress — call Cancel() first
        /// if you want to restart rather than let this silently start a second, competing wait.
        /// </summary>
        public void Login()
        {
            if (_loginInProgress)
            {
                Debug.LogWarning("[JiweAuth] Login() called while one is already in progress — ignored. Call Cancel() first to restart.");
                return;
            }

            _loginInProgress = true;
            _cancelled = false;
            _codeVerifier = RandomUrlSafe(32);
            _state = RandomUrlSafe(16);
            string codeChallenge = Base64UrlEncode(Sha256(_codeVerifier));

#if UNITY_STANDALONE || UNITY_EDITOR
            LoginViaLoopback(codeChallenge);
#elif UNITY_ANDROID || UNITY_IOS
            LoginViaDeepLink(codeChallenge);
#elif UNITY_WEBGL
            LoginViaWebRedirect(codeChallenge);
#else
            Fail("JiweAuth doesn't support this platform yet.");
#endif
        }

        private string BuildAuthUrl(string redirectUri, string codeChallenge)
        {
            string scope = string.IsNullOrWhiteSpace(extraScopes) ? Scope : $"{Scope} {extraScopes.Trim()}";
            string url = $"{AuthEndpoint}?response_type=code&scope={Uri.EscapeDataString(scope)}" +
                   $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&client_id={Uri.EscapeDataString(clientId)}" +
                   $"&state={_state}&code_challenge={codeChallenge}&code_challenge_method=S256";
            if (!string.IsNullOrWhiteSpace(walletResource)) url += $"&resource={Uri.EscapeDataString(walletResource.Trim())}";
            return url;
        }

        // ---------------------------------------------------------------
        // Standalone / Editor: loopback listener
        // ---------------------------------------------------------------
#if UNITY_STANDALONE || UNITY_EDITOR
        private async void LoginViaLoopback(string codeChallenge)
        {
            var listener = new System.Net.HttpListener();
            int port = loopbackPort > 0 ? loopbackPort : GetFreeLoopbackPort();
            string redirectUri = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(redirectUri);
            listener.Start();
            _activeListener = listener;

            OpenBrowser(BuildAuthUrl(redirectUri, codeChallenge));

            System.Net.HttpListenerContext context;
            try
            {
                // No inherent timeout on GetContextAsync() itself — a browser that never redirects back
                // (closed, backgrounded, network dropped) would hang here forever with no escape besides
                // Cancel(). Racing it against a delay task bounds it the same way every other network wait
                // in this SDK is bounded.
                var contextTask = listener.GetContextAsync();
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(networkTimeoutSeconds));
                if (await Task.WhenAny(contextTask, timeoutTask) == timeoutTask)
                {
                    Fail(_cancelled ? "Login cancelled." : "Timed out waiting for the browser redirect.");
                    return;
                }
                context = await contextTask;
            }
            catch (Exception e)
            {
                // Reachable via Cancel() stopping/closing the listener out from under this await.
                Fail(_cancelled ? "Login cancelled." : $"Local login listener failed: {e.Message}");
                return;
            }
            finally
            {
                // Deliberately NOT stopping the listener here — see the ROOT CAUSE note below. It only
                // gets stopped after the response is fully written, later in this method.
                _activeListener = null;
            }

            var query = context.Request.QueryString;
            // ROOT CAUSE (found the hard way): this used to call listener.Stop() in the finally block
            // above, immediately after GetContextAsync() returned — before the response below was ever
            // written. Stop() disposes any still-open HttpListenerResponse for that listener, so it raced
            // RespondToBrowser() below: sometimes the browser got its "Login complete" page before Stop()
            // won the race, sometimes Stop() won first and writing the response threw
            // ObjectDisposedException — silently, inside a fire-and-forget async method, which is exactly
            // why this could sit there looking hung with zero console output. Listener now only stops
            // AFTER the response is fully written and closed, a few lines down.
            await RespondToBrowser(context);
            listener.Stop();

            if (!ValidateRedirect(query["error"], query["code"], query["state"], out string code, out string error))
            {
                Fail(error);
                return;
            }

            await ExchangeCodeForToken(code, redirectUri);
        }

        private static int GetFreeLoopbackPort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task RespondToBrowser(System.Net.HttpListenerContext context)
        {
            var response = context.Response;
            byte[] buffer = Encoding.UTF8.GetBytes("<html><body>Login complete — you can return to the game.</body></html>");
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }
#endif

        // ---------------------------------------------------------------
        // Android / iOS: custom URI scheme deep link
        // ---------------------------------------------------------------
#if UNITY_ANDROID || UNITY_IOS
        private string _pendingRedirectUri;

        private void LoginViaDeepLink(string codeChallenge)
        {
            _pendingRedirectUri = $"{mobileRedirectScheme}://oauth-callback";
            _awaitingDeepLink = true;
            OpenBrowser(BuildAuthUrl(_pendingRedirectUri, codeChallenge));
        }

        private async void HandleDeepLink(string url)
        {
            // GREY AREA FIXED: this used to process ANY deep link that arrived, even one that showed up
            // after Cancel() — so a player who tapped Skip, assumed they'd moved on, and had the OS bring
            // the app back to the foreground later (a slow/delayed redirect is a real possibility, not just
            // theoretical) would silently get logged in anyway, well after their own UI had moved past the
            // login screen. Now a stale/late redirect is explicitly ignored.
            if (!_awaitingDeepLink) return;
            _awaitingDeepLink = false;

            var query = ParseQuery(url);
            query.TryGetValue("error", out string error);
            query.TryGetValue("code", out string code);
            query.TryGetValue("state", out string state);

            if (!ValidateRedirect(error, code, state, out string validCode, out string validationError))
            {
                Fail(validationError);
                return;
            }

            await ExchangeCodeForToken(validCode, _pendingRedirectUri);
        }
#endif

        // ---------------------------------------------------------------
        // WebGL: same-tab full-page redirect
        // ---------------------------------------------------------------
#if UNITY_WEBGL
        // Three WebGL-specific traps, all found by live testing (JiweChess, 2026-09-09), all handled by
        // Plugins/WebGL/JiweStorage.jslib:
        //  1. Application.OpenURL on WebGL ALWAYS opens a NEW tab (window.open _blank). This flow needs
        //     the SAME tab — Jiwe redirects back to this page — so the original tab sat on "Signing in..."
        //     forever while a second tab logged in with nobody listening. We navigate in place instead.
        //  2. PlayerPrefs on WebGL is IndexedDB-backed and asynchronous even after Save(); the page can
        //     unload before the PKCE verifier is flushed, so the token exchange later has no verifier.
        //     localStorage.setItem is synchronous, so the verifier/state go there.
        //  3. After the redirect the address bar still holds ?code=...; a refresh would replay a
        //     single-use code, so the query is stripped once it's been read.
        private void LoginViaWebRedirect(string codeChallenge)
        {
            string redirectUri = GetWebRedirectUri();
#if !UNITY_EDITOR
            JiweStorage_SetItem(WebGlVerifierKey, _codeVerifier);
            JiweStorage_SetItem(WebGlStateKey, _state);
            JiweStorage_NavigateTo(BuildAuthUrl(redirectUri, codeChallenge));
#else
            Application.OpenURL(BuildAuthUrl(redirectUri, codeChallenge));
#endif
        }

#if !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void JiweStorage_SetItem(string key, string value);
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern string JiweStorage_GetItem(string key);
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void JiweStorage_RemoveItem(string key);
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void JiweStorage_ClearUrlQuery();
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void JiweStorage_NavigateTo(string url);

        private bool TryResumeWebGlRedirect()
        {
            var query = ParseQuery(Application.absoluteURL);
            if (!query.ContainsKey("code") && !query.ContainsKey("error")) return false;

            // This is a fresh page load (a new script instance after the full-page redirect), so
            // IsLoggingIn would otherwise incorrectly read false here even while the exchange below is
            // genuinely in flight — this path never goes through Login(), which is the only other place
            // that sets it.
            _loginInProgress = true;
            _codeVerifier = JiweStorage_GetItem(WebGlVerifierKey);
            _state = JiweStorage_GetItem(WebGlStateKey);
            JiweStorage_RemoveItem(WebGlVerifierKey);
            JiweStorage_RemoveItem(WebGlStateKey);
            JiweStorage_ClearUrlQuery();

            query.TryGetValue("error", out string error);
            query.TryGetValue("code", out string code);
            query.TryGetValue("state", out string state);

            if (!ValidateRedirect(error, code, state, out string validCode, out string validationError))
            {
                Fail(validationError);
                return true;
            }

            _ = ExchangeCodeForToken(validCode, GetWebRedirectUri());
            return true;
        }
#endif

        private string GetWebRedirectUri()
        {
            if (!string.IsNullOrWhiteSpace(webglRedirectUri)) return webglRedirectUri.Trim();
            string url = Application.absoluteURL;
            int cut = url.IndexOfAny(new[] { '?', '#' });
            return cut >= 0 ? url.Substring(0, cut) : url;
        }
#endif

        // ---------------------------------------------------------------
        // Shared: validation, token exchange, user info
        // ---------------------------------------------------------------
        private bool ValidateRedirect(string error, string code, string state, out string validCode, out string validationError)
        {
            validCode = code;
            validationError = null;

            if (!string.IsNullOrEmpty(error)) { validationError = $"Jiwe login denied: {error}"; return false; }
            if (string.IsNullOrEmpty(code)) { validationError = "No authorization code in redirect."; return false; }
            if (state != _state) { validationError = "State mismatch — possible CSRF, aborting login."; return false; }
            return true;
        }

        private class TokenAttempt { public bool TimedOut; public bool Ok; public long Status; public string Body; public string Error; }

        private async Task<TokenAttempt> SendTokenRequest(string code, string redirectUri, bool basicHeader)
        {
            var form = new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", code },
                { "redirect_uri", redirectUri },
                { "code_verifier", _codeVerifier }
            };
            bool hasSecret = !string.IsNullOrEmpty(apiSecret);
            // No secret = a public client (PKCE only): client_id in the body, nothing to authenticate with.
            if (!hasSecret || !basicHeader)
            {
                form["client_id"] = clientId;
                if (hasSecret) form["client_secret"] = apiSecret;
            }

            using var req = UnityWebRequest.Post(TokenEndpoint, form);
            if (hasSecret && basicHeader)
                req.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{apiSecret}")));

            if (!await WaitBounded(req)) return new TokenAttempt { TimedOut = true };
            return new TokenAttempt
            {
                Ok = req.result == UnityWebRequest.Result.Success,
                Status = req.responseCode,
                Body = req.downloadHandler?.text,
                Error = req.error
            };
        }

        // The two failure signatures seen from Jiwe when client authentication is presented the wrong way:
        // 401 invalid_client, and 400 "client authentication must only be provided using one mechanism".
        private static bool LooksLikeClientAuthRejection(TokenAttempt a)
        {
            if (a.Ok) return false;
            if (a.Status == 401) return true;
            string b = a.Body ?? "";
            return a.Status == 400 && (b.Contains("client authentication") || b.Contains("mechanism") || b.Contains("invalid_client"));
        }

        private async Task ExchangeCodeForToken(string code, string redirectUri)
        {
            bool basicFirst = clientAuth != TokenClientAuth.RequestBody;
            var attempt = await SendTokenRequest(code, redirectUri, basicFirst);
            if (!attempt.TimedOut && clientAuth == TokenClientAuth.Auto && !string.IsNullOrEmpty(apiSecret) && LooksLikeClientAuthRejection(attempt))
            {
                Debug.Log("[JiweAuth] Token endpoint rejected the client authentication — retrying once with the secret in the request body. Set JiweAuth.clientAuth explicitly to skip this.");
                attempt = await SendTokenRequest(code, redirectUri, !basicFirst);
            }

            if (attempt.TimedOut) { Fail(_cancelled ? "Login cancelled." : "Token exchange timed out — no response from Jiwe."); return; }

            if (!attempt.Ok)
            {
                // The raw status line alone (e.g. "HTTP/1.1 401 Unauthorized") says nothing about why. OAuth
                // token endpoints almost always return a JSON error body ({error, error_description}) even
                // on a 4xx; surface that instead whenever present.
                string detail = attempt.Body;
                if (!string.IsNullOrEmpty(detail))
                {
                    try
                    {
                        var oauthError = JsonUtility.FromJson<OAuthErrorResponse>(detail);
                        if (!string.IsNullOrEmpty(oauthError?.error))
                        {
                            string desc = !string.IsNullOrEmpty(oauthError.error_description) ? $": {oauthError.error_description}" : "";
                            Fail($"Token exchange failed: {oauthError.error}{desc} ({attempt.Error})");
                            return;
                        }
                    }
                    catch (Exception) { /* body wasn't the expected OAuth error shape — fall through */ }
                    Fail($"Token exchange failed: {attempt.Error} — {detail}");
                    return;
                }
                Fail($"Token exchange failed: {attempt.Error}");
                return;
            }

            var token = JsonUtility.FromJson<TokenResponse>(attempt.Body);
            if (string.IsNullOrEmpty(token.id_token))
            {
                Fail("Token response had no id_token.");
                return;
            }

            IdToken = token.id_token;
            AccessToken = token.access_token;
            AccessTokenExpiresAtUtc = token.expires_in > 0 ? DateTime.UtcNow.AddSeconds(token.expires_in) : DateTime.MinValue;
            // Profile claims ride in the ID token itself. Jiwe's /me endpoint has been seen rejecting every
            // token (confirmed live 2026-09-14), so don't depend on it for the player's name.
            UserInfo = DecodeIdTokenClaims(token.id_token) ?? new JiweUserInfo();
            await FetchUserInfo(token.access_token);
            _loginInProgress = false;
            OnLoginSuccess?.Invoke();
        }

        // Reads the claims out of the ID token we just received directly from the token endpoint over TLS.
        // Deliberately NOT signature-verified — that is a server's job when it accepts a token from someone
        // else; here it only seeds display fields (sub, name, nickname, bio, picture, walletId).
        private static JiweUserInfo DecodeIdTokenClaims(string idToken)
        {
            try
            {
                var parts = idToken.Split('.');
                if (parts.Length < 2) return null;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                return JsonUtility.FromJson<JiweUserInfo>(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            }
            catch (Exception) { return null; }
        }

        private async Task FetchUserInfo(string accessToken)
        {
            using var req = UnityWebRequest.Get(UserInfoEndpoint);
            req.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            if (!await WaitBounded(req)) return; // best-effort — a failure here (including /me rejecting the token) only skips filling in fields the ID token lacked; login itself already succeeded

            if (req.result == UnityWebRequest.Result.Success)
            {
                var fresh = JsonUtility.FromJson<JiweUserInfo>(req.downloadHandler.text);
                if (fresh == null) return;
                UserInfo ??= new JiweUserInfo();
                if (string.IsNullOrEmpty(UserInfo.sub)) UserInfo.sub = fresh.sub;
                if (string.IsNullOrEmpty(UserInfo.name)) UserInfo.name = fresh.name;
                if (string.IsNullOrEmpty(UserInfo.nickname)) UserInfo.nickname = fresh.nickname;
                if (string.IsNullOrEmpty(UserInfo.bio)) UserInfo.bio = fresh.bio;
                if (string.IsNullOrEmpty(UserInfo.picture)) UserInfo.picture = fresh.picture;
            }
        }

        private void Fail(string message)
        {
            _loginInProgress = false;
            // OAuth/server error bodies can occasionally be a full HTML error page (e.g. a proxy/firewall
            // block) rather than JSON — cap length so that never turns into a wall of text in a UI label.
            if (message.Length > 300) message = message.Substring(0, 300) + "…";
            Debug.LogWarning($"[JiweAuth] {message}");
            OnLoginFailed?.Invoke(message);
        }

        /// <summary>
        /// Bounds every UnityWebRequest in the login flow so nothing can hang forever — Cancel() sets
        /// _cancelled, which this notices on the next poll and aborts the request; networkTimeoutSeconds
        /// otherwise fires on its own even if the player never taps Cancel.
        /// </summary>
        private async Task<bool> WaitBounded(UnityWebRequest req)
        {
            var op = req.SendWebRequest();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!op.isDone)
            {
                if (_cancelled || sw.Elapsed.TotalSeconds > networkTimeoutSeconds)
                {
                    req.Abort();
                    return false;
                }
                await Task.Yield();
            }
            return true;
        }

        private static Dictionary<string, string> ParseQuery(string url)
        {
            var result = new Dictionary<string, string>();
            int qIndex = url.IndexOf('?');
            if (qIndex < 0) return result;
            foreach (var pair in url.Substring(qIndex + 1).Split('&'))
            {
                var kv = pair.Split('=');
                if (kv.Length == 2) result[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
            }
            return result;
        }

        private static string RandomUrlSafe(int byteLength)
        {
            var bytes = new byte[byteLength];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Base64UrlEncode(bytes);
        }

        private static byte[] Sha256(string input)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(Encoding.ASCII.GetBytes(input));
        }

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        [Serializable] private class TokenResponse { public string access_token; public string id_token; public string token_type; public int expires_in; }
        [Serializable] private class OAuthErrorResponse { public string error; public string error_description; }
    }

    [Serializable]
    public class JiweUserInfo
    {
        public string sub;
        public string name;
        public string nickname;
        public string bio;
        public string picture;
        /// <summary>Wallet identifier claim — present when the rewards or in-app-purchases scope was granted.</summary>
        public string walletId;
    }
}
