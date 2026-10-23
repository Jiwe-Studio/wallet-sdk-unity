using System.Collections.Generic;
using Jiwe;
using UnityEngine;

namespace JiweSamples
{
    /// <summary>
    /// STARTER 1 — XP per run + a leaderboard (KentePunk's pattern).
    /// The visible XP counter works for everyone, logged in or not; only the Jiwe call is gated.
    /// Add this to a GameObject (it adds JiweAuth and JiweWallet), fill in credentials, press Play.
    /// </summary>
    [RequireComponent(typeof(JiweAuth), typeof(JiweWallet))]
    public class XpAndLeaderboardSample : MonoBehaviour
    {
        [Tooltip("Set true (from your own update check, or UpdateGateSample) to stop syncing from an outdated build.")]
        public bool updateRequired;

        public int SessionXp { get; private set; }
        public string Status { get; private set; } = "";
        public List<JiweLeaderboardEntry> Board { get; private set; } = new List<JiweLeaderboardEntry>();

        JiweAuth _auth; JiweWallet _wallet;

        void Awake()
        {
            _auth = GetComponent<JiweAuth>(); _wallet = GetComponent<JiweWallet>();
            if (_wallet.auth == null) _wallet.auth = _auth;
        }

        /// <summary>Call when a run ends.</summary>
        public void ReportRun(int score, string mode)
        {
            SessionXp += score;                                   // counts whether or not the player is logged in

            // Checked BEFORE the login gate: an outdated build's own API key can be rejected whether or not anyone is logged in.
            if (updateRequired) { Status = "Update the app to keep syncing XP."; return; }
            if (!_auth.IsLoggedIn) { Status = "Log in with Jiwe to save this and appear on the leaderboard."; return; }

            Status = "Syncing to the Jiwe leaderboard…";
            _wallet.GiveXpReward(score, $"My Game {mode} run", r =>
                Status = r.Success ? "Synced." : $"Couldn't sync right now ({r.Error}). Try again next time.");
        }

        /// <param name="period">"day", "week", "month", "year", or null for all-time.</param>
        public void RefreshLeaderboard(string period)
        {
            Status = "Loading leaderboard…";
            _wallet.GetLeaderboard("xp", 20, period, "highest", result =>
            {
                if (!result.Success) { Status = "Couldn't load the leaderboard: " + result.Error; return; }
                var rows = new List<JiweLeaderboardEntry>(result.Entries);
                // The server's own order and rank are not trustworthy, and a period board sends its score
                // as uppercase "XP" — entry.Score reads whichever field is set. Sort it yourself.
                rows.Sort((a, b) => b.Score.CompareTo(a.Score));
                Board = rows;
                Status = rows.Count == 0 ? "No scores yet — be the first." : "";
            });
        }

        void OnGUI()
        {
            SampleGui.Begin("XP + leaderboard");
            SampleGui.LoginBar(_auth);
            GUILayout.Label($"Session XP: <b>{SessionXp}</b>");
            if (GUILayout.Button("Finish a run (+100 XP)")) ReportRun(100, "solo");
            if (GUILayout.Button("Show this week's leaderboard")) RefreshLeaderboard("week");
            if (!string.IsNullOrEmpty(Status)) GUILayout.Label(Status);
            for (int i = 0; i < Board.Count; i++) GUILayout.Label($"{i + 1}. {Board[i].name}   {Board[i].Score}");
            SampleGui.End();
        }
    }
}
