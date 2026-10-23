using System;
using System.Collections;
using System.Linq;
using System.Net.Http;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using JiweSamples;

namespace Jiwe.Tests
{
    /// <summary>The starter samples, driven the way a game would, against the mock Jiwe server.</summary>
    public class SampleTests : JiweTestBase
    {
        [UnitySetUp] public IEnumerator ClearPrefs() { PlayerPrefs.DeleteAll(); yield return null; }

        IEnumerator WaitFor(Func<bool> cond, string what) => WaitUntil(cond, 15f, what);

        // ======================= 0. bootstrap =======================
        [UnityTest]
        public IEnumerator Bootstrap_AppliesCredentials_AndStaysGuestFirst()
        {
            var b = go.AddComponent<JiweBootstrap>();
            var cfg = ScriptableObject.CreateInstance<JiweCredentialsConfig>();
            cfg.clientId = "cfg-client"; cfg.apiSecret = "cfg-secret"; cfg.walletResource = "https://w/r"; cfg.apiUsername = "cfg-user"; cfg.apiKey = "cfg-key";
            Assert.IsTrue(b.ApplyConfig(cfg));
            Assert.AreEqual("cfg-client", auth.clientId); Assert.AreEqual("cfg-secret", auth.apiSecret);
            Assert.AreEqual("https://w/r", auth.walletResource);
            Assert.AreEqual("cfg-user", wallet.apiUsername); Assert.AreEqual("cfg-key", wallet.apiKey);
            Assert.AreSame(auth, wallet.auth);
            Assert.IsFalse(auth.loginOnStart, "guest-first: nothing calls Jiwe until the player opts in");
            Assert.IsNull(capturedAuthUrl);
            var attr = typeof(JiweBootstrap).GetCustomAttributes(typeof(DefaultExecutionOrder), false).Cast<DefaultExecutionOrder>().Single();
            Assert.AreEqual(-100, attr.order, "must run before JiweAuth.Awake resumes a WebGL login");
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_WithNoConfigAsset_KeepsInspectorValues()
        {
            var b = go.AddComponent<JiweBootstrap>();
            Assert.IsTrue(b.ApplyConfig(null), "clientId was already set on the component");
            Assert.AreEqual("client-1", auth.clientId);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_NotConfigured_BeginSignIn_FailsFast_WithoutOpeningABrowser()
        {
            auth.clientId = "";
            var b = go.AddComponent<JiweBootstrap>();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("No Jiwe credentials"));
            b.ApplyConfig(null);
            Assert.IsFalse(b.IsConfigured);
            string failure = null; b.BeginSignIn(() => Assert.Fail("must not succeed"), m => failure = m);
            Assert.IsNotNull(failure); StringAssert.Contains("isn't set up", failure);
            Assert.IsNull(capturedAuthUrl, "no browser to a blank client_id");
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_SignIn_Success_AndCancel()
        {
            var b = go.AddComponent<JiweBootstrap>();
            b.ApplyConfig(null);
            bool ok = false; b.BeginSignIn(() => ok = true, m => Assert.Fail(m));
            yield return WaitFor(() => capturedAuthUrl != null, "login URL");
            BrowserRedirect("code-boot", QueryParam(capturedAuthUrl, "state"));
            yield return WaitFor(() => ok, "sign-in");
            Assert.IsTrue(b.IsLoggedIn);

            // a second sign-in, cancelled
            auth.loginOnStart = false;
            string fail = null; capturedAuthUrl = null;
            b.BeginSignIn(() => { }, m => fail = m);
            yield return WaitFor(() => capturedAuthUrl != null, "second login URL");
            b.CancelSignIn();
            yield return WaitFor(() => fail != null, "cancel to surface");
            StringAssert.Contains("cancel", fail.ToLowerInvariant());
        }

        [UnityTest]
        public IEnumerator Bootstrap_AwardXp_NoOpsForGuests_AndCreditsOnceSignedIn()
        {
            var b = go.AddComponent<JiweBootstrap>(); b.ApplyConfig(null);
            bool? guestResult = null; b.AwardXp(10, "x", ok => guestResult = ok);
            Assert.AreEqual(false, guestResult); Assert.AreEqual(0, Requests("/rewards/xp").Count);

            yield return LoginOk();
            bool? result = null; b.AwardXp(10, "x", ok => result = ok);
            yield return WaitFor(() => result.HasValue, "xp award");
            Assert.AreEqual(true, result); Assert.AreEqual(1, Requests("/rewards/xp").Count);
        }

        // ======================= 1. XP + leaderboard =======================
        [UnityTest]
        public IEnumerator Xp_Guest_CountsLocally_ButSendsNothing()
        {
            var s = go.AddComponent<XpAndLeaderboardSample>();
            s.ReportRun(100, "solo");
            Assert.AreEqual(100, s.SessionXp); StringAssert.Contains("Log in", s.Status);
            Assert.AreEqual(0, Requests("/rewards/xp").Count);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Xp_UpdateRequired_BlocksTheCall_EvenWhenLoggedIn()
        {
            yield return LoginOk();
            var s = go.AddComponent<XpAndLeaderboardSample>(); s.updateRequired = true;
            s.ReportRun(100, "solo");
            Assert.AreEqual(100, s.SessionXp); StringAssert.Contains("Update", s.Status);
            Assert.AreEqual(0, Requests("/rewards/xp").Count);
        }

        [UnityTest]
        public IEnumerator Xp_LoggedIn_SyncsTheRun()
        {
            yield return LoginOk();
            var s = go.AddComponent<XpAndLeaderboardSample>();
            s.ReportRun(120, "rumble");
            yield return WaitFor(() => s.Status == "Synced.", "sync");
            var req = Requests("/rewards/xp").Single();
            StringAssert.Contains("\"xpEarned\":120", req.body); StringAssert.Contains("rumble", req.body);
        }

        [UnityTest]
        public IEnumerator Xp_FailureShowsAFriendlyMessage_AndKeepsTheLocalCount()
        {
            yield return LoginOk();
            Faults("[{\"match\":\"rewards/xp\",\"status\":500,\"count\":1}]");
            var s = go.AddComponent<XpAndLeaderboardSample>();
            s.ReportRun(50, "solo");
            yield return WaitFor(() => s.Status.StartsWith("Couldn't sync"), "failure message");
            Assert.AreEqual(50, s.SessionXp);
        }

        [UnityTest]
        public IEnumerator Leaderboard_IsSortedByScore_NotByTheServersOrder()
        {
            var s = go.AddComponent<XpAndLeaderboardSample>();
            s.RefreshLeaderboard("week");
            yield return WaitFor(() => s.Board.Count == 3, "board");
            CollectionAssert.AreEqual(new[] { "Ann", "Cy", "Bob" }, s.Board.Select(e => e.name).ToArray());
            CollectionAssert.AreEqual(new[] { 900, 500, 300 }, s.Board.Select(e => e.Score).ToArray());
            s.RefreshLeaderboard(null);
            yield return WaitFor(() => s.Board.Count == 2, "all-time board");
            Assert.AreEqual("Ann", s.Board[0].name); Assert.AreEqual(1641, s.Board[0].Score);
        }

        // ======================= 2. update gate =======================
        UpdateGateSample Gate(string version) { var g = go.AddComponent<UpdateGateSample>(); g.checkOnStart = false; g.currentVersionOverride = version; return g; }

        [UnityTest]
        public IEnumerator Gate_NothingPublished_BlocksNobody()
        {
            var g = Gate("1.0"); g.Check();
            yield return WaitFor(() => g.Checked, "check");
            Assert.IsFalse(g.UpdateRequired); Assert.IsEmpty(g.Announcement);
        }

        [UnityTest]
        public IEnumerator Gate_MinVersion_UsesRealVersionComparison_NotStringComparison()
        {
            wallet.SetGameData("{\"gameData\":{\"minVersion\":\"1.7\"}}", _ => { });
            yield return WaitFor(() => Requests("update/game-data").Count == 1, "write");
            foreach (var (current, blocked) in new[] { ("1.6", true), ("1.6.9", true), ("1.7", false), ("1.10", false), ("2.0", false) })
            {
                Destroy(go.GetComponent<UpdateGateSample>());
                var g = Gate(current); g.Check();
                yield return WaitFor(() => g.Checked, "check " + current);
                Assert.AreEqual(blocked, g.UpdateRequired, "build " + current + " vs minimum 1.7");
            }
        }

        [Test]
        public void Gate_IsOlderThan_Table()
        {
            Assert.IsTrue(UpdateGateSample.IsOlderThan("1.6", "1.7"));
            Assert.IsTrue(UpdateGateSample.IsOlderThan("1.6.5", "1.7"));
            Assert.IsFalse(UpdateGateSample.IsOlderThan("1.7", "1.7"));
            Assert.IsFalse(UpdateGateSample.IsOlderThan("1.10", "1.9"), "1.10 is newer than 1.9 — a string compare gets this wrong");
            Assert.IsFalse(UpdateGateSample.IsOlderThan("abc", "1.7"), "unreadable build version never blocks");
            Assert.IsFalse(UpdateGateSample.IsOlderThan("1.6", ""));
            Assert.IsFalse(UpdateGateSample.IsOlderThan("1.6", null));
            Assert.IsFalse(UpdateGateSample.IsOlderThan("1.6", "garbage"), "an unreadable minimum never blocks");
        }

        [UnityTest]
        public IEnumerator Gate_RotatedKey_RequiresAnUpdate()
        {
            wallet.apiKey = "rotated-out";
            var g = Gate("1.0"); g.Check();
            yield return WaitFor(() => g.UpdateRequired, "rotation to be detected");
        }

        [UnityTest]
        public IEnumerator Gate_TransientFailures_NeverBlock()
        {
            Faults("[{\"match\":\"account-balance\",\"status\":500,\"count\":1},{\"match\":\"game-data\",\"drop\":true,\"count\":1}]");
            var g = Gate("0.1"); g.Check();
            yield return new WaitForSeconds(1.5f);
            Assert.IsFalse(g.UpdateRequired, "a 500 and a dropped connection must fail open");
        }

        [UnityTest]
        public IEnumerator Gate_Announcement_ShowsOnce_ThenNewIdShowsAgain_ExpiredNever()
        {
            string pub(string id, string exp) => "{\"gameData\":{\"announcement\":{\"id\":\"" + id + "\",\"message\":\"Double XP \" " + "," + "\"expiresAt\":\"" + exp + "\"}}}";
            wallet.SetGameData("{\"gameData\":{\"announcement\":{\"id\":\"evt-1\",\"message\":\"Double XP\",\"expiresAt\":\"2099-01-01T00:00:00Z\"}}}", _ => { });
            yield return WaitFor(() => Requests("update/game-data").Count == 1, "publish");
            var g = Gate("1.0"); g.Check();
            yield return WaitFor(() => g.Announcement == "Double XP", "announcement");
            g.DismissAnnouncement();
            Assert.IsEmpty(g.Announcement);

            Destroy(go.GetComponent<UpdateGateSample>()); g = Gate("1.0"); g.Check();
            yield return WaitFor(() => g.Checked, "second check");
            Assert.IsEmpty(g.Announcement, "already seen on this device");

            wallet.SetGameData("{\"gameData\":{\"announcement\":{\"id\":\"evt-2\",\"message\":\"New event\",\"expiresAt\":\"2099-01-01T00:00:00Z\"}}}", _ => { });
            yield return WaitFor(() => Requests("update/game-data").Count == 2, "publish 2");
            Destroy(go.GetComponent<UpdateGateSample>()); g = Gate("1.0"); g.Check();
            yield return WaitFor(() => g.Announcement == "New event", "new announcement");

            g.DismissAnnouncement();
            wallet.SetGameData("{\"gameData\":{\"announcement\":{\"id\":\"evt-3\",\"message\":\"Old news\",\"expiresAt\":\"2020-01-01T00:00:00Z\"}}}", _ => { });
            yield return WaitFor(() => Requests("update/game-data").Count == 3, "publish 3");
            Destroy(go.GetComponent<UpdateGateSample>()); g = Gate("1.0"); g.Check();
            yield return WaitFor(() => g.Checked, "third check");
            Assert.IsEmpty(g.Announcement, "an expired announcement never shows");
        }

        // ======================= 3. cosmetic shop =======================
        [UnityTest]
        public IEnumerator Shop_LoggedOut_AsksToLogIn()
        {
            var s = go.AddComponent<CosmeticShopSample>(); s.Buy(s.items[0]);
            StringAssert.Contains("Log in", s.Status); Assert.AreEqual(0, Requests("/purchases/wallet").Count);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Shop_Success_UnlocksAndClearsTheKey()
        {
            yield return LoginOk();
            var s = go.AddComponent<CosmeticShopSample>(); var item = s.items[0];
            s.Buy(item);
            yield return WaitFor(() => s.IsOwned(item.itemId), "unlock");
            Assert.IsFalse(s.HasPendingAttempt(item.itemId), "a settled purchase releases its key");
            Assert.AreEqual(1000 - item.priceCowrie, State().player_balance);
            var req = Requests("/purchases/wallet", "POST").Single();
            StringAssert.Contains("\"itemId\":\"" + item.itemId + "\"", req.body);
            Assert.IsNotEmpty(Header(req, "idempotency-key"));
        }

        [UnityTest]
        public IEnumerator Shop_AmbiguousFailure_KeepsTheSameKey_AndRetryChargesOnce()
        {
            yield return LoginOk();
            var s = go.AddComponent<CosmeticShopSample>(); var item = s.items[0];
            Faults("[{\"match\":\"purchases/wallet\",\"status\":503,\"count\":1}]");
            s.Buy(item);
            yield return WaitFor(() => !s.Busy && s.Status.Contains("won't be charged twice"), "retry prompt");
            Assert.IsTrue(s.HasPendingAttempt(item.itemId), "the key is kept through an ambiguous failure");

            s.Buy(item);
            yield return WaitFor(() => s.IsOwned(item.itemId), "unlock on retry");
            var reqs = Requests("/purchases/wallet", "POST");
            Assert.AreEqual(2, reqs.Count);
            Assert.AreEqual(Header(reqs[0], "idempotency-key"), Header(reqs[1], "idempotency-key"), "retry must reuse the SAME key");
            Assert.AreEqual(1, State().purchase_count); Assert.AreEqual(1000 - item.priceCowrie, State().player_balance);
        }

        [UnityTest]
        public IEnumerator Shop_DefinitiveRejection_DropsTheKey_SoALaterAttemptIsANewPurchase()
        {
            yield return LoginOk();
            var s = go.AddComponent<CosmeticShopSample>(); var item = s.items[0];
            Config("{\"player_balance\":10}");
            s.Buy(item);
            yield return WaitFor(() => !s.Busy && s.Status.Contains("nsufficient"), "rejection");
            Assert.IsFalse(s.HasPendingAttempt(item.itemId), "a definitive failure must not keep its key");
            Assert.IsFalse(s.IsOwned(item.itemId));

            Config("{\"player_balance\":1000}");          // the player tops up
            s.Buy(item);
            yield return WaitFor(() => s.IsOwned(item.itemId), "unlock after top-up");
            var reqs = Requests("/purchases/wallet", "POST");
            Assert.AreNotEqual(Header(reqs[0], "idempotency-key"), Header(reqs[1], "idempotency-key"), "a new key, or the old rejection would replay forever");
        }

        [UnityTest]
        public IEnumerator Shop_PurchaseNeedingApproval_IsNotUnlocked_AndKeepsItsKey()
        {
            yield return LoginOk();
            Config("{\"requires_approval\":true}");
            var s = go.AddComponent<CosmeticShopSample>(); var item = s.items[0];
            s.Buy(item);
            yield return WaitFor(() => !s.Busy && s.Status.Contains("not paid yet"), "pending message");
            Assert.IsFalse(s.IsOwned(item.itemId)); Assert.IsTrue(s.HasPendingAttempt(item.itemId));
        }

        [UnityTest]
        public IEnumerator Shop_DoubleTap_SendsOnePurchase()
        {
            yield return LoginOk();
            var s = go.AddComponent<CosmeticShopSample>(); var item = s.items[0];
            s.Buy(item); s.Buy(item); s.Buy(item);
            yield return WaitFor(() => s.IsOwned(item.itemId), "unlock");
            Assert.AreEqual(1, Requests("/purchases/wallet", "POST").Count);
        }

        // ======================= 4. cloud save =======================
        [UnityTest]
        public IEnumerator Save_LoggedOut_StillSavesLocally()
        {
            var s = go.AddComponent<CloudSaveSample>(); s.LevelUp();
            Assert.AreEqual(1, s.Local.levelUnlocked); StringAssert.Contains("Log in", s.Status);
            Assert.AreEqual(0, Requests("/metadata/update/user-data").Count);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Save_RoundTripsToANewDevice()
        {
            yield return LoginOk();
            var s = go.AddComponent<CloudSaveSample>();
            s.LevelUp(); s.LevelUp(); s.LevelUp();
            yield return WaitFor(() => Requests("/metadata/update/user-data").Count == 3 && s.Status.StartsWith("Backed up"), "backups");
            var last = Requests("/metadata/update/user-data").Last().body;
            StringAssert.Contains("\"userData\"", last); StringAssert.Contains("\"levelUnlocked\":3", last); StringAssert.Contains("\"coins\":30", last);

            PlayerPrefs.DeleteAll(); Destroy(s);        // a new device: nothing saved locally
            yield return null;
            var fresh = go.AddComponent<CloudSaveSample>();
            Assert.AreEqual(0, fresh.Local.levelUnlocked);
            fresh.PullFromCloud();
            yield return WaitFor(() => fresh.Local.levelUnlocked == 3, "restore");
            Assert.AreEqual(30, fresh.Local.coins); StringAssert.Contains("Restored", fresh.Status);
        }

        [UnityTest]
        public IEnumerator Save_PullDoesNotOverwriteAHigherLocalSave()
        {
            yield return LoginOk();
            var s = go.AddComponent<CloudSaveSample>();
            s.LevelUp();
            yield return WaitFor(() => s.Status.StartsWith("Backed up"), "first backup");
            for (int i = 0; i < 4; i++) { s.Local.levelUnlocked++; }     // the device got ahead without syncing
            s.PullFromCloud();
            yield return WaitFor(() => s.Status.Contains("up to date"), "pull result");
            Assert.AreEqual(5, s.Local.levelUnlocked);
        }

        [UnityTest]
        public IEnumerator Save_BackupFailure_KeepsTheLocalProgress()
        {
            yield return LoginOk();
            Faults("[{\"match\":\"update/user-data\",\"status\":500,\"count\":1}]");
            var s = go.AddComponent<CloudSaveSample>(); s.LevelUp();
            yield return WaitFor(() => s.Status.Contains("still saved"), "failure message");
            Assert.AreEqual(1, s.Local.levelUnlocked);
        }

        [UnityTest]
        public IEnumerator Save_NeverSavedPlayer_PullsNothingGracefully()
        {
            yield return LoginOk();
            var s = go.AddComponent<CloudSaveSample>(); s.PullFromCloud();
            yield return WaitFor(() => !string.IsNullOrEmpty(s.Status), "pull");
            Assert.AreEqual(0, s.Local.levelUnlocked);
        }

        // ======================= 5. airtime claim =======================
        [UnityTest]
        public IEnumerator Airtime_NotEligibleLoggedOutOrNoPhone_SendsNothing()
        {
            var s = go.AddComponent<AirtimeClaimSample>();
            s.Claim("254700000000"); StringAssert.Contains("milestone", s.Status);
            s.AddXp(2500); s.Claim("254700000000"); StringAssert.Contains("Log in", s.Status);
            yield return LoginOk();
            s.Claim("  "); StringAssert.Contains("phone", s.Status);
            Assert.AreEqual(0, Requests("/rewards/airtime").Count);
        }

        [UnityTest]
        public IEnumerator Airtime_Claim_ChecksPool_Pays_AndConfirmsDelivery()
        {
            yield return LoginOk();
            var s = go.AddComponent<AirtimeClaimSample>(); s.AddXp(2600);
            s.Claim("254700000000");
            yield return WaitFor(() => !s.Claiming && s.LastLedgerTransactionId.Length > 0, "claim");
            Assert.AreEqual(100, s.UnclaimedXp, "the milestone's XP is spent");
            var body = Requests("/rewards/airtime", "POST").Single().body;
            StringAssert.Contains("\"airtimeReward\":5", body); StringAssert.Contains("254700000000", body);
            Assert.AreEqual(1, Requests("account-balance").Count, "the pool is re-checked right before the claim");
            StringAssert.Contains("on its way", s.Status);

            s.CheckLastClaim();
            yield return WaitFor(() => s.LastClaimStatus == "PENDING", "first status");
            s.CheckLastClaim();
            yield return WaitFor(() => s.LastClaimStatus == "POSTED", "second status");
            StringAssert.Contains("delivered", s.Status);
        }

        [UnityTest]
        public IEnumerator Airtime_LowPool_ExplainsAndSpendsNoXp()
        {
            yield return LoginOk();
            Config("{\"balance\":3.0}");
            var s = go.AddComponent<AirtimeClaimSample>(); s.AddXp(2500);
            s.Claim("254700000000");
            yield return WaitFor(() => !s.Claiming && s.Status.Contains("pool is low"), "low pool message");
            Assert.AreEqual(2500, s.UnclaimedXp); Assert.AreEqual(0, Requests("/rewards/airtime").Count);
        }

        [UnityTest]
        public IEnumerator Airtime_IfTheBalanceCheckFails_TheClaimStillProceeds()
        {
            yield return LoginOk();
            Faults("[{\"match\":\"account-balance\",\"status\":500,\"count\":1}]");
            var s = go.AddComponent<AirtimeClaimSample>(); s.AddXp(2500);
            s.Claim("254700000000");
            yield return WaitFor(() => !s.Claiming && s.LastLedgerTransactionId.Length > 0, "claim despite the failed check");
            Assert.AreEqual(1, Requests("/rewards/airtime").Count);
        }

        [UnityTest]
        public IEnumerator Airtime_DoubleTap_PaysOnce()
        {
            yield return LoginOk();
            var s = go.AddComponent<AirtimeClaimSample>(); s.AddXp(5000);
            s.Claim("254700000000"); s.Claim("254700000000");
            yield return WaitFor(() => !s.Claiming && s.LastLedgerTransactionId.Length > 0, "claim");
            Assert.AreEqual(1, Requests("/rewards/airtime").Count); Assert.AreEqual(2500, s.UnclaimedXp);
        }

        [UnityTest]
        public IEnumerator Airtime_Failure_KeepsTheXp()
        {
            yield return LoginOk();
            Faults("[{\"match\":\"rewards/airtime\",\"status\":500,\"count\":1}]");
            var s = go.AddComponent<AirtimeClaimSample>(); s.AddXp(2500);
            s.Claim("254700000000");
            yield return WaitFor(() => !s.Claiming && s.Status.StartsWith("Couldn't claim"), "failure");
            Assert.AreEqual(2500, s.UnclaimedXp, "a failed claim must not spend the player's XP");
        }

        // ======================= 6. server in front of Jiwe =======================
        static string NodePort => Environment.GetEnvironmentVariable("JIWE_NODE_PORT");
        static readonly HttpClient NodeHttp = new HttpClient();

        ServerRewardSample Server()
        {
            if (string.IsNullOrEmpty(NodePort)) Assert.Ignore("JIWE_NODE_PORT not set — run Tests~/run-tests.sh, which starts the Node sample server");
            using (var r = NodeHttp.PostAsync($"http://127.0.0.1:{NodePort}/api/_reset", new StringContent("{}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult()) { }
            var s = go.AddComponent<ServerRewardSample>(); s.serverUrl = $"http://127.0.0.1:{NodePort}";
            return s;
        }

        [UnityTest]
        public IEnumerator Server_LoggedOut_AsksToLogIn()
        {
            var s = Server(); s.ClaimRunReward(100);
            StringAssert.Contains("Log in", s.Status); Assert.AreEqual(0, Requests("/rewards/xp").Count);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Server_AppliesTheCap_AndCreditsOnJiwe_WithThePlayersOwnToken()
        {
            yield return LoginOk();
            var s = Server();                        // cap 300/day, 200/run (set by run-tests.sh)
            s.ClaimRunReward(150); yield return WaitFor(() => !s.Busy && s.LastAwarded == 150, "first claim");
            s.ClaimRunReward(150); yield return WaitFor(() => !s.Busy && s.LastAwarded == 150 && Requests("/rewards/xp").Count == 2, "second claim");
            s.ClaimRunReward(150); yield return WaitFor(() => !s.Busy && s.CapReached, "cap reached");
            Assert.AreEqual(0, s.LastAwarded); StringAssert.Contains("cap", s.Status);
            Assert.AreEqual(2, Requests("/rewards/xp").Count, "nothing is sent to Jiwe once the cap is hit");
            var req = Requests("/rewards/xp").First();
            Assert.AreEqual("test-user", Header(req, "x-api-username"));
            Assert.AreEqual(auth.IdToken, Header(req, "x-api-token"), "Jiwe credits whoever the token names");
        }

        [UnityTest]
        public IEnumerator Server_TheCapIsPerPlayer_NotSharedAcrossPlayers()
        {
            yield return LoginOk();
            var s = Server();
            s.ClaimRunReward(200); yield return WaitFor(() => !s.Busy && s.LastAwarded == 200, "A's first claim");
            s.ClaimRunReward(200); yield return WaitFor(() => !s.Busy && s.LastAwarded == 100, "A's claim hits the cap");
            s.ClaimRunReward(100); yield return WaitFor(() => !s.Busy && s.CapReached, "A is capped");

            Config("{\"claims\":{\"sub\":\"player-999\"}}");     // a different player signs in
            yield return LoginOk();
            s.ClaimRunReward(150);
            yield return WaitFor(() => !s.Busy && s.LastAwarded == 150, "B gets their own, full cap");
            Assert.IsFalse(s.CapReached);
        }

        [UnityTest]
        public IEnumerator Server_RejectsAnOutOfRangeScore_AsNotRetryable()
        {
            yield return LoginOk();
            var s = Server();
            s.ClaimRunReward(5000);
            yield return WaitFor(() => !s.Busy && s.Status.Length > 0 && !s.Status.StartsWith("Saving"), "rejection");
            StringAssert.Contains("score", s.Status); Assert.IsFalse(s.LastFailureRetryable);
            Assert.AreEqual(0, Requests("/rewards/xp").Count);
        }

        [UnityTest]
        public IEnumerator Server_WhenJiweFails_ReleasesTheReservation_SoTheRetryIsPaidInFull()
        {
            yield return LoginOk();
            var s = Server();
            Faults("[{\"match\":\"rewards/xp\",\"status\":500,\"count\":1}]");
            s.ClaimRunReward(200);
            yield return WaitFor(() => !s.Busy && s.LastFailureRetryable, "retryable failure");
            Assert.AreEqual(0, s.LastAwarded);

            s.ClaimRunReward(200);
            yield return WaitFor(() => !s.Busy && s.LastAwarded == 200, "retry succeeds");
            s.ClaimRunReward(200);
            yield return WaitFor(() => !s.Busy && s.LastAwarded == 100, "only the rest of the cap is left");
            Assert.AreEqual(300 - 200, s.LastAwarded, "the failed attempt did NOT eat into the cap");
        }

        [UnityTest]
        public IEnumerator Server_Unreachable_IsReportedAsRetryable()
        {
            yield return LoginOk();
            var s = Server(); s.serverUrl = $"http://127.0.0.1:{FreePort()}";
            s.ClaimRunReward(100);
            yield return WaitFor(() => !s.Busy && s.Status.Length > 0 && !s.Status.StartsWith("Saving"), "unreachable");
            Assert.IsTrue(s.LastFailureRetryable);
        }

        static void Destroy(UnityEngine.Object o) => UnityEngine.Object.Destroy(o);
    }
}
