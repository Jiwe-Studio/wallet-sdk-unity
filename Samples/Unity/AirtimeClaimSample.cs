using System;
using Jiwe;
using UnityEngine;

namespace JiweSamples
{
    /// <summary>
    /// STARTER 5 — an airtime milestone (KentePunk: 2,500 XP earns KES 5).
    /// Three things worth copying: re-check the pool right before the claim (the figure on your menu can be
    /// stale), guard against double-taps (this call has no idempotency key), and treat "accepted" as NOT "paid" —
    /// airtime is asynchronous and can still fail.
    ///
    /// Airtime is real money. A shipped client can't enforce a cap, so if a forged call would cost you,
    /// put this behind your own server instead (README section 9, and the server-in-front sample).
    /// </summary>
    [RequireComponent(typeof(JiweAuth), typeof(JiweWallet))]
    public class AirtimeClaimSample : MonoBehaviour
    {
        public int milestoneXp = 2500;
        public int milestoneKes = 5;                       // Jiwe's minimum airtime reward is 5

        public int UnclaimedXp { get; private set; }
        public string Status { get; private set; } = "";
        public bool Claiming { get; private set; }
        public string LastLedgerTransactionId { get; private set; } = "";
        public string LastClaimStatus { get; private set; } = "";

        const string XpKey = "jiwe_sample_unclaimed_xp";
        string _phone = "";
        JiweAuth _auth; JiweWallet _wallet;

        void Awake()
        {
            _auth = GetComponent<JiweAuth>(); _wallet = GetComponent<JiweWallet>();
            if (_wallet.auth == null) _wallet.auth = _auth;
            UnclaimedXp = PlayerPrefs.GetInt(XpKey, 0);
        }

        public bool HasPendingMilestone => UnclaimedXp >= milestoneXp;

        public void AddXp(int amount)
        {
            if (amount <= 0) return;
            UnclaimedXp += amount;
            PlayerPrefs.SetInt(XpKey, UnclaimedXp); PlayerPrefs.Save();
        }

        /// <param name="phone">International format, e.g. 254700000000 — Jiwe's profile has no phone number, so ask the player.</param>
        public void Claim(string phone)
        {
            if (Claiming) return;                                           // double-tap guard
            if (!HasPendingMilestone) { Status = "Keep playing — you haven't reached the milestone yet."; return; }
            if (!_auth.IsLoggedIn) { Status = "Log in to claim."; return; }
            if (string.IsNullOrWhiteSpace(phone)) { Status = "Enter your phone number first."; return; }

            Claiming = true; Status = "Checking the airtime pool…";
            // The pool figure on your menu can be minutes stale. Re-check right before spending the call.
            _wallet.GetWalletBalance(bal =>
            {
                if (bal.Success && bal.AvailableAmount < milestoneKes)
                {
                    Claiming = false;
                    Status = $"The airtime pool is low right now (KES {bal.Available} left) — try again later.";
                    return;
                }
                // If the balance call itself failed, fall through: Jiwe's own answer is still authoritative.

                _wallet.GiveAirtimeReward(milestoneKes, phone.Trim(), "Milestone reward", r =>
                {
                    Claiming = false;
                    if (r.Success)
                    {
                        UnclaimedXp -= milestoneXp;
                        PlayerPrefs.SetInt(XpKey, UnclaimedXp); PlayerPrefs.Save();
                        LastLedgerTransactionId = r.LedgerTransactionId ?? "";
                        Status = "Airtime on its way — we'll confirm when it lands.";   // accepted, NOT paid yet
                    }
                    else Status = "Couldn't claim right now: " + r.Error;
                }, Guid.NewGuid().ToString());
            });
        }

        /// <summary>Poll the claim until it settles (PENDING → POSTED, or FAILED). Call with backoff, not in a tight loop.</summary>
        public void CheckLastClaim()
        {
            if (string.IsNullOrEmpty(LastLedgerTransactionId)) return;
            _wallet.GetTransactionStatus(LastLedgerTransactionId, r =>
            {
                LastClaimStatus = r.Status ?? "";
                Status = LastClaimStatus == "POSTED" ? "Airtime delivered!"
                       : LastClaimStatus == "FAILED" ? "The airtime didn't go through."
                       : "Still processing…";
            });
        }

        void OnGUI()
        {
            SampleGui.Begin("Airtime milestone");
            SampleGui.LoginBar(_auth);
            GUILayout.Label($"XP toward airtime: <b>{UnclaimedXp}</b> / {milestoneXp}");
            if (GUILayout.Button("Earn 500 XP")) AddXp(500);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Phone", GUILayout.Width(60));
            _phone = GUILayout.TextField(_phone);
            GUILayout.EndHorizontal();
            GUI.enabled = !Claiming && HasPendingMilestone;
            if (GUILayout.Button($"Claim KES {milestoneKes}")) Claim(_phone);
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(LastLedgerTransactionId) && GUILayout.Button("Check delivery")) CheckLastClaim();
            if (!string.IsNullOrEmpty(Status)) GUILayout.Label(Status);
            SampleGui.End();
        }
    }
}
