using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Jiwe
{
    /// <summary>
    /// Rewards and purchases against the player's Jiwe wallet. Requires a
    /// logged-in JiweAuth (rewards/purchases are billed against a specific
    /// player's account, so login must happen first).
    ///
    /// Simplified from the original SDK: no Newtonsoft.Json dependency (uses
    /// Unity's built-in JsonUtility), and result callbacks instead of silent
    /// Debug.Log-only error handling.
    /// </summary>
    public class JiweWallet : MonoBehaviour
    {
        private const string RewardUrl = "https://bursment.jiwe.io/api/v1/cowrie/rewards";
        private const string PurchaseUrl = "https://bursment.jiwe.io/api/v1/cowrie/purchases";

        public JiweAuth auth;

        public void RewardPlayer(double amount, string orderId, string description, Action<JiweWalletResult> onComplete = null)
        {
            StartCoroutine(Post(RewardUrl, amount, orderId, description, onComplete));
        }

        public void Purchase(double amount, string orderId, string description, Action<JiweWalletResult> onComplete = null)
        {
            StartCoroutine(Post(PurchaseUrl, amount, orderId, description, onComplete));
        }

        private IEnumerator Post(string url, double amount, string orderId, string description, Action<JiweWalletResult> onComplete)
        {
            if (auth == null || !auth.IsLoggedIn)
            {
                onComplete?.Invoke(new JiweWalletResult { Success = false, Error = "Not logged in to Jiwe — call JiweAuth.Login() first." });
                yield break;
            }

            var payload = JsonUtility.ToJson(new WalletRequestPayload
            {
                gameId = auth.gameId,
                amount = amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                orderId = orderId,
                description = description,
                idempotencyKey = Guid.NewGuid().ToString("N").Substring(0, 8)
            });

            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("api_key", auth.testMode ? $"test_{auth.apiKey}" : auth.apiKey);
            req.SetRequestHeader("api_secret", auth.apiSecret);
            req.SetRequestHeader("Authorization", $"Bearer {auth.IdToken}");

            yield return req.SendWebRequest();

            bool success = req.result == UnityWebRequest.Result.Success;
            onComplete?.Invoke(new JiweWalletResult
            {
                Success = success,
                Error = success ? null : $"{req.responseCode}: {req.error}",
                RawResponse = req.downloadHandler.text
            });
        }

        [Serializable] private class WalletRequestPayload { public string gameId; public string amount; public string orderId; public string description; public string idempotencyKey; }
    }

    public struct JiweWalletResult
    {
        public bool Success;
        public string Error;
        public string RawResponse;
    }
}
