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
    /// This produces the id_token that JiweWallet needs (as X-API-TOKEN) for
    /// reward calls — it does NOT carry the X-API-USERNAME/X-API-KEY wallet
    /// credentials, since those are static per-app values used on every
    /// wallet call (including ones that don't need a logged-in player, like
    /// the leaderboard) — see JiweWallet for those.
    ///
    /// Endpoints are fixed per Jiwe's own docs (id.jiwe.io) and no longer need
    /// to be typed into the Inspector per project. Only your OAuth client
    /// credentials (from your Jiwe profile > Apps) are required:
    ///   - clientId, apiSecret (used as the token endpoint's client_secret)
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
        private const string AuthEndpoint = "https://id.jiwe.io/auth";
        private const string TokenEndpoint = "https://id.jiwe.io/token";
        private const string UserInfoEndpoint = "https://id.jiwe.io/me";
        private const string Scope = "openid profile in-app-purchases rewards";

        [Header("Your Jiwe OAuth client credentials (Jiwe profile > Apps)")]
        public string clientId;
        // Sent as the token endpoint's client_secret, in the POST BODY (not an Authorization: Basic
        // header) — confirmed by testing that Jiwe's real token endpoint requires this on EVERY platform,
        // including a pure PKCE mobile/deep-link flow. That's not textbook OAuth-spec "public client"
        // behavior (PKCE exists specifically so a public/native client doesn't need a secret at all), but
        // it's what their server actually expects — omitting it fails with a 401 invalid_client on mobile,
        // confirmed live. Don't "fix" this away for security purity without re-confirming against a real
        // token exchange first. This does mean the secret ships inside your compiled build (Android APK,
        // WebGL bundle) — accept that tradeoff deliberately, and see the README for how to keep it out of
        // source control at least (a gitignored local config asset, not a hardcoded value here).
        public string apiSecret;

        [Header("Android/iOS only — must be registered with Jiwe as a redirect_uri")]
        public string mobileRedirectScheme = "jiwewallet";

        [Header("Behaviour")]
        [Tooltip("Start login automatically on Awake. Turn off to call Login() yourself (e.g. from a menu button).")]
        public bool loginOnStart = true;

        /// <summary>The id_token to send as the reward/purchase API's bearer token. Empty until login succeeds.</summary>
        public string IdToken { get; private set; } = "";
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
        private bool _cancelled;

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
            try { _activeListener?.Stop(); _activeListener?.Close(); } catch { /* already stopped/disposed — fine */ }
        }

        /// <summary>Starts (or restarts) the login flow. Safe to call from a "Log in" button instead of relying on loginOnStart.</summary>
        public void Login()
        {
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
            return $"{AuthEndpoint}?response_type=code&scope={Uri.EscapeDataString(Scope)}" +
                   $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&client_id={Uri.EscapeDataString(clientId)}" +
                   $"&state={_state}&code_challenge={codeChallenge}&code_challenge_method=S256";
        }

        // ---------------------------------------------------------------
        // Standalone / Editor: loopback listener
        // ---------------------------------------------------------------
