using System;
using System.Collections;
using System.Text;
using Jiwe;
using JiweSamples;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Runs INSIDE a real WebGL player. On first load it starts a login, which navigates the SAME tab to the mock
/// "Jiwe login page"; the mock redirects straight back with ?code=…, the page reloads, JiweAuth resumes from
/// localStorage, exchanges the code (cross-origin, so CORS is exercised) and then this script drives the SDK and
/// reports every result to the mock server, where the orchestrating script reads them.
/// </summary>
public static class WebGlE2EBoot
{
    public const string Mock = "http://127.0.0.1:8765";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("Jiwe E2E");
        go.SetActive(false);                                    // configure before any Awake runs
        var auth = go.AddComponent<JiweAuth>();
        var wallet = go.AddComponent<JiweWallet>();
        go.AddComponent<XpAndLeaderboardSample>();
        auth.clientId = "client-1"; auth.apiSecret = "secret-1";
        auth.authorizationEndpoint = Mock + "/auth"; auth.tokenEndpoint = Mock + "/token"; auth.userInfoEndpoint = Mock + "/me";
        auth.loginOnStart = true;
        wallet.auth = auth; wallet.apiUsername = "test-user"; wallet.apiKey = "test-key"; wallet.walletBaseUrl = Mock + "/rest";
        go.AddComponent<WebGlE2ERunner>();
        go.SetActive(true);
        UnityEngine.Object.DontDestroyOnLoad(go);
    }
}

public class WebGlE2ERunner : MonoBehaviour
{
    JiweAuth _auth; JiweWallet _wallet; XpAndLeaderboardSample _sample;

    void Awake()
    {
        _auth = GetComponent<JiweAuth>(); _wallet = GetComponent<JiweWallet>(); _sample = GetComponent<XpAndLeaderboardSample>();
        _auth.OnLoginSuccess += () => StartCoroutine(Run());
        _auth.OnLoginFailed += m => Report("login", false, m);
    }

    void Start() { Report("boot", true, "url=" + Application.absoluteURL); }

    void Report(string step, bool ok, string detail)
    {
        string json = "{\"step\":\"" + step + "\",\"ok\":" + (ok ? "true" : "false") + ",\"detail\":\"" + Esc(detail) + "\"}";
        var req = new UnityWebRequest(WebGlE2EBoot.Mock + "/__report", "POST")
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
            downloadHandler = new DownloadHandlerBuffer()
        };
        req.SetRequestHeader("Content-Type", "application/json");
        req.SendWebRequest();
    }
    static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "'").Replace("\n", " ");

    IEnumerator Run()
    {
        var u = _auth.UserInfo;
        Report("login", _auth.IsLoggedIn && !string.IsNullOrEmpty(_auth.AccessToken) && u != null && u.sub == "player-123" && u.walletId == "wallet-abc",
               $"sub={u?.sub} name={u?.name} walletId={u?.walletId} expired={_auth.IsAccessTokenExpired}");
        yield return null;

        JiweWalletResult? xp = null; _wallet.GiveXpReward(10, "e2e", r => xp = r);
        yield return new WaitUntil(() => xp.HasValue);
        Report("xp", xp.Value.Success && !string.IsNullOrEmpty(xp.Value.LedgerTransactionId), $"status={xp.Value.HttpStatus} err={xp.Value.Error}");

        JiweLeaderboardResult? lb = null; _wallet.GetLeaderboard("xp", 10, "week", "highest", r => lb = r);
        yield return new WaitUntil(() => lb.HasValue);
        Report("leaderboard", lb.Value.Success && lb.Value.Entries.Length == 3 && lb.Value.Entries[1].Score == 900, $"n={lb.Value.Entries?.Length} err={lb.Value.Error}");

        JiwePurchaseResult? p1 = null; _wallet.PurchaseWithWallet(150, "skin_e2e", "e2e", "e2e-key-1", r => p1 = r);
        yield return new WaitUntil(() => p1.HasValue);
        JiwePurchaseResult? p2 = null; _wallet.PurchaseWithWallet(150, "skin_e2e", "e2e", "e2e-key-1", r => p2 = r);
        yield return new WaitUntil(() => p2.HasValue);
        Report("purchase", p1.Value.Success && p2.Value.Success && p1.Value.PaymentOrderId == p2.Value.PaymentOrderId && p1.Value.Status == "POSTED",
               $"status={p1.Value.Status} replaySame={p1.Value.PaymentOrderId == p2.Value.PaymentOrderId} err={p1.Value.Error}");

        JiweRewardsEarnedResult? earned = null; _wallet.GetRewardsEarned(r => earned = r);
        yield return new WaitUntil(() => earned.HasValue);
        Report("earned", earned.Value.Success && earned.Value.XP == 8400f, $"xp={earned.Value.XP} err={earned.Value.Error}");

        JiweRewardsEarnedResult? bearer = null; _wallet.GetRewardsEarnedBearer(r => bearer = r);
        yield return new WaitUntil(() => bearer.HasValue);
        Report("bearer", bearer.Value.Success && bearer.Value.Points == 1250f, $"status={bearer.Value.HttpStatus} err={bearer.Value.Error}");

        JiweWalletResult? sd = null; _wallet.SetUserData("{\"userData\":{\"levelUnlocked\":9}}", r => sd = r);
        yield return new WaitUntil(() => sd.HasValue);
        JiweWalletResult? gd = null; _wallet.GetUserData(r => gd = r);
        yield return new WaitUntil(() => gd.HasValue);
        Report("userdata", sd.Value.Success && gd.Value.Success && gd.Value.RawResponse.Contains("\"levelUnlocked\": 9"), gd.Value.RawResponse);

        JiweWalletResult? sg = null; _wallet.SetGameData("{\"gameData\":{\"minVersion\":\"1.7\"}}", r => sg = r);
        yield return new WaitUntil(() => sg.HasValue);
        JiweWalletResult? gg = null; _wallet.GetGameData(r => gg = r);
        yield return new WaitUntil(() => gg.HasValue);
        Report("gamedata", sg.Value.Success && gg.Value.RawResponse.Contains("1.7"), gg.Value.RawResponse);

        // A starter sample, running in the real player (this also proves its OnGUI doesn't throw: it runs every frame).
        _sample.ReportRun(100, "e2e");
        yield return new WaitUntil(() => _sample.Status == "Synced." || _sample.Status.StartsWith("Couldn't"));
        _sample.RefreshLeaderboard("week");
        yield return new WaitUntil(() => _sample.Board.Count == 3);
        Report("sample", _sample.Status == "" && _sample.Board[0].name == "Ann", $"top={_sample.Board[0].name} xp={_sample.SessionXp}");

        Report("done", true, "all steps ran");
    }
}
