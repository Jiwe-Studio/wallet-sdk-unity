using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Jiwe.Tests
{
    /// <summary>
    /// READ-ONLY checks against the REAL Jiwe API — the only tests here that can tell you Jiwe still behaves the way the
    /// mock models it. Skipped unless you provide credentials in the environment (nothing is read from your project,
    /// and nothing is ever written):
    ///
    ///   JIWE_LIVE_API_USERNAME=... JIWE_LIVE_API_KEY=... Tests~/run-tests.sh
    ///   (optional) JIWE_LIVE_BASE_URL=https://abacus.jiwe.io/rest   — point at a non-production environment instead
    ///
    /// It cannot test login (that needs a person in a browser) or anything that moves value.
    /// </summary>
    public class LiveSmokeTests
    {
        static string Env(string k) { var v = Environment.GetEnvironmentVariable(k); return string.IsNullOrEmpty(v) ? null : v; }

        [UnityTest]
        public IEnumerator Live_ReadOnly_Smoke()
        {
            string user = Env("JIWE_LIVE_API_USERNAME"), key = Env("JIWE_LIVE_API_KEY");
            if (user == null || key == null) Assert.Ignore("set JIWE_LIVE_API_USERNAME and JIWE_LIVE_API_KEY to run the read-only live smoke test");

            var go = new GameObject("jiwe-live-smoke"); go.SetActive(false);
            var auth = go.AddComponent<JiweAuth>(); auth.loginOnStart = false;
            var wallet = go.AddComponent<JiweWallet>();
            wallet.auth = auth; wallet.apiUsername = user; wallet.apiKey = key;
            if (Env("JIWE_LIVE_BASE_URL") != null) wallet.walletBaseUrl = Env("JIWE_LIVE_BASE_URL");
            go.SetActive(true);
            try
            {
                var valid = new Box<bool>(); wallet.CheckCredentialsValid(valid.Callback); yield return valid.Wait(30);
                Assert.IsTrue(valid.Value, "these credentials were rejected (401/403) — wrong environment, or rotated?");

                var bal = new Box<JiweWalletBalanceResult>(); wallet.GetWalletBalance(bal.Callback); yield return bal.Wait(30);
                Assert.IsTrue(bal.Value.Success, "balance: " + bal.Value.Error);
                Debug.Log($"[live] app wallet: available {bal.Value.AvailableAmount} (debits {bal.Value.DebitsAmount}, credits {bal.Value.CreditsAmount})");

                var gd = new Box<JiweWalletResult>(); wallet.GetGameData(gd.Callback); yield return gd.Wait(30);
                Assert.IsTrue(gd.Value.Success, "game data: " + gd.Value.Error);
                StringAssert.Contains("gameData", gd.Value.RawResponse, "the read envelope should wrap your fields in \"gameData\"");

                foreach (string period in new string[] { null, "week" })
                {
                    var lb = new Box<JiweLeaderboardResult>(); wallet.GetLeaderboard("xp", 5, period, "highest", lb.Callback); yield return lb.Wait(30);
                    Assert.IsTrue(lb.Value.Success, $"leaderboard ({period ?? "all-time"}): " + lb.Value.Error);
                    foreach (var e in lb.Value.Entries) Assert.GreaterOrEqual(e.Score, 0);
                    Debug.Log($"[live] leaderboard {(period ?? "all-time")}: {lb.Value.Entries.Length} rows" +
                              (lb.Value.Entries.Length > 0 ? $", first raw fields XP={lb.Value.Entries[0].XP} currentXP={lb.Value.Entries[0].currentXP} xp={lb.Value.Entries[0].xp} -> Score {lb.Value.Entries[0].Score}" : ""));
                }
            }
            finally { UnityEngine.Object.Destroy(go); }
        }
    }
}
