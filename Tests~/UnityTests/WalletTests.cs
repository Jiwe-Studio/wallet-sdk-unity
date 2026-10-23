using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Jiwe.Tests
{
    [Serializable] class UserDataEnvelope { public string type; public SaveData userData; }
    [Serializable] class SaveData { public int levelUnlocked; public string journey; public int extra; }
    [Serializable] class GameDataEnvelope { public string type; public GameDataBody gameData; }
    [Serializable] class GameDataBody { public string minVersion; public string note; }

    /// <summary>JiweWallet against the mock: request shapes, headers, response parsing and failure classification.</summary>
    public class WalletTests : JiweTestBase
    {
        // ---- guards ------------------------------------------------------------------
        [UnityTest]
        public IEnumerator PlayerCalls_WhenLoggedOut_SendNothing_AndSayWhy()
        {
            var xp = new Box<JiweWalletResult>(); wallet.GiveXpReward(5, "d", xp.Callback); yield return xp.Wait();
            var buy = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(10, "i", "d", "key", buy.Callback); yield return buy.Wait();
            var earned = new Box<JiweRewardsEarnedResult>(); wallet.GetRewardsEarned(earned.Callback); yield return earned.Wait();
            var bearer = new Box<JiweRewardsEarnedResult>(); wallet.GetRewardsEarnedBearer(bearer.Callback); yield return bearer.Wait();
            var ud = new Box<JiweWalletResult>(); wallet.GetUserData(ud.Callback); yield return ud.Wait();
            var sd = new Box<JiweWalletResult>(); wallet.SetUserData("{}", sd.Callback); yield return sd.Wait();

            foreach (var r in new[] { xp.Value, ud.Value, sd.Value })
            {
                Assert.IsFalse(r.Success); StringAssert.Contains("Not logged in", r.Error);
                Assert.AreEqual(0, r.HttpStatus); Assert.IsFalse(r.Retryable, "nothing was sent, so there is nothing to retry");
            }
            Assert.IsFalse(buy.Value.Success); StringAssert.Contains("Not logged in", buy.Value.Error); Assert.IsFalse(buy.Value.Retryable);
            Assert.IsFalse(earned.Value.Success); Assert.IsFalse(bearer.Value.Success);
            Assert.AreEqual(0, Log().Count, "no request may reach Jiwe while logged out");
        }

        // ---- rewards -----------------------------------------------------------------
        [UnityTest]
        public IEnumerator GiveXp_SendsApiKeyAndIdToken_AndTheDocumentedBody()
        {
            yield return LoginOk();
            var r = new Box<JiweWalletResult>(); wallet.GiveXpReward(25, "Completed level 4", r.Callback); yield return r.Wait();

            Assert.IsTrue(r.Value.Success, r.Value.Error);
            Assert.AreEqual(200, r.Value.HttpStatus);
            Assert.IsNotEmpty(r.Value.LedgerTransactionId);
            var req = Requests("/rewards/xp", "POST").Single();
            Assert.AreEqual("test-user", Header(req, "x-api-username"));
            Assert.AreEqual("test-key", Header(req, "x-api-key"));
            Assert.AreEqual(auth.IdToken, Header(req, "x-api-token"), "default token is the ID token (what shipped games send)");
            StringAssert.Contains("\"xpEarned\":25", req.body);
            StringAssert.Contains("\"description\":\"Completed level 4\"", req.body);
            StringAssert.Contains("\"gameSessionId\":\"" + wallet.GameSessionId + "\"", req.body);
            StringAssert.Contains("\"metadata\":{}", req.body);
            StringAssert.DoesNotContain("transactionId", req.body, "XP examples carry no transactionId unless you pass one");
        }

        [UnityTest]
        public IEnumerator GiveXp_WithATransactionId_SendsIt()
        {
            yield return LoginOk();
            var r = new Box<JiweWalletResult>(); wallet.GiveXpReward(5, "d", r.Callback, "my-ref-1"); yield return r.Wait();
            StringAssert.Contains("\"transactionId\":\"my-ref-1\"", Requests("/rewards/xp", "POST").Single().body);
        }

        [UnityTest]
        public IEnumerator GiveXp_Zero_IsRejected_NotRetryable()
        {
            yield return LoginOk();
            var r = new Box<JiweWalletResult>(); wallet.GiveXpReward(0, "d", r.Callback); yield return r.Wait();
            Assert.IsFalse(r.Value.Success); Assert.AreEqual(400, r.Value.HttpStatus); Assert.IsFalse(r.Value.Retryable);
            StringAssert.Contains("xpEarned", r.Value.Error);
        }

        [UnityTest]
        public IEnumerator Airtime_IsAcceptedNotSettled_ThenSettlesViaTransactionStatus()
        {
            yield return LoginOk();
            var r = new Box<JiweWalletResult>(); wallet.GiveAirtimeReward(5, "254700000000", "Milestone", r.Callback); yield return r.Wait();
            Assert.IsTrue(r.Value.Success, r.Value.Error);
            Assert.AreEqual(202, r.Value.HttpStatus, "airtime is asynchronous: accepted, not paid");
            Assert.IsNotEmpty(r.Value.LedgerTransactionId);
            var body = Requests("/rewards/airtime", "POST").Single().body;
            StringAssert.Contains("\"airtimeReward\":5", body);
            StringAssert.Contains("\"recipientNumber\":\"254700000000\"", body);

            var first = new Box<JiweWalletResult>(); wallet.GetTransactionStatus(r.Value.LedgerTransactionId, first.Callback); yield return first.Wait();
            Assert.AreEqual("PENDING", first.Value.Status);
            var second = new Box<JiweWalletResult>(); wallet.GetTransactionStatus(r.Value.LedgerTransactionId, second.Callback); yield return second.Wait();
            Assert.AreEqual("POSTED", second.Value.Status);
        }

        [UnityTest]
        public IEnumerator Airtime_BelowTheMinimum_IsRejected()
        {
            yield return LoginOk();
            var r = new Box<JiweWalletResult>(); wallet.GiveAirtimeReward(4, "254700000000", "d", r.Callback); yield return r.Wait();
            Assert.IsFalse(r.Value.Success); Assert.AreEqual(400, r.Value.HttpStatus); StringAssert.Contains("at least 5", r.Value.Error);
        }

        [UnityTest]
        public IEnumerator TransactionStatus_UnknownId_Is404_NotRetryable()
        {
            var r = new Box<JiweWalletResult>(); wallet.GetTransactionStatus("does-not-exist", r.Callback); yield return r.Wait();
            Assert.IsFalse(r.Value.Success); Assert.AreEqual(404, r.Value.HttpStatus); Assert.IsFalse(r.Value.Retryable);
            StringAssert.Contains("not found", r.Value.Error);
        }

        // ---- which token is X-API-TOKEN ------------------------------------------------
        [UnityTest]
        public IEnumerator PlayerToken_Auto_UsesTheAccessToken_OnceAWalletResourceIsSet()
        {
            auth.walletResource = "https://wallet.example/r";
            yield return LoginOk();
            var r = new Box<JiweWalletResult>(); wallet.GiveXpReward(5, "d", r.Callback); yield return r.Wait();
            Assert.AreEqual(auth.AccessToken, Header(Requests("/rewards/xp").Single(), "x-api-token"));
        }

        [UnityTest]
        public IEnumerator PlayerToken_CanBeForcedEitherWay()
        {
            auth.walletResource = "https://wallet.example/r";
            yield return LoginOk();
            wallet.playerTokenKind = JiweWallet.PlayerTokenKind.IdToken;
            var a = new Box<JiweWalletResult>(); wallet.GiveXpReward(5, "d", a.Callback); yield return a.Wait();
            wallet.playerTokenKind = JiweWallet.PlayerTokenKind.AccessToken;
            var b = new Box<JiweWalletResult>(); wallet.GiveXpReward(5, "d", b.Callback); yield return b.Wait();
            var reqs = Requests("/rewards/xp");
            Assert.AreEqual(auth.IdToken, Header(reqs[0], "x-api-token"));
            Assert.AreEqual(auth.AccessToken, Header(reqs[1], "x-api-token"));
            Assert.IsTrue(a.Value.Success && b.Value.Success);
        }

        [UnityTest]
        public IEnumerator WrongApiKey_Is403_AndNotRetryable()
        {
            yield return LoginOk();
            wallet.apiKey = "wrong";
            var r = new Box<JiweWalletResult>(); wallet.GiveXpReward(5, "d", r.Callback); yield return r.Wait();
            Assert.IsFalse(r.Value.Success); Assert.AreEqual(403, r.Value.HttpStatus); Assert.IsFalse(r.Value.Retryable);
        }

        // ---- purchases ---------------------------------------------------------------
        [UnityTest]
        public IEnumerator Purchase_SendsTheWalletPurchaseSchema_AndParsesTheResult()
        {
            yield return LoginOk();
            var r = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(150, "skin_nebula_01", "Nebula skin", "key-1", r.Callback); yield return r.Wait();

            var v = r.Value;
            Assert.IsTrue(v.Success, v.Error);
            Assert.AreEqual(200, v.HttpStatus); Assert.IsFalse(v.Retryable);
            Assert.AreEqual("POSTED", v.Status); Assert.IsFalse(v.RequiresApproval);
            Assert.IsNotEmpty(v.PaymentOrderId); Assert.IsNotEmpty(v.LedgerTransactionId);
            Assert.AreEqual(150, v.Amount); Assert.AreEqual("KES", v.Currency); Assert.AreEqual("skin_nebula_01", v.ItemId);

            var req = Requests("/purchases/wallet", "POST").Single();
            Assert.AreEqual("key-1", Header(req, "idempotency-key"));
            Assert.AreEqual(auth.IdToken, Header(req, "x-api-token"));
            Assert.AreEqual("test-user", Header(req, "x-api-username"));
            StringAssert.Contains("\"amount\":150", req.body);
            StringAssert.Contains("\"itemId\":\"skin_nebula_01\"", req.body);
            StringAssert.Contains("\"description\":\"Nebula skin\"", req.body);
            foreach (var wrong in new[] { "entityId", "entityName", "gamerWalletId" })
                StringAssert.DoesNotContain(wrong, req.body, "those belong to purchases/adhoc, not purchases/wallet");
            Assert.AreEqual(850, State().player_balance);
        }

        [UnityTest]
        public IEnumerator Purchase_ReplayWithTheSameKey_ReturnsTheOriginal_AndChargesOnce()
        {
            yield return LoginOk();
            var a = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(150, "skin", "d", "same-key", a.Callback); yield return a.Wait();
            var b = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(150, "skin", "d", "same-key", b.Callback); yield return b.Wait();
            Assert.IsTrue(a.Value.Success && b.Value.Success);
            Assert.AreEqual(a.Value.PaymentOrderId, b.Value.PaymentOrderId, "a replay returns the ORIGINAL purchase");
            Assert.AreEqual(1, State().purchase_count);
            Assert.AreEqual(850, State().player_balance, "charged exactly once");
        }

        [UnityTest]
        public IEnumerator Purchase_SameKeyDifferentRequest_Is409_AndExecutesNothing()
        {
            yield return LoginOk();
            var a = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(150, "skin", "d", "k", a.Callback); yield return a.Wait();
            var b = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(999, "other", "d", "k", b.Callback); yield return b.Wait();
            Assert.IsFalse(b.Value.Success); Assert.AreEqual(409, b.Value.HttpStatus); Assert.IsFalse(b.Value.Retryable);
            Assert.AreEqual(1, State().purchase_count);
        }

        [UnityTest]
        public IEnumerator Purchase_WithoutAKey_FailsFast_AndSendsNothing()
        {
            yield return LoginOk();
            var a = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(10, "i", "d", "", a.Callback); yield return a.Wait();
            var b = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(10, "i", "d", new string('k', 201), b.Callback); yield return b.Wait();
            foreach (var r in new[] { a.Value, b.Value })
            {
                Assert.IsFalse(r.Success); StringAssert.Contains("idempotencyKey", r.Error); Assert.IsFalse(r.Retryable); Assert.AreEqual(0, r.HttpStatus);
            }
            Assert.AreEqual(0, Requests("/purchases/wallet").Count);
        }

        [UnityTest]
        public IEnumerator Purchase_InsufficientFunds_IsDefinitive_AndTheKeyIsBoundToTheFailure()
        {
            yield return LoginOk();
            Config("{\"player_balance\":10}");
            var a = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(150, "skin", "d", "key-A", a.Callback); yield return a.Wait();
            Assert.IsFalse(a.Value.Success); Assert.AreEqual(422, a.Value.HttpStatus); Assert.IsFalse(a.Value.Retryable);
            StringAssert.Contains("nsufficient", a.Value.Error);

            Config("{\"player_balance\":1000}");      // the player tops up…
            var replay = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(150, "skin", "d", "key-A", replay.Callback); yield return replay.Wait();
            Assert.IsFalse(replay.Value.Success, "…but the OLD key replays the OLD rejection — this is why a definitive failure needs a new key");
            Assert.AreEqual(422, replay.Value.HttpStatus);

            var fresh = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(150, "skin", "d", "key-B", fresh.Callback); yield return fresh.Wait();
            Assert.IsTrue(fresh.Value.Success, fresh.Value.Error);
        }

        [UnityTest]
        public IEnumerator Purchase_RetryableFailures_AreFlagged_AndTheSameKeyThenSucceeds()
        {
            yield return LoginOk();
            foreach (var status in new[] { 500, 503, 429 })
            {
                string key = "retry-" + status;
                Faults("[{\"match\":\"purchases/wallet\",\"status\":" + status + ",\"count\":1}]");
                var bad = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(10, "i", "d", key, bad.Callback); yield return bad.Wait();
                Assert.IsFalse(bad.Value.Success); Assert.AreEqual(status, bad.Value.HttpStatus);
                Assert.IsTrue(bad.Value.Retryable, status + " is retryable per Jiwe's Errors page");

                var good = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(10, "i", "d", key, good.Callback); yield return good.Wait();
                Assert.IsTrue(good.Value.Success, good.Value.Error);
            }
        }

        [UnityTest]
        public IEnumerator Purchase_DroppedConnection_IsRetryable_WithNoStatus()
        {
            yield return LoginOk();
            Faults("[{\"match\":\"purchases/wallet\",\"drop\":true,\"count\":1}]");
            var r = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(10, "i", "d", "k-drop", r.Callback); yield return r.Wait();
            Assert.IsFalse(r.Value.Success); Assert.AreEqual(0, r.Value.HttpStatus); Assert.IsTrue(r.Value.Retryable);
        }

        [UnityTest]
        public IEnumerator Purchase_DefinitiveStatuses_AreNotRetryable()
        {
            yield return LoginOk();
            foreach (var status in new[] { 400, 403, 404, 409, 422 })
            {
                Faults("[{\"match\":\"purchases/wallet\",\"status\":" + status + ",\"count\":1}]");
                var r = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(10, "i", "d", "k-" + status, r.Callback); yield return r.Wait();
                Assert.IsFalse(r.Value.Success); Assert.AreEqual(status, r.Value.HttpStatus);
                Assert.IsFalse(r.Value.Retryable, status + " is definitive: the outcome will not change");
            }
        }

        [UnityTest]
        public IEnumerator Purchase_HtmlErrorPage_DoesNotThrow_AndIsRetryable()
        {
            yield return LoginOk();
            Faults("[{\"match\":\"purchases/wallet\",\"status\":502,\"body\":\"<html><body>Bad gateway</body></html>\",\"count\":1}]");
            var r = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(10, "i", "d", "k-html", r.Callback); yield return r.Wait();
            Assert.IsFalse(r.Value.Success); Assert.AreEqual(502, r.Value.HttpStatus); Assert.IsTrue(r.Value.Retryable);
            Assert.IsNotEmpty(r.Value.Error);
        }

        [UnityTest]
        public IEnumerator Purchase_RequiringApproval_IsAcceptedNotPaid_ThenSettles()
        {
            yield return LoginOk();
            Config("{\"requires_approval\":true}");
            var r = new Box<JiwePurchaseResult>(); wallet.PurchaseWithWallet(150, "skin", "d", "k-appr", r.Callback); yield return r.Wait();
            Assert.IsTrue(r.Value.Success, "accepted");
            Assert.AreEqual(202, r.Value.HttpStatus);
            Assert.IsTrue(r.Value.RequiresApproval); Assert.AreEqual("PENDING", r.Value.Status);
            Assert.AreEqual(1000, State().player_balance, "not paid yet");

            var s1 = new Box<JiwePurchaseResult>(); wallet.GetPurchaseStatus(r.Value.PaymentOrderId, s1.Callback); yield return s1.Wait();
            Assert.AreEqual("PENDING", s1.Value.Status);
            var s2 = new Box<JiwePurchaseResult>(); wallet.GetPurchaseStatus(r.Value.PaymentOrderId, s2.Callback); yield return s2.Wait();
            Assert.AreEqual("POSTED", s2.Value.Status); Assert.IsFalse(s2.Value.RequiresApproval);
            var req = Requests("/purchases/wallet/", "GET").First();
            Assert.AreEqual(auth.IdToken, Header(req, "x-api-token"));
        }

        [UnityTest]
        public IEnumerator PurchaseStatus_UnknownOrder_Is404()
        {
            yield return LoginOk();
            var r = new Box<JiwePurchaseResult>(); wallet.GetPurchaseStatus("nope", r.Callback); yield return r.Wait();
            Assert.IsFalse(r.Value.Success); Assert.AreEqual(404, r.Value.HttpStatus);
        }

        // ---- earned totals ------------------------------------------------------------
        [UnityTest]
        public IEnumerator RewardsEarned_ApiKeyForm_ParsesAllFour_AndSendsKeyPlusToken()
        {
            yield return LoginOk();
            var r = new Box<JiweRewardsEarnedResult>(); wallet.GetRewardsEarned(r.Callback); yield return r.Wait();
            Assert.IsTrue(r.Value.Success, r.Value.Error);
            Assert.AreEqual(1250f, r.Value.Points); Assert.AreEqual(8400f, r.Value.XP);
            Assert.AreEqual(150f, r.Value.AirtimeEarned); Assert.AreEqual("KES", r.Value.AirtimeCurrency);
            var req = Requests("/rewards/earned", "GET").Single();
            Assert.AreEqual("test-key", Header(req, "x-api-key")); Assert.IsNotNull(Header(req, "x-api-token"));
        }

        [UnityTest]
        public IEnumerator RewardsEarned_BearerForm_SendsOnlyTheBearer_NeverApiKeyHeaders()
        {
            yield return LoginOk();
            var r = new Box<JiweRewardsEarnedResult>(); wallet.GetRewardsEarnedBearer(r.Callback); yield return r.Wait();
            Assert.IsTrue(r.Value.Success, r.Value.Error);
            Assert.AreEqual(1250f, r.Value.Points); Assert.AreEqual(8400f, r.Value.XP);
            Assert.AreEqual(0f, r.Value.AirtimeEarned, "the OIDC response carries points and XP only");
            var req = Requests("/oidc/rewards/earned", "GET").Single();
            Assert.AreEqual("Bearer " + auth.AccessToken, Header(req, "authorization"));
            Assert.IsNull(Header(req, "x-api-username")); Assert.IsNull(Header(req, "x-api-key")); Assert.IsNull(Header(req, "x-api-token"));
        }

        [UnityTest]
        public IEnumerator RewardsEarned_Bearer_MissingScope_Is403_MissingAudience_Is401()
        {
            yield return LoginOk();
            Config("{\"bearer_scope_ok\":false}");
            var a = new Box<JiweRewardsEarnedResult>(); wallet.GetRewardsEarnedBearer(a.Callback); yield return a.Wait();
            Assert.IsFalse(a.Value.Success); Assert.AreEqual(403, a.Value.HttpStatus);
            Config("{\"bearer_scope_ok\":true,\"bearer_audience_ok\":false}");
            var b = new Box<JiweRewardsEarnedResult>(); wallet.GetRewardsEarnedBearer(b.Callback); yield return b.Wait();
            Assert.IsFalse(b.Value.Success); Assert.AreEqual(401, b.Value.HttpStatus);
        }

        // ---- storage -------------------------------------------------------------------
        [UnityTest]
        public IEnumerator UserData_RoundTrips_Merges_AndNullRemoves()
        {
            yield return LoginOk();
            var w1 = new Box<JiweWalletResult>(); wallet.SetUserData("{\"userData\":{\"levelUnlocked\":7,\"journey\":\"a1\"}}", w1.Callback); yield return w1.Wait();
            Assert.IsTrue(w1.Value.Success, w1.Value.Error);
            var w2 = new Box<JiweWalletResult>(); wallet.SetUserData("{\"userData\":{\"extra\":3}}", w2.Callback); yield return w2.Wait();
            var rd = new Box<JiweWalletResult>(); wallet.GetUserData(rd.Callback); yield return rd.Wait();
            var d = JsonUtility.FromJson<UserDataEnvelope>(rd.Value.RawResponse).userData;
            Assert.AreEqual(7, d.levelUnlocked); Assert.AreEqual("a1", d.journey); Assert.AreEqual(3, d.extra, "second write merged, first fields survived");

            var w3 = new Box<JiweWalletResult>(); wallet.SetUserData("{\"userData\":{\"journey\":null}}", w3.Callback); yield return w3.Wait();
            var rd2 = new Box<JiweWalletResult>(); wallet.GetUserData(rd2.Callback); yield return rd2.Wait();
            StringAssert.DoesNotContain("journey", rd2.Value.RawResponse);
            Assert.AreEqual("test-key", Header(Requests("/metadata/user-data", "GET").First(), "x-api-key"));
        }

        [UnityTest]
        public IEnumerator UserData_AFlatBody_IsRejected_TheEnvelopeIsRequired()
        {
            yield return LoginOk();
            var r = new Box<JiweWalletResult>(); wallet.SetUserData("{\"levelUnlocked\":7}", r.Callback); yield return r.Wait();
            Assert.IsFalse(r.Value.Success); Assert.AreEqual(400, r.Value.HttpStatus);
        }

        [UnityTest]
        public IEnumerator GameData_NeedsNoLogin_RoundTrips_Merges_AndNullRemoves()
        {
            var w1 = new Box<JiweWalletResult>(); wallet.SetGameData("{\"gameData\":{\"minVersion\":\"1.7\",\"note\":\"hi\"}}", w1.Callback); yield return w1.Wait();
            Assert.IsTrue(w1.Value.Success, w1.Value.Error);
            var rd = new Box<JiweWalletResult>(); wallet.GetGameData(rd.Callback); yield return rd.Wait();
            var g = JsonUtility.FromJson<GameDataEnvelope>(rd.Value.RawResponse).gameData;
            Assert.AreEqual("1.7", g.minVersion); Assert.AreEqual("hi", g.note);

            var w2 = new Box<JiweWalletResult>(); wallet.SetGameData("{\"gameData\":{\"note\":null}}", w2.Callback); yield return w2.Wait();
            var rd2 = new Box<JiweWalletResult>(); wallet.GetGameData(rd2.Callback); yield return rd2.Wait();
            var g2 = JsonUtility.FromJson<GameDataEnvelope>(rd2.Value.RawResponse).gameData;
            Assert.AreEqual("1.7", g2.minVersion, "merge keeps the other keys"); StringAssert.DoesNotContain("note", rd2.Value.RawResponse);

            var w3 = new Box<JiweWalletResult>(); wallet.SetGameData("{\"gameData\":{}}", w3.Callback); yield return w3.Wait();
            var rd3 = new Box<JiweWalletResult>(); wallet.GetGameData(rd3.Callback); yield return rd3.Wait();
            StringAssert.Contains("1.7", rd3.Value.RawResponse, "an empty gameData removes nothing");
            Assert.AreEqual("/rest/api/v1/metadata/update/game-data", Requests("update/game-data", "POST").First().path);
        }

        [UnityTest]
        public IEnumerator GameData_AFlatBody_IsRejected_TheEnvelopeIsRequired()
        {
            var r = new Box<JiweWalletResult>(); wallet.SetGameData("{\"minVersion\":\"1.7\"}", r.Callback); yield return r.Wait();
            Assert.IsFalse(r.Value.Success); Assert.AreEqual(400, r.Value.HttpStatus);
        }

        // ---- leaderboard & balance -------------------------------------------------------
        [UnityTest]
        public IEnumerator Leaderboard_PeriodBoard_ReadsUppercaseXP_ViaScore_AndNeedsLocalSorting()
        {
            var r = new Box<JiweLeaderboardResult>(); wallet.GetLeaderboard("xp", 10, "week", "highest", r.Callback); yield return r.Wait();
            Assert.IsTrue(r.Value.Success, r.Value.Error);
            Assert.AreEqual(3, r.Value.Entries.Length);
            Assert.AreEqual("Bob", r.Value.Entries[0].name, "the server's own order is not score order");
            var ann = r.Value.Entries.Single(e => e.name == "Ann");
            Assert.AreEqual(900, ann.XP); Assert.AreEqual(0, ann.xp, "reading the lowercase field gives 0 — the bug Score avoids");
            Assert.AreEqual(900, ann.Score);
            var sorted = r.Value.Entries.OrderByDescending(e => e.Score).Select(e => e.name).ToArray();
            CollectionAssert.AreEqual(new[] { "Ann", "Cy", "Bob" }, sorted);

            var body = Requests("/leaderboard", "POST").Single().body;
            StringAssert.Contains("\"rewardType\":\"xp\"", body); StringAssert.Contains("\"leaderboardPeriod\":\"week\"", body);
            StringAssert.Contains("\"maxEntries\":10", body); StringAssert.Contains("\"bestPointsRanking\":\"highest\"", body);
            Assert.IsNull(Header(Requests("/leaderboard").Single(), "x-api-token"), "the leaderboard needs no login");
        }

        [UnityTest]
        public IEnumerator Leaderboard_AllTime_UsesCurrentXpThenXp_AndAnOmittedPeriodIsEmpty()
        {
            var r = new Box<JiweLeaderboardResult>(); wallet.GetLeaderboard("xp", 5, null, null, r.Callback); yield return r.Wait();
            Assert.AreEqual(1641, r.Value.Entries.Single(e => e.name == "Ann").Score, "currentXP wins when XP is 0");
            Assert.AreEqual(915, r.Value.Entries.Single(e => e.name == "Max").Score, "falls back to xp");
            var body = Requests("/leaderboard", "POST").Single().body;
            StringAssert.Contains("\"leaderboardPeriod\":\"\"", body); StringAssert.Contains("\"bestPointsRanking\":\"highest\"", body);
        }

        [UnityTest]
        public IEnumerator Balance_KeepsFractions_AndFloorsTheIntFields()
        {
            Config("{\"balance\":10.12,\"credits\":250.5,\"debits\":100}");
            var r = new Box<JiweWalletBalanceResult>(); wallet.GetWalletBalance(r.Callback); yield return r.Wait();
            Assert.IsTrue(r.Value.Success);
            Assert.AreEqual(10.12, r.Value.AvailableAmount, 1e-9); Assert.AreEqual(10, r.Value.Available, "floored, so 'is there enough?' never rounds up");
            Assert.AreEqual(250.5, r.Value.CreditsAmount, 1e-9); Assert.AreEqual(250, r.Value.Credits);
            Assert.AreEqual(100, r.Value.Debits);
        }

        [UnityTest]
        public IEnumerator Balance_NeedsNoLogin_AndWrongKeyFails()
        {
            wallet.apiKey = "bad";
            var r = new Box<JiweWalletBalanceResult>(); wallet.GetWalletBalance(r.Callback); yield return r.Wait();
            Assert.IsFalse(r.Value.Success);
        }

        // ---- forced update ----------------------------------------------------------------
        [UnityTest]
        public IEnumerator CheckCredentialsValid_FalseOnlyOnADefinitiveRejection()
        {
            var ok = new Box<bool>(); wallet.CheckCredentialsValid(ok.Callback); yield return ok.Wait();
            Assert.IsTrue(ok.Value);

            wallet.apiKey = "rotated-away";
            var bad = new Box<bool>(); wallet.CheckCredentialsValid(bad.Callback); yield return bad.Wait();
            Assert.IsFalse(bad.Value, "403 = this build's keys were rotated out");
            wallet.apiKey = "test-key";

            Faults("[{\"match\":\"account-balance\",\"status\":401,\"count\":1}]");
            var u = new Box<bool>(); wallet.CheckCredentialsValid(u.Callback); yield return u.Wait();
            Assert.IsFalse(u.Value, "401 is also definitive");

            foreach (var f in new[] { "{\"match\":\"account-balance\",\"status\":500,\"count\":1}", "{\"match\":\"account-balance\",\"status\":429,\"count\":1}", "{\"match\":\"account-balance\",\"drop\":true,\"count\":1}" })
            {
                Faults("[" + f + "]");
                var t = new Box<bool>(); wallet.CheckCredentialsValid(t.Callback); yield return t.Wait();
                Assert.IsTrue(t.Value, "a transient failure must never look like a stale build: " + f);
            }
        }

        // ---- plumbing ----------------------------------------------------------------------
        [UnityTest]
        public IEnumerator WalletBaseUrl_TrailingSlash_AndOverride_Work()
        {
            wallet.walletBaseUrl = Base + "/rest/";
            var r = new Box<JiweWalletBalanceResult>(); wallet.GetWalletBalance(r.Callback); yield return r.Wait();
            Assert.IsTrue(r.Value.Success, r.Value.Error);
            Assert.AreEqual("/rest/api/v1/clients/account-balance", Requests("account-balance").Single().path);
        }

        [UnityTest]
        public IEnumerator A200WithAnErrorEnvelope_IsStillAFailure()
        {
            yield return LoginOk();
            Faults("[{\"match\":\"rewards/xp\",\"status\":200,\"body\":{\"type\":\"ERROR\",\"message\":\"nope\"},\"count\":1}]");
            var r = new Box<JiweWalletResult>(); wallet.GiveXpReward(5, "d", r.Callback); yield return r.Wait();
            Assert.IsFalse(r.Value.Success); Assert.AreEqual("nope", r.Value.Error); Assert.AreEqual(200, r.Value.HttpStatus); Assert.IsFalse(r.Value.Retryable);
        }

        [UnityTest]
        public IEnumerator RewardCalls_DroppedConnection_AreRetryableWithNoStatus()
        {
            yield return LoginOk();
            Faults("[{\"match\":\"rewards/xp\",\"drop\":true,\"count\":1}]");
            var r = new Box<JiweWalletResult>(); wallet.GiveXpReward(5, "d", r.Callback); yield return r.Wait();
            Assert.IsFalse(r.Value.Success); Assert.AreEqual(0, r.Value.HttpStatus); Assert.IsTrue(r.Value.Retryable);
        }

        [UnityTest]
        public IEnumerator GameSessionId_IsGeneratedAndStablePerInstance()
        {
            Assert.IsNotEmpty(wallet.GameSessionId);
            var before = wallet.GameSessionId;
            yield return null;
            Assert.AreEqual(before, wallet.GameSessionId);
        }
    }
}
