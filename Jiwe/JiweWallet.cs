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
    /// Three kinds of calls here:
    ///   - Reward calls (XP / points / airtime) credit a specific PLAYER's
    ///     account, so they need that player logged in via JiweAuth — the
    ///     X-API-TOKEN header is their id_token.
    ///   - Purchases (PurchaseWithWallet) debit the logged-in player's own
    ///     Jiwe balance for an in-game item — also needs X-API-TOKEN, plus a
    ///     caller-supplied Idempotency-Key (see PurchaseWithWallet's doc
    ///     comment — this one has a real double-charge footgun if misused).
    ///   - Everything else (leaderboard, wallet balance, transaction status,
    ///     Game Data) is scoped to YOUR app, not a specific player, and only
    ///     needs the static X-API-USERNAME/X-API-KEY pair below — no login
    ///     required, so these work even before a player has logged in.
    /// </summary>
    public class JiweWallet : MonoBehaviour
    {
        private const string DefaultBaseUrl = "https://abacus.jiwe.io/rest";
        private string BaseUrl => (string.IsNullOrWhiteSpace(walletBaseUrl) ? DefaultBaseUrl : walletBaseUrl.Trim().TrimEnd('/')) + "/api/v1";
        // UnityWebRequest's own built-in timeout — no cancel button needed here since these are one-shot
        // fire-and-forget calls, not a blocking login screen (contrast JiweAuth.networkTimeoutSeconds,
        // which backs a manually-polled wait specifically so JiweAuth.Cancel() can also interrupt it).
        // Deliberately a SEPARATE constant from JiweAuth's, not shared — the two components have no
        // reason to be coupled, and 20s for both is a coincidence of picking the same reasonable default,
        // not a shared setting. Adjust independently if one class of call needs a different bound.
        private const int NetworkTimeoutSeconds = 20;

        [Header("Your Jiwe wallet API credentials (Jiwe profile > Apps)")]
        public string apiUsername;
        public string apiKey;

        [Header("Player login (needed only for reward calls)")]
        public JiweAuth auth;

        public enum PlayerTokenKind { Auto, IdToken, AccessToken }
        [Tooltip("Which login token is sent as X-API-TOKEN on reward/purchase/user-data calls. Jiwe's docs say a Hermes ACCESS token; projects built before that was documented used the ID token and it worked live (KentePunk, Sheng Word League). Auto = access token when JiweAuth.walletResource is set (so it's audienced to the Wallet API), otherwise the ID token. Force one only if Auto isn't working for your environment.")]
        public PlayerTokenKind playerTokenKind = PlayerTokenKind.Auto;

        [Header("Optional — non-production environments only")]
        [Tooltip("Leave empty for production (https://abacus.jiwe.io/rest). Jiwe runs separate non-production environments with different base URLs and credentials — paste the Wallet base URL Jiwe gives you, WITHOUT the /api/v1 part. A production key won't authenticate against non-production and vice versa.")]
        public string walletBaseUrl = "";

        private string PlayerToken
        {
            get
            {
                if (auth == null) return "";
                bool useAccess = playerTokenKind == PlayerTokenKind.AccessToken
                    || (playerTokenKind == PlayerTokenKind.Auto && !string.IsNullOrWhiteSpace(auth.walletResource) && !string.IsNullOrEmpty(auth.AccessToken));
                return useAccess ? auth.AccessToken : auth.IdToken;
            }
        }

        /// <summary>Unique per play session; sent with reward calls so Jiwe can group them. Auto-generated, override before use if you want your own scheme.</summary>
        public string GameSessionId { get; set; }

        private void Awake()
        {
            GameSessionId = Guid.NewGuid().ToString();
        }

        // -----------------------------------------------------------------
        // Rewards (credit the logged-in player; require JiweAuth.IdToken)
        // -----------------------------------------------------------------

        /// <param name="transactionId">Optional reference of your own for this reward. Jiwe's RewardBase schema accepts one on every reward type, but its XP examples omit it, so it is only sent when you supply it.</param>
        public void GiveXpReward(int xpEarned, string description, Action<JiweWalletResult> onComplete = null, string transactionId = null)
        {
            string json = string.IsNullOrEmpty(transactionId)
                ? JsonUtility.ToJson(new XpRewardPayload { xpEarned = xpEarned, description = description, gameSessionId = GameSessionId, metadata = new EmptyMetadata() })
                : JsonUtility.ToJson(new XpRewardWithRefPayload { xpEarned = xpEarned, description = description, gameSessionId = GameSessionId, transactionId = transactionId, metadata = new EmptyMetadata() });
            StartCoroutine(PostReward($"{BaseUrl}/rewards/xp", json, onComplete));
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
        // Purchases (debit the logged-in player's own wallet; require JiweAuth.IdToken)
        // -----------------------------------------------------------------

        /// <summary>
        /// Charges the logged-in player's own Jiwe wallet balance for an in-game item. A purchase
        /// never partially applies — it either fully succeeds or nothing moves.
        ///
        /// Request fields match the WalletPurchase schema in Jiwe's supported API reference
        /// (v1.0.15): amount, itemId, description, metadata. <c>itemId</c> is YOUR catalogue
        /// reference for what's being bought — Jiwe carries it onto the purchase record but never
        /// prices or validates it, so the amount you send is the amount charged (see README §6 on
        /// why that matters for a client-side purchase). Jiwe's hand-written "Take a payment" guide
        /// shows entityId/entityName/gamerWalletId on this endpoint instead; those are the fields of
        /// the separate purchases/adhoc endpoint, and don't appear anywhere in this endpoint's schema.
        ///
        /// <c>idempotencyKey</c> is NOT optional and NOT auto-generated, unlike the reward calls'
        /// transactionId. Generate it ONCE, right before this call, store it locally, and reuse the
        /// EXACT same value if you retry after a timeout or an otherwise-ambiguous failure — sending
        /// the same key again returns the ORIGINAL purchase instead of charging the player a second
        /// time. Generating a fresh key on every retry defeats the entire point of this header and
        /// risks a real double charge. Max 200 characters. See the README's "In-app purchases"
        /// section for the full retry-safe flow.
        /// </summary>
        public void PurchaseWithWallet(int amount, string itemId, string description, string idempotencyKey, Action<JiwePurchaseResult> onComplete)
        {
            var payload = new PurchasePayload { amount = amount, itemId = itemId, description = description, metadata = new EmptyMetadata() };
            StartCoroutine(PostPurchase(JsonUtility.ToJson(payload), idempotencyKey, onComplete));
        }

        /// <summary>
        /// Checks a purchase's current status by the <c>PaymentOrderId</c> from PurchaseWithWallet's
        /// result — use this after a timeout/ambiguous PurchaseWithWallet call if you don't have the
        /// idempotency key on hand to safely retry, or to poll a purchase where RequiresApproval was
        /// true. Same API-key headers plus X-API-TOKEN as PurchaseWithWallet, per the reference.
        /// </summary>
        public void GetPurchaseStatus(string paymentOrderId, Action<JiwePurchaseResult> onComplete)
        {
            StartCoroutine(GetPurchase(paymentOrderId, onComplete));
        }

        // -----------------------------------------------------------------
        // What a player has earned (two ways to ask)
        // -----------------------------------------------------------------

        /// <summary>
        /// Reads the logged-in player's earned totals — points, XP and airtime — through the API-key
        /// boundary: <c>GET /api/v1/rewards/earned</c> with X-API-USERNAME/X-API-KEY plus X-API-TOKEN.
        /// This is the form already running live in KentePunk. Totals are scoped to YOUR app (what the
        /// player earned from you), and they are LIFETIME EARNED totals — not a spendable balance: points
        /// the player has since spent are not subtracted, and Jiwe has no endpoint that returns a player's
        /// current wallet balance.
        /// </summary>
        public void GetRewardsEarned(Action<JiweRewardsEarnedResult> onComplete)
        {
            StartCoroutine(GetRewardsEarnedCoroutine(onComplete, bearer: false));
        }

        /// <summary>
        /// The same totals through Jiwe's player-facing "Hermes OIDC bearer" boundary:
        /// <c>GET /api/v1/oidc/rewards/earned</c> with <c>Authorization: Bearer &lt;access_token&gt;</c> and NO
        /// API-key headers at all (Jiwe refuses a request that carries both). Documented as the right call
        /// from a player's own device because it needs no API key — but not yet verified live in this SDK.
        ///
        /// Requires the <c>rewards:read</c> scope (a token without it gets 403 — add it via
        /// <see cref="JiweAuth.extraScopes"/>, and don't retry) AND an access token audienced to the Wallet API
        /// (<see cref="JiweAuth.walletResource"/>; without it the token is for the default resource and the
        /// call gets 401). The supported reference lists only points and XP here, so AirtimeEarned reads 0.
        /// Uses <see cref="JiweAuth.AccessToken"/>, never the ID token. The access token lives ~15 minutes and
        /// can't be refreshed by a public client: on a 401 send the player back through Login().
        /// </summary>
        public void GetRewardsEarnedBearer(Action<JiweRewardsEarnedResult> onComplete)
        {
            StartCoroutine(GetRewardsEarnedCoroutine(onComplete, bearer: true));
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
        /// Checks whether THIS BUILD's own apiUsername/apiKey (the WALLET credentials on this component)
        /// are still accepted by Jiwe — the mechanism behind a deliberate forced-update flow: rotate these
        /// keys server-side, and every build still holding the old ones can detect it next launch and
        /// prompt players to update, rather than silently failing every wallet call with no explanation.
        /// onResult gets `false` ONLY on a definitive 401/403 (credentials actively rejected); everything
        /// else (network blip, timeout, 5xx) reports `true`, so a transient outage is never mistaken for
        /// "this build is stale" — that distinction matters, an over-eager forced-update prompt is worse
        /// than not having one. Call this once at startup and gate your own "update required" UI on the
        /// result.
        ///
        /// SCOPE: this only validates the wallet API pair (apiUsername/apiKey on THIS component) — it does
        /// NOT validate JiweAuth's separate OAuth pair (clientId/apiSecret). The two are independent
        /// credentials against different endpoints; rotating one doesn't invalidate the other, and there's
        /// no equivalent lightweight check for the OAuth pair (validating it means actually running a
        /// login). If you rotate both together for a forced update, this only confirms the wallet half —
        /// an invalid OAuth secret would instead surface later as a real login failure via
        /// JiweAuth.OnLoginFailed, not from this method.
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

        /// <summary>
        /// Raw read access to your app's Game Data — an app-scoped JSON key/value blob (no player
        /// login needed), for anything the game should react to without shipping a new build: a
        /// minimum-supported-version gate, an in-game announcement, a feature flag, etc. This is
        /// Jiwe's own documented use case for this endpoint, not a workaround.
        ///
        /// Deliberately schema-less here — define your own [Serializable] class for whatever you
        /// store and parse RawResponse yourself with JsonUtility.FromJson&lt;T&gt;. Per Jiwe's docs,
        /// the response wraps your fields in an envelope — {"type", "gameData": {...your fields...},
        /// "_version"} — so your class needs a `gameData` field of your own nested type, not a flat
        /// shape matching RawResponse directly. See the README's "Game Data patterns" section for
        /// worked examples (version gate, announcement banner).
        ///
        /// Treat everything stored here as PUBLIC: Jiwe's docs say explicitly that Game Data should
        /// be OK to expose, and reading it only requires X-API-USERNAME, not the secret X-API-KEY.
        /// </summary>
        public void GetGameData(Action<JiweWalletResult> onComplete)
        {
            StartCoroutine(GetGameDataCoroutine(onComplete));
        }

        /// <summary>
        /// Writes to your app's Game Data. Confirmed live: updates merge/patch rather than replace —
        /// sending a JSON object only touches the keys you include, so set a field to null explicitly
        /// to clear it rather than expecting an empty payload to wipe existing data. jsonPayload must
        /// mirror the read side's envelope, e.g. {"gameData": {"minVersion": "1.8"}} — not a bare
        /// {"minVersion": "1.8"}. Confirmed live (KentePunk writes {"gameData":{"lvl_<id>":4}} this way, merging
        /// only its own key). Everyone can read what you write here — see GetGameData.
        /// </summary>
        public void SetGameData(string jsonPayload, Action<JiweWalletResult> onComplete)
        {
            StartCoroutine(SetGameDataCoroutine(jsonPayload, onComplete));
        }

        /// <summary>
        /// Raw read of THIS player's application-defined data (<c>GET /api/v1/metadata/user-data</c>) —
        /// per-player storage such as unlocked levels, cosmetics or a cloud save. Needs a logged-in player
        /// (X-API-TOKEN) as well as the API key. Like Game Data it is schema-less here: Jiwe wraps your
        /// fields as <c>{"type":"OK","userData":{...your fields...}}</c>, so give your parser a
        /// <c>userData</c> field. A player who has never saved gets an empty object.
        /// </summary>
        public void GetUserData(Action<JiweWalletResult> onComplete)
        {
            StartCoroutine(UserDataCoroutine("GET", $"{BaseUrl}/metadata/user-data", null, onComplete));
        }

        /// <summary>
        /// Merges into THIS player's data (<c>POST /api/v1/metadata/update/user-data</c>). jsonPayload mirrors
        /// the read envelope: <c>{"userData":{"levelUnlocked":15}}</c>. Updates merge — only the keys you send
        /// change; send a key as null to remove it. KentePunk also echoes a <c>_version</c> counter inside
        /// userData; treat it as a change counter, NOT a lock — Jiwe doesn't reject a write with a stale one,
        /// so two devices writing at once can overwrite each other.
        /// </summary>
        public void SetUserData(string jsonPayload, Action<JiweWalletResult> onComplete)
        {
            StartCoroutine(UserDataCoroutine("POST", $"{BaseUrl}/metadata/update/user-data", jsonPayload, onComplete));
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
            if (requireToken) req.SetRequestHeader("X-API-TOKEN", PlayerToken);

            yield return req.SendWebRequest();

            onComplete?.Invoke(ToResult(req));
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
            onComplete?.Invoke(new JiweWalletBalanceResult
            {
                Success = true,
                // Jiwe returns monetary values in major units and says they "may carry a fractional part" — keep the
                // exact figure, and floor the int fields so a pre-claim "is there enough?" check never rounds up.
                DebitsAmount = response.debits, CreditsAmount = response.credits, AvailableAmount = response.available,
                Debits = (int)Math.Floor(response.debits), Credits = (int)Math.Floor(response.credits), Available = (int)Math.Floor(response.available)
            });
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

        private IEnumerator PostPurchase(string jsonPayload, string idempotencyKey, Action<JiwePurchaseResult> onComplete)
        {
            if (auth == null || !auth.IsLoggedIn)
            {
                onComplete?.Invoke(new JiwePurchaseResult { Success = false, Error = "Not logged in to Jiwe — call JiweAuth.Login() first." });
                yield break;
            }

            using var req = new UnityWebRequest($"{BaseUrl}/purchases/wallet", "POST");
            req.timeout = NetworkTimeoutSeconds;
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonPayload));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Idempotency-Key", idempotencyKey);
            ApplyStaticHeaders(req);
            req.SetRequestHeader("X-API-TOKEN", PlayerToken);

            yield return req.SendWebRequest();

            onComplete?.Invoke(ParsePurchaseResponse(req));
        }

        private IEnumerator GetPurchase(string paymentOrderId, Action<JiwePurchaseResult> onComplete)
        {
            using var req = UnityWebRequest.Get($"{BaseUrl}/purchases/wallet/{paymentOrderId}");
            req.timeout = NetworkTimeoutSeconds;
            ApplyStaticHeaders(req);
            if (auth != null && auth.IsLoggedIn) req.SetRequestHeader("X-API-TOKEN", PlayerToken);
            yield return req.SendWebRequest();

            onComplete?.Invoke(ParsePurchaseResponse(req));
        }

        private JiwePurchaseResult ParsePurchaseResponse(UnityWebRequest req)
        {
            bool success = req.result == UnityWebRequest.Result.Success;
            string text = req.downloadHandler?.text;
            string message = null;
            PurchaseResponse parsed = null;
            if (!string.IsNullOrEmpty(text))
            {
                try { parsed = JsonUtility.FromJson<PurchaseResponse>(text); } catch (Exception) { /* not JSON */ }
                message = parsed?.message;
                if (parsed != null && parsed.type == "ERROR") success = false;
            }

            return new JiwePurchaseResult
            {
                Success = success,
                Error = success ? null : (message ?? req.error),
                RawResponse = text,
                HttpStatus = req.responseCode,
                Retryable = !success && IsRetryable(req.responseCode),
                PaymentOrderId = parsed?.payment_order_id,
                LedgerTransactionId = parsed?.ledger_transaction_id,
                Status = parsed?.status,
                RequiresApproval = parsed?.requires_approval ?? false,
                Amount = parsed?.amount ?? 0,
                Currency = parsed?.currency,
                ItemId = parsed?.itemId,
                Description = parsed?.description,
                CatalogPriceValidated = parsed?.catalogPriceValidated ?? false
            };
        }

        private IEnumerator GetRewardsEarnedCoroutine(Action<JiweRewardsEarnedResult> onComplete, bool bearer)
        {
            if (auth == null || !auth.IsLoggedIn || (bearer && string.IsNullOrEmpty(auth.AccessToken)))
            {
                onComplete?.Invoke(new JiweRewardsEarnedResult { Success = false, Error = "Not logged in to Jiwe — call JiweAuth.Login() first." });
                yield break;
            }

            using var req = UnityWebRequest.Get(bearer ? $"{BaseUrl}/oidc/rewards/earned" : $"{BaseUrl}/rewards/earned");
            req.timeout = NetworkTimeoutSeconds;
            if (bearer)
            {
                req.SetRequestHeader("Authorization", $"Bearer {auth.AccessToken}");
            }
            else
            {
                ApplyStaticHeaders(req);
                req.SetRequestHeader("X-API-TOKEN", PlayerToken);
            }
            yield return req.SendWebRequest();

            string text = req.downloadHandler?.text;
            if (req.result != UnityWebRequest.Result.Success)
            {
                string message = null;
                try { message = JsonUtility.FromJson<GenericResponse>(text)?.message; } catch (Exception) { }
                onComplete?.Invoke(new JiweRewardsEarnedResult { Success = false, Error = message ?? req.error, RawResponse = text, HttpStatus = req.responseCode });
                yield break;
            }

            RewardsEarnedResponse parsed = null;
            try { parsed = JsonUtility.FromJson<RewardsEarnedResponse>(text); } catch (Exception) { }
            onComplete?.Invoke(new JiweRewardsEarnedResult
            {
                Success = parsed != null,
                Error = parsed == null ? "Unexpected response from Jiwe." : null,
                HttpStatus = req.responseCode,
                Points = parsed?.points ?? 0,
                XP = parsed?.XP ?? 0,
                AirtimeEarned = parsed?.airtimeEarned ?? 0,
                AirtimeCurrency = string.IsNullOrEmpty(parsed?.airtimeCurrency) ? "KES" : parsed.airtimeCurrency,
                RawResponse = text
            });
        }

        private IEnumerator UserDataCoroutine(string method, string url, string jsonPayload, Action<JiweWalletResult> onComplete)
        {
            if (auth == null || !auth.IsLoggedIn)
            {
                onComplete?.Invoke(new JiweWalletResult { Success = false, Error = "Not logged in to Jiwe — call JiweAuth.Login() first." });
                yield break;
            }

            using var req = new UnityWebRequest(url, method);
            req.timeout = NetworkTimeoutSeconds;
            req.downloadHandler = new DownloadHandlerBuffer();
            if (jsonPayload != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonPayload));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            ApplyStaticHeaders(req);
            req.SetRequestHeader("X-API-TOKEN", PlayerToken);

            yield return req.SendWebRequest();

            onComplete?.Invoke(ToResult(req));
        }

        private IEnumerator GetGameDataCoroutine(Action<JiweWalletResult> onComplete)
        {
            using var req = UnityWebRequest.Get($"{BaseUrl}/metadata/game-data");
            req.timeout = NetworkTimeoutSeconds;
            ApplyStaticHeaders(req);
            yield return req.SendWebRequest();

            onComplete?.Invoke(ToResult(req));
        }

        private IEnumerator SetGameDataCoroutine(string jsonPayload, Action<JiweWalletResult> onComplete)
        {
            // Confirmed live: the write path is /metadata/update/game-data, not /metadata/game-data —
            // Jiwe's own endpoint docs list only the latter for both read and write, but POSTing there
            // doesn't apply the update. It also requires X-API-KEY in addition to X-API-USERNAME (Jiwe's
            // docs list X-API-USERNAME only) — ApplyStaticHeaders already sends both on every call, so
            // this is handled automatically, not something you need to add here.
            using var req = new UnityWebRequest($"{BaseUrl}/metadata/update/game-data", "POST");
            req.timeout = NetworkTimeoutSeconds;
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonPayload));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyStaticHeaders(req);

            yield return req.SendWebRequest();

            onComplete?.Invoke(ToResult(req));
        }

        // HTTP statuses where retrying is right per Jiwe's Errors page: no response at all (a timeout —
        // you can't tell whether it arrived), 429 (rate limited) and 5xx. Every other 4xx is a definitive
        // answer that will not change on retry.
        private static bool IsRetryable(long httpStatus) => httpStatus == 0 || httpStatus == 429 || httpStatus >= 500;

        private static JiweWalletResult ToResult(UnityWebRequest req)
        {
            bool success = req.result == UnityWebRequest.Result.Success;
            string text = req.downloadHandler?.text;
            string message = null, ledger = null, status = null;
            if (!string.IsNullOrEmpty(text))
            {
                GenericResponse parsed = null;
                try { parsed = JsonUtility.FromJson<GenericResponse>(text); } catch (Exception) { /* not JSON — e.g. a proxy's HTML error page */ }
                message = parsed?.message;
                ledger = parsed?.ledger_transaction_id;
                status = parsed?.status;
                if (parsed != null && parsed.type == "ERROR") success = false;
            }

            return new JiweWalletResult
            {
                Success = success,
                Error = success ? null : (message ?? req.error),
                RawResponse = text,
                HttpStatus = req.responseCode,
                Retryable = !success && IsRetryable(req.responseCode),
                LedgerTransactionId = ledger,
                Status = status
            };
        }

        private void ApplyStaticHeaders(UnityWebRequest req)
        {
            req.SetRequestHeader("X-API-USERNAME", apiUsername);
            req.SetRequestHeader("X-API-KEY", apiKey);
        }

        [Serializable] private class EmptyMetadata { }
        [Serializable] private class XpRewardWithRefPayload { public int xpEarned; public string description; public string gameSessionId; public string transactionId; public EmptyMetadata metadata; }
        [Serializable] private class XpRewardPayload { public int xpEarned; public string description; public string gameSessionId; public EmptyMetadata metadata; }
        [Serializable] private class PointsRewardPayload { public int pointsEarned; public string description; public string gameSessionId; public string transactionId; public EmptyMetadata metadata; }
        [Serializable] private class AirtimeRewardPayload { public int airtimeReward; public string description; public string recipientNumber; public string gameSessionId; public string transactionId; public EmptyMetadata metadata; }
        [Serializable] private class TransactionStatusPayload { public string transactionId; }
        [Serializable] private class PurchasePayload { public int amount; public string itemId; public string description; public EmptyMetadata metadata; }
        [Serializable] private class PurchaseResponse { public string type; public string message; public string payment_order_id; public string ledger_transaction_id; public string status; public bool requires_approval; public int amount; public string currency; public string itemId; public string description; public bool catalogPriceValidated; }
        [Serializable] private class LeaderboardRequestPayload { public string rewardType; public int maxEntries; public string leaderboardPeriod; public string bestPointsRanking; }
        [Serializable] private class GenericResponse { public string type; public string message; public string ledger_transaction_id; public string status; }
        [Serializable] private class WalletBalanceResponse { public string type; public double debits; public double credits; public double available; }
        [Serializable] private class LeaderboardResponse { public string type; public JiweLeaderboardEntry[] leaderboard; }
        [Serializable] private class RewardsEarnedResponse { public float points; public float XP; public float airtimeEarned; public string airtimeCurrency; }
    }

    public struct JiweWalletResult
    {
        public bool Success;
        public string Error;
        public string RawResponse;
        /// <summary>HTTP status of the response; 0 means no response arrived (timeout/connection failure) — or that no request was sent at all, e.g. you weren't logged in.</summary>
        public long HttpStatus;
        /// <summary>True when retrying is appropriate per Jiwe's docs: a timeout (outcome unknown), 429, or 5xx. Retry with backoff — and for anything that moves money, with the SAME idempotency key. False for every other failure: a 4xx is a definitive answer that won't change.</summary>
        public bool Retryable;
        /// <summary>The ledger_transaction_id Jiwe returned, if any — your handle for GetTransactionStatus. Store it before telling the player anything.</summary>
        public string LedgerTransactionId;
        /// <summary>The "status" field of a transactions/status response (PENDING, POSTED, ARCHIVED, FAILED, REVERSED); null for other calls.</summary>
        public string Status;
    }

    public struct JiweWalletBalanceResult
    {
        public bool Success;
        public string Error;
        public int Debits;
        public int Credits;
        public int Available;
        /// <summary>Exact values — may have a fractional part.</summary>
        public double DebitsAmount;
        public double CreditsAmount;
        public double AvailableAmount;
    }

    public struct JiweRewardsEarnedResult
    {
        public bool Success;
        public string Error;
        public string RawResponse;
        public long HttpStatus;
        public float Points;
        public float XP;
        public float AirtimeEarned;
        /// <summary>Currency of AirtimeEarned; defaults to "KES" when the response doesn't say.</summary>
        public string AirtimeCurrency;
    }

    public struct JiwePurchaseResult
    {
        public bool Success;
        public string Error;
        public string RawResponse;
        /// <summary>HTTP status; 0 = no response arrived.</summary>
        public long HttpStatus;
        /// <summary>True when the outcome may be unknown or the request wasn't processed (no response, 429, 5xx): retry with the SAME idempotency key. When false and Success is false, Jiwe gave a definitive answer — that key is now bound to it, so a genuine retry needs a NEW key.</summary>
        public bool Retryable;
        public string PaymentOrderId;
        public string LedgerTransactionId;
        public string Status; // "PENDING" / "POSTED" / "ARCHIVED" / "FAILED" / "REVERSED" — see README §6
        public bool RequiresApproval;
        public int Amount;
        public string Currency;
        public string ItemId;
        public string Description;
        public bool CatalogPriceValidated;
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
        public int currentXP; // some entries carry the total under this instead of `xp`
        public int XP; // the live API's per-period score is UPPERCASE "XP" (JsonUtility is case-sensitive, so without this field every period board read as 0 — confirmed live in KentePunk)

        /// <summary>The score to display: whichever of XP / currentXP / xp is nonzero, in that order.</summary>
        public int Score => XP != 0 ? XP : currentXP != 0 ? currentXP : xp;
    }
}
