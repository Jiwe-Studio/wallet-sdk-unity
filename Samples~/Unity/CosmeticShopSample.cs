using System;
using System.Collections.Generic;
using Jiwe;
using UnityEngine;

namespace JiweSamples
{
    [Serializable] public class ShopItem { public string itemId; public string name; public int priceCowrie; }

    /// <summary>
    /// STARTER 3 — buy cosmetics with the player's cowrie (KentePunk's skin shop).
    /// The part that matters is the idempotency key: made ONCE per item, saved to disk BEFORE the call, kept
    /// through ambiguous failures (no response, 429, 5xx) and thrown away after a settled success or a
    /// definitive rejection. Get that wrong and a retry can charge twice — or a player stays stuck on an old failure.
    ///
    /// Jiwe never validates the price, so the amount is whatever this client sends. Fine for low-value
    /// cosmetics; for anything you can't afford to have underpaid, price it on your own server (README section 9).
    /// </summary>
    [RequireComponent(typeof(JiweAuth), typeof(JiweWallet))]
    public class CosmeticShopSample : MonoBehaviour
    {
        public List<ShopItem> items = new List<ShopItem>
        {
            new ShopItem { itemId = "skin_nebula_01", name = "Nebula", priceCowrie = 150 },
            new ShopItem { itemId = "skin_ember_02",  name = "Ember",  priceCowrie = 300 },
        };

        public string Status { get; private set; } = "";
        public bool Busy { get; private set; }

        const string OwnedKey = "jiwe_sample_owned";
        static string KeyPref(string itemId) => "jiwe_sample_purchase_key_" + itemId;

        JiweAuth _auth; JiweWallet _wallet;
        void Awake() { _auth = GetComponent<JiweAuth>(); _wallet = GetComponent<JiweWallet>(); if (_wallet.auth == null) _wallet.auth = _auth; }

        public bool IsOwned(string itemId) => (PlayerPrefs.GetString(OwnedKey, "") + ",").Contains("," + itemId + ",");
        /// <summary>True while a purchase attempt for this item is unresolved and its key is being kept.</summary>
        public bool HasPendingAttempt(string itemId) => PlayerPrefs.HasKey(KeyPref(itemId));

        void Grant(string itemId) { PlayerPrefs.SetString(OwnedKey, PlayerPrefs.GetString(OwnedKey, "") + "," + itemId); PlayerPrefs.Save(); }

        public void Buy(ShopItem item)
        {
            if (Busy) return;
            if (!_auth.IsLoggedIn) { Status = "Log in to buy."; return; }
            if (IsOwned(item.itemId)) { Status = "You already own this."; return; }

            // One key per logical purchase, persisted BEFORE the network call so a crash can't lose it.
            string pref = KeyPref(item.itemId);
            string key = PlayerPrefs.GetString(pref, "");
            if (string.IsNullOrEmpty(key)) { key = Guid.NewGuid().ToString("N"); PlayerPrefs.SetString(pref, key); PlayerPrefs.Save(); }

            Busy = true; Status = "Buying…";
            _wallet.PurchaseWithWallet(item.priceCowrie, item.itemId, "Skin: " + item.name, key, r =>
            {
                Busy = false;
                if (r.Success && !r.RequiresApproval && r.Status != "PENDING")
                {
                    PlayerPrefs.DeleteKey(pref); PlayerPrefs.Save();      // settled — the next purchase gets a fresh key
                    Grant(item.itemId); Status = item.name + " unlocked!";
                }
                else if (r.Success)
                {
                    Status = "Purchase accepted but not paid yet — not unlocked.";   // keep the key; poll GetPurchaseStatus(r.PaymentOrderId)
                }
                else if (r.Retryable)
                {
                    Status = "Couldn't confirm the purchase. Try again — you won't be charged twice.";   // SAME key next time
                }
                else
                {
                    PlayerPrefs.DeleteKey(pref); PlayerPrefs.Save();      // definitive rejection: a later attempt is a NEW purchase
                    Status = string.IsNullOrEmpty(r.Error) ? $"Purchase failed ({r.HttpStatus})." : r.Error;
                }
            });
        }

        void OnGUI()
        {
            SampleGui.Begin("Cosmetic shop (cowrie)");
            SampleGui.LoginBar(_auth);
            foreach (var item in items)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{item.name} — {item.priceCowrie} cowrie");
                GUI.enabled = !Busy && !IsOwned(item.itemId);
                if (GUILayout.Button(IsOwned(item.itemId) ? "Owned" : "Buy", GUILayout.Width(120))) Buy(item);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            if (!string.IsNullOrEmpty(Status)) GUILayout.Label(Status);
            SampleGui.End();
        }
    }
}
