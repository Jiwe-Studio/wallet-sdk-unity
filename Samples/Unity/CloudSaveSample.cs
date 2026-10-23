using System;
using Jiwe;
using UnityEngine;

namespace JiweSamples
{
    [Serializable] public class SaveData
    {
        public int levelUnlocked;
        public int coins;
        public string journey = "";
        public int _version;     // a change counter you echo back — NOT a lock; two devices writing at once can overwrite each other
    }

    /// <summary>
    /// STARTER 4 — cloud save with Jiwe User Data (KentePunk's progress sync).
    /// The local save is authoritative; the cloud is a sync target. A failed push is logged and carried on
    /// from, because the progress is already safe on the device. On pull, adopt the cloud copy only if it is ahead.
    /// </summary>
    [RequireComponent(typeof(JiweAuth), typeof(JiweWallet))]
    public class CloudSaveSample : MonoBehaviour
    {
        public SaveData Local { get; private set; } = new SaveData();
        public string Status { get; private set; } = "";

        const string LocalKey = "jiwe_sample_save";
        [Serializable] class ReadEnvelope { public string type; public SaveData userData; }
        [Serializable] class WriteEnvelope { public SaveData userData; }

        JiweAuth _auth; JiweWallet _wallet;

        void Awake()
        {
            _auth = GetComponent<JiweAuth>(); _wallet = GetComponent<JiweWallet>();
            if (_wallet.auth == null) _wallet.auth = _auth;
            LoadLocal();
        }

        void LoadLocal()
        {
            string json = PlayerPrefs.GetString(LocalKey, "");
            Local = string.IsNullOrEmpty(json) ? new SaveData() : (JsonUtility.FromJson<SaveData>(json) ?? new SaveData());
        }

        void SaveLocal() { PlayerPrefs.SetString(LocalKey, JsonUtility.ToJson(Local)); PlayerPrefs.Save(); }

        /// <summary>Your game's progress event. Saves locally first, then syncs.</summary>
        public void LevelUp(int coinsEarned = 10)
        {
            Local.levelUnlocked++; Local.coins += coinsEarned; Local.journey = "chapter-" + Local.levelUnlocked;
            SaveLocal();
            PushToCloud();
        }

        public void PushToCloud()
        {
            if (!_auth.IsLoggedIn) { Status = "Saved on this device. Log in to back it up."; return; }
            Local._version++;
            SaveLocal();
            // Send every field you own, every time — even though Jiwe merges, it means a forgotten field can't be lost.
            string body = JsonUtility.ToJson(new WriteEnvelope { userData = Local });
            _wallet.SetUserData(body, r =>
                Status = r.Success ? "Backed up to Jiwe." : $"Cloud backup failed ({r.Error}); progress is still saved on this device.");
        }

        public void PullFromCloud()
        {
            if (!_auth.IsLoggedIn) { Status = "Log in to restore your progress."; return; }
            _wallet.GetUserData(r =>
            {
                if (!r.Success) { Status = "Couldn't reach your cloud save — using this device's progress."; return; }
                SaveData cloud = null;
                try { cloud = JsonUtility.FromJson<ReadEnvelope>(r.RawResponse)?.userData; } catch (Exception) { }
                if (cloud == null) { Status = "No cloud save yet."; return; }     // a never-saved player has an empty record
                if (cloud.levelUnlocked > Local.levelUnlocked)
                {
                    Local = cloud; SaveLocal(); Status = $"Restored level {cloud.levelUnlocked} from the cloud.";
                }
                else Status = "This device is already up to date.";
            });
        }

        void OnGUI()
        {
            SampleGui.Begin("Cloud save (User Data)");
            SampleGui.LoginBar(_auth);
            GUILayout.Label($"Level <b>{Local.levelUnlocked}</b>   coins <b>{Local.coins}</b>   ({Local.journey})");
            if (GUILayout.Button("Level up (+10 coins)")) LevelUp();
            if (GUILayout.Button("Restore from cloud")) PullFromCloud();
            if (!string.IsNullOrEmpty(Status)) GUILayout.Label(Status);
            SampleGui.End();
        }
    }
}
