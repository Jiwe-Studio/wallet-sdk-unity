using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Jiwe.Tests
{
    [Serializable] public class LogItem { public string method; public string path; public string query; public string body; public string headers; }
    [Serializable] public class LogEnvelope { public List<LogItem> items; }
    [Serializable] public class StateEnvelope { public int player_balance; public int purchase_count; public int auth_seq; }

    /// <summary>A result slot a callback fills in, with a bounded wait.</summary>
    public class Box<T>
    {
        public T Value; public bool Done;
        public Action<T> Callback => v => { Value = v; Done = true; };
        public IEnumerator Wait(float seconds = 15f)
        {
            float t = Time.realtimeSinceStartup;
            while (!Done && Time.realtimeSinceStartup - t < seconds) yield return null;
            Assert.IsTrue(Done, "timed out waiting for the SDK callback");
        }
    }

    /// <summary>
    /// Shared setup: a clean mock Jiwe server per test, an SDK instance pointed at it, and a helper that
    /// drives a real login through JiweAuth's loopback listener the way a browser redirect would.
    /// </summary>
    public abstract class JiweTestBase
    {
        protected static string Port => Environment.GetEnvironmentVariable("JIWE_MOCK_PORT") ?? "8765";
        protected static string Base => $"http://127.0.0.1:{Port}";
        static readonly HttpClient Http = new HttpClient();

        protected GameObject go;
        protected JiweAuth auth;
        protected JiweWallet wallet;
        protected int loopbackPort;
        protected string capturedAuthUrl;

        // login outcome
        protected bool loginDone, loginOk; protected string loginMessage;
        int _codeSeq;

        [UnitySetUp]
        public IEnumerator SetUpBase()
        {
            Control("/__reset", "{}");
            Control("/__faults", "{\"faults\":[]}");
            capturedAuthUrl = null; loginDone = loginOk = false; loginMessage = null;
            JiweAuth.UrlOpener = u => capturedAuthUrl = u;
            loopbackPort = FreePort();

            go = new GameObject("jiwe-under-test");
            go.SetActive(false);                        // configure before Awake runs
            auth = go.AddComponent<JiweAuth>();
            wallet = go.AddComponent<JiweWallet>();
            auth.loginOnStart = false;
            auth.clientId = "client-1";
            auth.apiSecret = "secret-1";
            auth.authorizationEndpoint = Base + "/auth";
            auth.tokenEndpoint = Base + "/token";
            auth.userInfoEndpoint = Base + "/me";
            auth.loopbackPort = loopbackPort;
            auth.networkTimeoutSeconds = 5f;
            wallet.auth = auth;
            wallet.apiUsername = "test-user";
            wallet.apiKey = "test-key";
            wallet.walletBaseUrl = Base + "/rest";
            auth.OnLoginSuccess += () => { loginDone = true; loginOk = true; };
            auth.OnLoginFailed += m => { loginDone = true; loginOk = false; loginMessage = m; };
            go.SetActive(true);
            yield return null;
        }

        [TearDown]
        public void TearDownBase()
        {
            JiweAuth.UrlOpener = null;
            if (go != null) { try { auth.Cancel(); } catch { } UnityEngine.Object.Destroy(go); }
        }

        // ---- mock control ------------------------------------------------
        protected static string Control(string path, string json, bool get = false)
        {
            var req = new HttpRequestMessage(get ? HttpMethod.Get : HttpMethod.Post, Base + path);
            if (!get) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
            return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        protected static void Config(string json) => Control("/__config", json);
        protected static void Faults(string json) => Control("/__faults", "{\"faults\":" + json + "}");
        protected static StateEnvelope State() => JsonUtility.FromJson<StateEnvelope>(Control("/__state", "", get: true));
        protected static List<LogItem> Log() => JsonUtility.FromJson<LogEnvelope>(Control("/__log", "", get: true)).items;
        protected static List<LogItem> Requests(string pathContains, string method = null)
        {
            var r = new List<LogItem>();
            foreach (var i in Log()) if (i.path.Contains(pathContains) && (method == null || i.method == method)) r.Add(i);
            return r;
        }
        protected static string Header(LogItem item, string name)
        {
            foreach (var line in item.headers.Split('\n'))
                if (line.StartsWith(name.ToLowerInvariant() + ": ")) return line.Substring(name.Length + 2);
            return null;
        }
        protected static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
        }

        // ---- login driver -------------------------------------------------
        /// <summary>Parses a query string value out of the URL JiweAuth asked the browser to open.</summary>
        protected static string QueryParam(string url, string key)
        {
            int q = url.IndexOf('?'); if (q < 0) return null;
            foreach (var pair in url.Substring(q + 1).Split('&'))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (kv[0] == key) return kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            }
            return null;
        }

        protected IEnumerator WaitUntil(Func<bool> cond, float seconds = 10f, string what = "condition")
        {
            float t = Time.realtimeSinceStartup;
            while (!cond() && Time.realtimeSinceStartup - t < seconds) yield return null;
            Assert.IsTrue(cond(), "timed out waiting for " + what);
        }

        /// <summary>Starts a login and waits until JiweAuth has opened the login URL (and so is listening).</summary>
        protected IEnumerator BeginLogin()
        {
            capturedAuthUrl = null; loginDone = false;
            auth.Login();
            yield return WaitUntil(() => capturedAuthUrl != null, 5f, "JiweAuth to open the login URL");
        }

        /// <summary>Plays the browser's part: hits the loopback redirect URI with the given query.</summary>
        protected void BrowserRedirect(string codeOrNull, string state, string error = null)
        {
            string redirect = QueryParam(capturedAuthUrl, "redirect_uri");
            string q = error != null ? $"error={error}&state={state}" : $"code={codeOrNull}&state={state}";
            _ = Http.GetAsync(redirect + "?" + q);     // fire and forget: JiweAuth answers on the main thread
        }

        /// <summary>A complete, successful login: open URL -> redirect with the right state -> token exchange.</summary>
        protected IEnumerator LoginOk()
        {
            yield return BeginLogin();
            BrowserRedirect("code-" + (++_codeSeq) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6), QueryParam(capturedAuthUrl, "state"));
            yield return WaitUntil(() => loginDone, 15f, "login to finish");
            Assert.IsTrue(loginOk, "login should succeed but failed: " + loginMessage);
        }

        protected IEnumerator LoginExpectingFailure(string code = "code-x", string state = null, string error = null)
        {
            yield return BeginLogin();
            BrowserRedirect(code, state ?? QueryParam(capturedAuthUrl, "state"), error);
            yield return WaitUntil(() => loginDone, 15f, "login to finish");
            Assert.IsFalse(loginOk, "login should have failed");
        }
    }
}
