using System;
using Jiwe;
using UnityEngine;

namespace JiweSamples
{
    /// <summary>
    /// STARTER 0 — guest-first setup (the pattern JiweChess uses). Put this on one GameObject; it adds
    /// JiweAuth and JiweWallet, loads credentials from a gitignored asset, and does NOT log anyone in
    /// until you call <see cref="BeginSignIn"/>. Players play as guests first; sign-in is for the
    /// features that need an account (leaderboard writes, rewards, purchases).
    ///
    /// [DefaultExecutionOrder(-100)] is load-bearing: on WebGL, JiweAuth.Awake() resumes an in-flight
    /// login using whatever credentials are on it at that moment, so credentials must be injected first.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(JiweAuth), typeof(JiweWallet))]
    public class JiweBootstrap : MonoBehaviour
    {
        public static JiweBootstrap Instance { get; private set; }

        [Tooltip("Off = guest-first: nothing calls Jiwe until BeginSignIn(). On = log in as soon as the game starts.")]
        public bool signInOnStart = false;
        public bool persistAcrossScenes = true;

        public JiweAuth Auth { get; private set; }
        public JiweWallet Wallet { get; private set; }
        public bool IsConfigured { get; private set; }
        public bool IsLoggedIn => Auth != null && Auth.IsLoggedIn;
        public string Status { get; private set; } = "";

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            if (persistAcrossScenes) DontDestroyOnLoad(gameObject);

            Auth = GetComponent<JiweAuth>();
            Wallet = GetComponent<JiweWallet>();
            Wallet.auth = Auth;
            Auth.loginOnStart = signInOnStart;

            // Permanent, not just per-call: on WebGL the redirect is a full page reload, so a brand-new
            // bootstrap has no memory of the button click that started the login.
            Auth.OnLoginSuccess += () => Status = "Signed in.";
            Auth.OnLoginFailed += msg => Status = "Sign-in failed: " + msg;

            ApplyConfig(Resources.Load<JiweCredentialsConfig>("JiweCredentials"));
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        /// <summary>Copies credentials onto the SDK components. A null config leaves whatever is already set in the Inspector.</summary>
        public bool ApplyConfig(JiweCredentialsConfig cfg)
        {
            if (cfg != null && !string.IsNullOrEmpty(cfg.clientId))
            {
                Auth.clientId = cfg.clientId; Auth.apiSecret = cfg.apiSecret; Auth.walletResource = cfg.walletResource;
                Wallet.apiUsername = cfg.apiUsername; Wallet.apiKey = cfg.apiKey;
            }
            IsConfigured = !string.IsNullOrEmpty(Auth.clientId);
            if (!IsConfigured) Debug.LogWarning("[JiweBootstrap] No Jiwe credentials found (Resources/JiweCredentials.asset) — sign-in is disabled.");
            return IsConfigured;
        }

        /// <summary>Starts sign-in. Fails fast, with a message, rather than opening a browser to a blank client_id.</summary>
        public void BeginSignIn(Action onSuccess, Action<string> onFailure)
        {
            if (!IsConfigured) { onFailure?.Invoke("Jiwe sign-in isn't set up on this build yet."); return; }

            void Done() { Auth.OnLoginSuccess -= Done; Auth.OnLoginFailed -= Failed; onSuccess?.Invoke(); }
            void Failed(string m) { Auth.OnLoginSuccess -= Done; Auth.OnLoginFailed -= Failed; onFailure?.Invoke(m); }
            Auth.OnLoginSuccess += Done;
            Auth.OnLoginFailed += Failed;
            Auth.Login();
        }

        /// <summary>Wire to your sign-in screen's Cancel button; stays valid for the whole login.</summary>
        public void CancelSignIn() => Auth?.Cancel();

        /// <summary>Awards XP, quietly doing nothing for a guest (who has no token to credit).</summary>
        public void AwardXp(int amount, string description, Action<bool> onComplete = null)
        {
            if (!IsConfigured || !IsLoggedIn) { onComplete?.Invoke(false); return; }
            Wallet.GiveXpReward(amount, description, r =>
            {
                if (!r.Success) Debug.LogWarning("[JiweBootstrap] XP award failed: " + r.Error);
                onComplete?.Invoke(r.Success);
            });
        }

        void OnGUI()
        {
            SampleGui.Begin("Jiwe bootstrap (guest-first)");
            GUILayout.Label(IsConfigured ? "Credentials loaded." : "<color=orange>No credentials configured.</color>");
            SampleGui.LoginBar(Auth);
            if (!string.IsNullOrEmpty(Status)) GUILayout.Label(Status);
            SampleGui.End();
        }
    }
}
