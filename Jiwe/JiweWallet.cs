using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Jiwe
{
    /// <summary>
    /// Rewards, leaderboard, wallet balance, and transaction status against the
    /// Jiwe Wallet API (confirmed current as of "Jiwe Wallet Documentation
    /// [WIP]": abacus.jiwe.io/rest/api/v1/*).
    ///
    /// Two kinds of calls here:
    ///   - Reward calls (XP / points / airtime) credit a specific PLAYER's
    ///     account, so they need that player logged in via JiweAuth — the
    ///     X-API-TOKEN header is their id_token.
    ///   - Everything else (leaderboard, wallet balance, transaction status)
    ///     is scoped to YOUR app, not a specific player, and only needs the
    ///     static X-API-USERNAME/X-API-KEY pair below — no login required,
    ///     so these work even before a player has logged in.
    ///
    /// Note: there is no "Purchase" (charge-the-player) endpoint in the
    /// current API docs — the previous SDK's Purchase() call had no
    /// current equivalent and has been removed rather than left pointing at
    /// a stale/wrong host. If Jiwe adds a player-purchase endpoint later,
    /// add it here following the same pattern as the reward methods below.
    /// </summary>
    public class JiweWallet : MonoBehaviour
    {
        private const string BaseUrl = "https://abacus.jiwe.io/rest/api/v1";
        private const int NetworkTimeoutSeconds = 20; // UnityWebRequest's own built-in timeout — no cancel button needed here since these are one-shot fire-and-forget calls, not a blocking login screen (contrast JiweAuth.WaitBounded, which also supports an explicit Cancel())

        [Header("Your Jiwe wallet API credentials (Jiwe profile > Apps)")]
        public string apiUsername;
        public string apiKey;

        [Header("Player login (needed only for reward calls)")]
        public JiweAuth auth;

        /// <summary>Unique per play session; sent with reward calls so Jiwe can group them. Auto-generated, override before use if you want your own scheme.</summary>
        public string GameSessionId { get; set; }

        private void Awake()
        {
            GameSessionId = Guid.NewGuid().ToString();
        }

        // -----------------------------------------------------------------
        // Rewards (credit the logged-in player; require JiweAuth.IdToken)
        // -----------------------------------------------------------------

        public void GiveXpReward(int xpEarned, string description, Action<JiweWalletResult> onComplete = null, string transactionId = null)
        {
            var payload = new XpRewardPayload { xpEarned = xpEarned, description = description, gameSessionId = GameSessionId, metadata = new EmptyMetadata() };
            StartCoroutine(PostReward($"{BaseUrl}/rewards/xp", JsonUtility.ToJson(payload), onComplete));
        }

        public void GivePointsReward(int pointsEarned, string description, Action<JiweWalletResult> onComplete = null, string transactionId = null)
        {
            var payload = new PointsRewardPayload { pointsEarned = pointsEarned, description = description, gameSessionId = GameSessionId, transactionId = transactionId ?? Guid.NewGuid().ToString("N").Substring(0, 8), metadata = new EmptyMetadata() };
            StartCoroutine(PostReward($"{BaseUrl}/rewards", JsonUtility.ToJson(payload), onComplete));
        }

        public void GiveAirtimeReward(int airtimeReward, string recipientNumber, string description, Action<JiweWalletResult> onComplete = null, string transactionId = null)
        {
            var payload = new AirtimeRewardPayload { airtimeReward = airtimeReward, description = description, recipientNumber = recipientNumber, gameSessionId = GameSessionId, transactionId = transactionId ?? Guid.NewGuid().ToString("N").Substring(0, 8), metadata = new EmptyMetadata() };
            StartCoroutine(PostReward($"{BaseUrl}/rewards/airtime", JsonUtility.ToJson(payload), onComplete));
        }

        // -----------------------------------------------------------------
        // App-scoped calls (no player login needed)
        // -----------------------------------------------------------------

        public void GetWalletBalance(Action<JiweWalletBalanceResult> onComplete)
        {
            StartCoroutine(GetBalance(onComplete));
        }

        /// <param name="rewardType">"xp" or "cowrie"</param>
        /// <param name="period">"day"/"week"/"month"/"year", or null for all-time</param>
        public void GetLeaderboard(string rewardType, int maxEntries, string period, string bestPointsRanking, Action<JiweLeaderboardResult> onComplete)
        {
            var payload = new LeaderboardRequestPayload { rewardType = rewardType, maxEntries = maxEntries, leaderboardPeriod = period ?? "", bestPointsRanking = bestPointsRanking ?? "highest" };
            StartCoroutine(PostLeaderboard(JsonUtility.ToJson(payload), onComplete));
        }

        public void GetTransactionStatus(string transactionId, Action<JiweWalletResult> onComplete)
        {
            var payload = new TransactionStatusPayload { transactionId = transactionId };
            StartCoroutine(PostReward($"{BaseUrl}/transactions/status", JsonUtility.ToJson(payload), onComplete, requireToken: false));
        }

        /// <summary>
        /// Checks whether THIS BUILD's own apiUsername/apiKey are still accepted by Jiwe — the mechanism
        /// behind a deliberate forced-update flow: rotate these keys server-side, and every build still
        /// holding the old ones can detect it next launch and prompt players to update, rather than
        /// silently failing every wallet call with no explanation. onResult gets `false` ONLY on a
        /// definitive 401/403 (credentials actively rejected); everything else (network blip, timeout,
        /// 5xx) reports `true`, so a transient outage is never mistaken for "this build is stale" — that
        /// distinction matters, an over-eager forced-update prompt is worse than not having one.
        /// Call this once at startup and gate your own "update required" UI on the result.
        /// </summary>
        public void CheckCredentialsValid(Action<bool> onResult)
        {
            StartCoroutine(CheckCredentials(onResult));
        }

        private IEnumerator CheckCredentials(Action<bool> onResult)
        {
            using var req = UnityWebRequest.Get($"{BaseUrl}/clients/account-balance");
            req.timeout = NetworkTimeoutSeconds;
            ApplyStaticHeaders(req);
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success) { onResult?.Invoke(true); yield break; }
            onResult?.Invoke(req.responseCode != 401 && req.responseCode != 403);
        }

        // -----------------------------------------------------------------
        // Requests
        // -----------------------------------------------------------------

        private IEnumerator PostReward(string url, string jsonPayload, Action<JiweWalletResult> onComplete, bool requireToken = true)
        {
            if (requireToken && (auth == null || !auth.IsLoggedIn))
            {
                onComplete?.Invoke(new JiweWalletResult { Success = false, Error = "Not logged in to Jiwe — call JiweAuth.Login() first." });
                yield break;
            }

            using var req = new UnityWebRequest(url, "POST");
            req.timeout = NetworkTimeoutSeconds;
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonPayload));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyStaticHeaders(req);
            if (requireToken) req.SetRequestHeader("X-API-TOKEN", auth.IdToken);

            yield return req.SendWebRequest();

            bool success = req.result == UnityWebRequest.Result.Success;
            string message = null;
            if (!string.IsNullOrEmpty(req.downloadHandler.text))
            {
                var parsed = JsonUtility.FromJson<GenericResponse>(req.downloadHandler.text);
                message = parsed?.message;
                if (parsed != null && parsed.type == "ERROR") success = false;
            }

            onComplete?.Invoke(new JiweWalletResult
            {
                Success = success,
                Error = success ? null : (message ?? req.error),
                RawResponse = req.downloadHandler.text
            });
        }

        private IEnumerator GetBalance(Action<JiweWalletBalanceResult> onComplete)
        {
            using var req = UnityWebRequest.Get($"{BaseUrl}/clients/account-balance");
            req.timeout = NetworkTimeoutSeconds;
            ApplyStaticHeaders(req);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(new JiweWalletBalanceResult { Success = false, Error = req.error });
                yield break;
            }

            var response = JsonUtility.FromJson<WalletBalanceResponse>(req.downloadHandler.text);
            onComplete?.Invoke(new JiweWalletBalanceResult { Success = true, Debits = response.debits, Credits = response.credits, Available = response.available });
        }

        private IEnumerator PostLeaderboard(string jsonPayload, Action<JiweLeaderboardResult> onComplete)
        {
            using var req = new UnityWebRequest($"{BaseUrl}/leaderboard", "POST");
            req.timeout = NetworkTimeoutSeconds;
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonPayload));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyStaticHeaders(req);

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(new JiweLeaderboardResult { Success = false, Error = req.error });
                yield break;
            }

            var response = JsonUtility.FromJson<LeaderboardResponse>(req.downloadHandler.text);
            onComplete?.Invoke(new JiweLeaderboardResult { Success = true, Entries = response.leaderboard ?? Array.Empty<JiweLeaderboardEntry>() });
        }

        private void ApplyStaticHeaders(UnityWebRequest req)
        {
            req.SetRequestHeader("X-API-USERNAME", apiUsername);
            req.SetRequestHeader("X-API-KEY", apiKey);
        }

        [Serializable] private class EmptyMetadata { }
        [Serializable] private class XpRewardPayload { public int xpEarned; public string description; public string gameSessionId; public EmptyMetadata metadata; }
        [Serializable] private class PointsRewardPayload { public int pointsEarned; public string description; public string gameSessionId; public string transactionId; public EmptyMetadata metadata; }
        [Serializable] private class AirtimeRewardPayload { public int airtimeReward; public string description; public string recipientNumber; public string gameSessionId; public string transactionId; public EmptyMetadata metadata; }
        [Serializable] private class TransactionStatusPayload { public string transactionId; }
        [Serializable] private class LeaderboardRequestPayload { public string rewardType; public int maxEntries; public string leaderboardPeriod; public string bestPointsRanking; }
        [Serializable] private class GenericResponse { public string type; public string message; public string ledger_transaction_id; }
        [Serializable] private class WalletBalanceResponse { public string type; public int debits; public int credits; public int available; }
        [Serializable] private class LeaderboardResponse { public string type; public JiweLeaderboardEntry[] leaderboard; }
    }

    public struct JiweWalletResult
    {
        public bool Success;
        public string Error;
        public string RawResponse;
    }

    public struct JiweWalletBalanceResult
    {
        public bool Success;
        public string Error;
        public int Debits;
        public int Credits;
        public int Available;
    }

    public struct JiweLeaderboardResult
    {
        public bool Success;
        public string Error;
        public JiweLeaderboardEntry[] Entries;
    }

    [Serializable]
    public class JiweLeaderboardEntry
    {
        public string id;
        public string name;
        public string username;
        public string email;
        public string phone_number;
        public int rank;
        public int xp;
        public int currentXP; // some entries carry the total under this instead of `xp` — prefer currentXP when nonzero
    }
}
