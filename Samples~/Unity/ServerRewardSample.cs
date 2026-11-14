using System;
using System.Collections;
using System.Text;
using Jiwe;
using UnityEngine;
using UnityEngine.Networking;

namespace JiweSamples
{
    /// <summary>
    /// STARTER 6 — a game that talks to YOUR server, which talks to Jiwe (Sheng Word League's shape).
    /// Run Samples/server-node/server.mjs, set serverUrl, log in. The game never holds the payout decision:
    /// the server validates the run, applies the cap, and credits the player on Jiwe.
    ///
    /// Use this shape when a forged call would cost you real money (airtime, cash, a daily pool). It needs a
    /// server you run; the client-only samples don't. See README section 9.
    /// </summary>
    [RequireComponent(typeof(JiweAuth), typeof(JiweWallet))]
    public class ServerRewardSample : MonoBehaviour
    {
        public string serverUrl = "http://localhost:8766";

        public string Status { get; private set; } = "";
        public int LastAwarded { get; private set; }
        public bool CapReached { get; private set; }
        /// <summary>True when the server says the failure is worth retrying (a timeout, 429 or 5xx on Jiwe's side).</summary>
        public bool LastFailureRetryable { get; private set; }
        public bool Busy { get; private set; }

        [Serializable] class Reply { public int awarded; public bool capReached; public string error; public bool retryable; }

        JiweAuth _auth;
        void Awake() { _auth = GetComponent<JiweAuth>(); }

        public void ClaimRunReward(int score) { if (!Busy) StartCoroutine(Claim(score)); }

        IEnumerator Claim(int score)
        {
            if (!_auth.IsLoggedIn) { Status = "Log in first."; yield break; }
            Busy = true; Status = "Saving your XP…"; LastFailureRetryable = false;

            using var req = new UnityWebRequest(serverUrl.TrimEnd('/') + "/api/run-complete", "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{\"score\":" + score + "}"));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + _auth.IdToken);   // the server reads `sub`; Jiwe verifies the token
            req.timeout = 20;
            yield return req.SendWebRequest();
            Busy = false;

            Reply reply = null;
            try { reply = JsonUtility.FromJson<Reply>(req.downloadHandler.text); } catch (Exception) { }

            if (req.result == UnityWebRequest.Result.Success && reply != null)
            {
                LastAwarded = reply.awarded; CapReached = reply.capReached;
                Status = reply.capReached ? "You've hit today's XP cap — come back tomorrow." : $"+{reply.awarded} XP saved.";
                yield break;
            }
            LastAwarded = 0;
            LastFailureRetryable = req.responseCode == 0 || (reply != null && reply.retryable);
            Status = !string.IsNullOrEmpty(reply?.error) ? reply.error : "Couldn't reach the server.";
        }

        void OnGUI()
        {
            SampleGui.Begin("Server in front of Jiwe");
            SampleGui.LoginBar(_auth);
            GUI.enabled = !Busy;
            if (GUILayout.Button("Finish a run (score 150)")) ClaimRunReward(150);
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(Status)) GUILayout.Label(Status);
            if (LastFailureRetryable) GUILayout.Label("That one was temporary — try again.");
            SampleGui.End();
        }
    }
}
