using UnityEngine;

namespace JiweSamples
{
    /// <summary>
    /// Legacy compatibility fixture. Keep real API keys and client secrets out of shipped Unity builds.
    /// Use Samples/Quickstart with a public client ID.
    /// </summary>
    [CreateAssetMenu(menuName = "Jiwe/Credentials Config", fileName = "JiweCredentials")]
    public class JiweCredentialsConfig : ScriptableObject
    {
        [Header("OAuth (Jiwe profile > Apps)")]
        public string clientId = "";
        public string apiSecret = "";
        [Tooltip("From Jiwe onboarding. Leave empty if you only use API-key calls.")]
        public string walletResource = "";

        [Header("Wallet API (same Apps page)")]
        public string apiUsername = "";
        public string apiKey = "";
    }
}
