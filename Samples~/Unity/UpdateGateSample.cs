using System;
using System.Globalization;
using Jiwe;
using UnityEngine;

namespace JiweSamples
{
    /// <summary>
    /// STARTER 2 — update gate + announcements (KentePunk's startup guard).
    /// Two independent "please update" signals — rotated API keys, and a minimum version you publish in
    /// Game Data — plus an announcement banner. Every check FAILS OPEN: a network blip never blocks a player.
    ///
    /// Publish settings with POST /rest/api/v1/metadata/update/game-data (Postman is fine):
    ///   {"gameData":{"minVersion":"1.7","announcement":{"id":"evt-1","message":"Double XP weekend!","expiresAt":"2026-12-01T00:00:00Z"}}}
    /// Remember: everything in Game Data is PUBLIC.
    /// </summary>
    [RequireComponent(typeof(JiweAuth), typeof(JiweWallet))]
    public class UpdateGateSample : MonoBehaviour
    {
        public bool checkOnStart = true;
        [Tooltip("Pretend to be this version (tests, demos). Empty = Application.version.")]
        public string currentVersionOverride = "";
        [Tooltip("Shown with the update message so the player can act on it, e.g. your store page.")]
        public string updateUrl = "";

        public bool Checked { get; private set; }
        public bool UpdateRequired { get; private set; }
        public string MinVersion { get; private set; } = "";
        /// <summary>The announcement to show now (empty if none, already seen, or expired).</summary>
        public string Announcement { get; private set; } = "";

        const string SeenKey = "jiwe_sample_last_announcement";
        string _announcementId = "";
        JiweWallet _wallet;

        [Serializable] class Envelope { public string type; public Body gameData; }
        [Serializable] class Body { public string minVersion; public Notice announcement; }
        [Serializable] class Notice { public string id; public string message; public string expiresAt; }

        void Awake() { _wallet = GetComponent<JiweWallet>(); if (_wallet.auth == null) _wallet.auth = GetComponent<JiweAuth>(); }
        void Start() { if (checkOnStart) Check(); }

        string Current => string.IsNullOrEmpty(currentVersionOverride) ? Application.version : currentVersionOverride;

        public void Check()
        {
            // 1) Key rotation: false ONLY on a definitive 401/403 — never on a blip.
            _wallet.CheckCredentialsValid(valid => { if (!valid) UpdateRequired = true; });

            // 2) Minimum version + announcement, both in Game Data.
            _wallet.GetGameData(r =>
            {
                Checked = true;
                if (!r.Success) return;                                   // fail OPEN
                Envelope env = null;
                try { env = JsonUtility.FromJson<Envelope>(r.RawResponse); } catch (Exception) { }
                var data = env?.gameData; if (data == null) return;

                MinVersion = data.minVersion ?? "";
                if (IsOlderThan(Current, MinVersion)) UpdateRequired = true;

                var n = data.announcement;
                if (n != null && !string.IsNullOrEmpty(n.id) && !string.IsNullOrEmpty(n.message)
                    && n.id != PlayerPrefs.GetString(SeenKey, "") && !IsExpired(n.expiresAt))
                {
                    _announcementId = n.id; Announcement = n.message;
                }
            });
        }

        /// <summary>Call when the player closes the banner so it isn't shown again (tracked per device, not on Jiwe).</summary>
        public void DismissAnnouncement()
        {
            if (!string.IsNullOrEmpty(_announcementId)) { PlayerPrefs.SetString(SeenKey, _announcementId); PlayerPrefs.Save(); }
            Announcement = "";
        }

        /// <summary>An empty or unparseable minimum never blocks anyone.</summary>
        public static bool IsOlderThan(string current, string minimum)
        {
            if (string.IsNullOrWhiteSpace(minimum)) return false;
            return Version.TryParse(current, out var cur) && Version.TryParse(minimum.Trim(), out var min) && cur < min;
        }

        static bool IsExpired(string expiresAt) =>
            !string.IsNullOrEmpty(expiresAt)
            && DateTime.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            && DateTime.UtcNow > t;

        void OnGUI()
        {
            SampleGui.Begin("Update gate + announcements");
            GUILayout.Label($"This build: <b>{Current}</b>   minimum: <b>{(string.IsNullOrEmpty(MinVersion) ? "none" : MinVersion)}</b>");
            if (UpdateRequired)
            {
                GUILayout.Label("<color=orange><b>Please update to keep playing online.</b></color>");
                if (!string.IsNullOrEmpty(updateUrl) && GUILayout.Button("Open store page")) Application.OpenURL(updateUrl);
            }
            if (!string.IsNullOrEmpty(Announcement))
            {
                GUILayout.Label("<b>Announcement:</b> " + Announcement);
                if (GUILayout.Button("Dismiss")) DismissAnnouncement();
            }
            if (GUILayout.Button("Check again")) Check();
            SampleGui.End();
        }
    }
}