#if UNITY_STANDALONE || UNITY_EDITOR
        private async void LoginViaLoopback(string codeChallenge)
        {
            var listener = new System.Net.HttpListener();
            int port = GetFreeLoopbackPort();
            string redirectUri = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(redirectUri);
            listener.Start();
            _activeListener = listener;

            Application.OpenURL(BuildAuthUrl(redirectUri, codeChallenge));

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
            Application.OpenURL(BuildAuthUrl(_pendingRedirectUri, codeChallenge));
        }

        private async void HandleDeepLink(string url)
        {
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
        private void LoginViaWebRedirect(string codeChallenge)
        {
            string redirectUri = GetPageUrlWithoutQuery();
#if !UNITY_EDITOR
            PlayerPrefs.SetString(WebGlVerifierKey, _codeVerifier);
            PlayerPrefs.SetString(WebGlStateKey, _state);
            PlayerPrefs.Save();
#endif
            Application.OpenURL(BuildAuthUrl(redirectUri, codeChallenge));
        }

#if !UNITY_EDITOR
        private bool TryResumeWebGlRedirect()
        {
            var query = ParseQuery(Application.absoluteURL);
            if (!query.ContainsKey("code") && !query.ContainsKey("error")) return false;

            _codeVerifier = PlayerPrefs.GetString(WebGlVerifierKey, "");
            _state = PlayerPrefs.GetString(WebGlStateKey, "");
            PlayerPrefs.DeleteKey(WebGlVerifierKey);
            PlayerPrefs.DeleteKey(WebGlStateKey);

            query.TryGetValue("error", out string error);
            query.TryGetValue("code", out string code);
            query.TryGetValue("state", out string state);

            if (!ValidateRedirect(error, code, state, out string validCode, out string validationError))
            {
                Fail(validationError);
                return true;
            }

            _ = ExchangeCodeForToken(validCode, GetPageUrlWithoutQuery());
            return true;
        }
#endif

        private static string GetPageUrlWithoutQuery()
        {
            string url = Application.absoluteURL;
            int qIndex = url.IndexOf('?');
            return qIndex >= 0 ? url.Substring(0, qIndex) : url;
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

        private async Task ExchangeCodeForToken(string code, string redirectUri)
        {
            var form = new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", code },
                { "redirect_uri", redirectUri },
                { "client_id", clientId },
                { "client_secret", apiSecret },
                { "code_verifier", _codeVerifier }
            };

            using var req = UnityWebRequest.Post(TokenEndpoint, form);
            if (!await WaitBounded(req)) { Fail(_cancelled ? "Login cancelled." : "Token exchange timed out — no response from Jiwe."); return; }

            if (req.result != UnityWebRequest.Result.Success)
            {
                // req.error alone is just the raw HTTP status line (e.g. "HTTP/1.1 401 Unauthorized") — no
                // indication of why. OAuth token endpoints almost always return a JSON error body
                // ({error, error_description}) even on a 4xx; surface that instead whenever present. Found
                // the hard way: this was the only thing that made a real invalid_client failure
                // diagnosable at all (it turned out Jiwe's token endpoint requires client_secret in the
                // body — see the field comment on apiSecret above — omitting it fails exactly like this).
                string detail = req.downloadHandler?.text;
                if (!string.IsNullOrEmpty(detail))
                {
                    try
                    {
                        var oauthError = JsonUtility.FromJson<OAuthErrorResponse>(detail);
                        if (!string.IsNullOrEmpty(oauthError?.error))
                        {
                            string desc = !string.IsNullOrEmpty(oauthError.error_description) ? $": {oauthError.error_description}" : "";
                            Fail($"Token exchange failed: {oauthError.error}{desc} ({req.error})");
                            return;
                        }
                    }
                    catch (Exception) { /* body wasn't the expected OAuth error shape — fall through */ }
                    Fail($"Token exchange failed: {req.error} — {detail}");
                    return;
                }
                Fail($"Token exchange failed: {req.error}");
                return;
            }

            var token = JsonUtility.FromJson<TokenResponse>(req.downloadHandler.text);
            if (string.IsNullOrEmpty(token.id_token))
            {
                Fail("Token response had no id_token.");
                return;
            }

            IdToken = token.id_token;
            await FetchUserInfo(token.access_token);
            OnLoginSuccess?.Invoke();
        }

        private async Task FetchUserInfo(string accessToken)
        {
            using var req = UnityWebRequest.Get(UserInfoEndpoint);
            req.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            if (!await WaitBounded(req)) return; // best-effort — timeout/cancel here just skips the display-name refresh, login itself already succeeded

            if (req.result == UnityWebRequest.Result.Success)
            {
                UserInfo = JsonUtility.FromJson<JiweUserInfo>(req.downloadHandler.text);
            }
        }

        private void Fail(string message)
        {
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
    }
}
