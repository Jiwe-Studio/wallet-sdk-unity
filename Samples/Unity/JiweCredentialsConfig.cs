using UnityEngine;

namespace JiweSamples
{
    /// <summary>
    /// Your real Jiwe credentials, kept OUT of scenes and prefabs. Create the asset with
    /// Assets > Create > Jiwe > Credentials Config, save it at exactly Assets/Resources/JiweCredentials.asset,
    /// and add that path to .gitignore. See README section 9.
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
