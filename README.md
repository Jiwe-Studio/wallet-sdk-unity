# Wallet API Documentation

> **Read this box before shipping anything.** These are real bugs found (and fixed, in this SDK) while
> building a live game against Jiwe's actual servers — not theoretical advice.
>
> 1. **Always give login a working "Skip"/"Cancel" button, the entire time login is in progress, not
>    just before it starts.** A hung browser or a slow token exchange has no other way out. `JiweAuth.Cancel()`
>    exists exactly for this — wire it to a button that stays clickable throughout, and call it
>    unconditionally on Skip even if a login attempt is mid-flight.
> 2. **`apiSecret` must be sent in the token exchange's POST body, on every platform, including mobile.**
>    Confirmed by a live 401 (`invalid_client`) when it was omitted for a "public client" PKCE flow —
>    Jiwe's server isn't OAuth-spec-pure here, it wants the secret regardless of client type. Don't
>    remove it for security purity without re-testing against a real login first.
> 3. **Never hardcode real credentials in a committed script or scene.** Load them at runtime from a
>    gitignored config asset instead (a `ScriptableObject` works well) — see "Keeping credentials out of
>    git" below. This SDK's own `clientId`/`apiSecret`/`apiUsername`/`apiKey` fields are Inspector values
>    for exactly that reason: assign them on a prefab/asset that's gitignored, not by editing this script.
> 4. **If you ever need to force players on old builds to update, rotate `apiUsername`/`apiKey` server-side
>    and call `JiweWallet.CheckCredentialsValid()` once at startup.** It reports `false` only on a
>    definitive 401/403 — never on a transient network failure — so it's safe to gate a "please update"
>    banner on directly.
> 5. **A `HttpListener.Stop()` called before its response is fully written disposes the response mid-write
>    and throws silently inside async code.** Already fixed in this SDK's loopback path (see the comment
>    in `LoginViaLoopback`) — if you ever touch that method, keep `Stop()` AFTER the browser response is
>    sent, not in a `finally` right after the redirect is caught.
> 6. **Before letting a player tap a reward/airtime claim button, re-check `JiweWallet.GetBalance()` and
>    compare `Available` against the claim amount — don't only rely on Jiwe's own rejection.** A pool
>    figure shown earlier on a different screen (e.g. a main menu) can be stale by the time the player
>    actually claims. `JiweWalletBalanceResult` already carries `Success`/`Available` for exactly this;
>    a consumer game shipped without this check and surfaced Jiwe's raw "Account balance is insufficient"
>    error instead of a clear "pool is low, try later" message — the fix is a few lines in the caller, not
>    in this SDK, but easy to miss if you don't know to do it.
> 7. **If credentials copied from the Jiwe dashboard behave unexpectedly** (auth fails despite matching
>    values, or a top-up doesn't seem to reflect), don't assume your code is wrong first — Jiwe support has
>    confirmed at least one case where the dashboard's "API Secret" column displayed the wrong value paired
>    to an API Key. Recopy the credentials and confirm which account is actually configured before you go
>    debugging the integration.

The Jiwe Wallet API is designed to allow you to authenticate players and allow them to receive rewards and make purchases on their accounts.

As the developer, the game will be drawing and crediting your account for any in-game transaction by players. You can keep track of transactions through the Jiwe IO wallet dashboard for your records and to make sure you have a positive wallet balance to maintain normal functionality.

Content:

1. Getting started on Jiwe IO

   1. Registering on Jiwe IO
   2. Connecting Your Jiwe IO wallet to Your Celo Wallet

2. Adding authentication into the game

   1. How authentication works
   2. Generating game wallet ID
   3. Downloading and importing the JiweWallet SDK for Unity
   4. Integrating the game wallet ID

3. Adding purchase and reward functionality through the JiweWallet SDK for Unity

   1. How the functionality works
   2. Adding rewards
   3. Adding purchases

4. Download and play the game

5. Withdrawing to Celo or Mpesa (Kotani Pay)

* * *

1. **Getting started on Jiwe IO:**

1. Register on Jiwe IO
2. Connecting Celo Wallet to Jiwe Wallet

a) Register on Jiwe IO

1. Go to[www.jiwe.io](http://www.jiwe.io), click signup to register your account, and log in with the created account
2. Once logged in click browse games which will take you to the games library and on the panel on the left-hand side click wallet to set up your wallet.

b)Creating your Jiwe IO wallet and connecting it to your Celo wallet

1. Follow the instructions on the wallet page to connect to the Celo wallet (Alfajores Test Wallet). To test your wallet
2. Request funds from[rock@jiwe.io](mailto:rock@jiwe.io) or whatapp +254773754444 to top up your Alfajores Test Wallet, and then from Alfajores wallet you can send and recieve from your Jiwe wallet page.

* * *

**2)Adding authentication to your game:**

**How authentication works:**

![](https://lh4.googleusercontent.com/m5CsdPl_rfCqGatj-jnBH2ew7bHhB-jsALx-BhF8rwVZrdqfNrIKT5kmIT5CVXM-F71mIFj-IftC6k7h9n87Dw5i2sodFwumGpNcKL1UAup4hRoQawAYEBqr0ttiBD4wtIbH5qTmCbkMZ3HMBIm6eZG_csu-juziV9vmp76xBIasefDg7MrjfnF-qg)**  
****Steps:**

1. Generating game wallet ID API key (Each game requires a unique wallet ID)
2. Downloading and importing the `Jiwe/` folder and configuring your app credentials

1. **Generating the game wallet ID**

1. Go to your profile settings by selecting the profile button on the left panel on Jiwe IO
2. Select Apps from the top tabs, follow the instructions and generate a unique Game Wallet ID, API Key and API Secret for each game you create  

   (For security purposes a unique key is required for each game to be able to validate or invalidate each game individually.)

**B. Download and import the JiweWallet SDK to your game**

Copy the `Jiwe/` folder (`JiweAuth.cs` + `JiweWallet.cs`) into your project's `Assets/` — that's the whole SDK now. No extra DLLs to import: the endpoint URLs (`id.jiwe.io/auth`, `/token`, `/me`) are built in, and there's no Newtonsoft.Json dependency to drag in separately (the SDK uses Unity's own `JsonUtility`).

- In your Hierarchy, create an empty GameObject (e.g. "JiweSDK") and add both the **JiweAuth** and **JiweWallet** components to it
- On **JiweWallet**, drag the same GameObject's **JiweAuth** component into the `auth` field
- Fill in your credentials from your Jiwe profile page — note these are **two separate credential pairs**, not one:
  - On **JiweAuth** (OAuth login): `clientId`, `apiSecret` (sent as the token endpoint's `client_secret`)
  - On **JiweWallet** (wallet API calls): `apiUsername`, `apiKey` — sent as `X-API-USERNAME`/`X-API-KEY` on every wallet call. These are static per-app and don't require a player to be logged in, which is why leaderboard/wallet-balance/transaction-status calls work even before login.
  - `mobileRedirectScheme` (on JiweAuth) — **Android/iOS builds only**; a custom URI scheme (e.g. `yourgame`) that you must also register with Jiwe as an allowed redirect URI. Not used on Standalone or WebGL.

**The login redirect itself works differently per platform** (this is inherent to how each platform's browser integration works, not a config choice):

| Platform | How the redirect gets back to your game |
|---|---|
| Standalone / Editor | A local loopback HTTP server catches the browser's redirect automatically — no extra setup. |
| Android / iOS | The system browser redirects to your custom URI scheme, which reopens the app. Requires registering that redirect URI with Jiwe first. |
| WebGL | Jiwe's login page redirects back to your **same hosted game URL** with the auth code attached; the page reloads and the SDK resumes automatically. **Your Jiwe app must allow CORS from your hosted domain**, since the token exchange happens as a browser-side request — ask Jiwe to whitelist your domain if this fails with a CORS error in the browser console. |

Leave `loginOnStart` checked (default) to log in automatically when the scene loads, or uncheck it and call `jiweAuth.Login()` yourself (e.g. from a "Log in with Jiwe" button) if you'd rather not block on login before showing any menu.

1. Test by running the game (or a WebGL/Android build) — it should open the Jiwe login page, and once you log in, `JiweAuth.OnLoginSuccess` fires and `jiweAuth.IsLoggedIn` becomes true.
2. If login fails, `JiweAuth.OnLoginFailed` fires with a message (also logged as a `Debug.LogWarning`) — nothing else in the SDK force-stops your game, so how you gate gameplay on login is up to you.
3. **Always wire a Skip/Cancel button that stays clickable the whole time login is in progress**, calling `jiweAuth.Cancel()`:

```csharp
skipButton.onClick.AddListener(() => {
    jiweAuth.Cancel(); // no-op if nothing's in progress — safe to call unconditionally
    ShowMainMenuAnonymously();
});
```

`networkTimeoutSeconds` (default 20) also bounds the flow on its own even if the player never taps Skip — a hung browser or dead network eventually fails visibly instead of hanging forever.

**Keeping credentials out of git:** don't type real values into `clientId`/`apiSecret`/`apiUsername`/`apiKey` on a committed prefab or scene. Instead, create a small `ScriptableObject` (gitignored) that holds the real values, and have your own startup code copy them into these fields at runtime — e.g.:

```csharp
// A gitignored asset (Resources/MyJiweCredentials.asset) holding real values, loaded once at startup:
var config = Resources.Load<MyJiweCredentialsConfig>("MyJiweCredentials");
if (config != null)
{
    jiweAuth.clientId = config.clientId;
    jiweAuth.apiSecret = config.apiSecret;
    jiweWallet.apiUsername = config.apiUsername;
    jiweWallet.apiKey = config.apiKey;
}
```

This is the same pattern used in the reference implementation this SDK was extracted from — one `.gitignore` line (`Assets/Resources/MyJiweCredentials.asset`) keeps real keys from ever being committed, while every scene rebuild/re-import still picks them up correctly.

**Forcing an update via key rotation:** if you ever need to push all players to a new build (a breaking API change, a security rotation, etc.), rotate `apiUsername`/`apiKey` on Jiwe's side and check for it at startup:

```csharp
jiweWallet.CheckCredentialsValid(valid => {
    if (!valid) ShowUpdateRequiredBanner(); // old builds still holding the rotated-out keys land here
});
```

This only fires `false` on a real 401/403 — a flaky network or a server hiccup reports `true`, so it's safe to gate directly on the result without worrying about false positives locking players out during an unrelated outage.

* * *

**3. Adding reward, leaderboard, and wallet functionality through the JiweWallet SDK**

**3a. How it works:**

![](https://lh3.googleusercontent.com/nxz3WieVAfGAm-ftFibi9yAX5AP_y1YgH94opxO2PMVmd9lPZXA2Z4e0hzFFs-e_M2ItXbKBdps6hxGuLJYbPMUtuzKNwf35Pi6RrYmZgfBMui_a0xA4lYyVF9JzK5SlPVLJvWnil-sys6R08kegwowMNMJnyM4va120DzuHz4GSqTM_GSZn3AtELQ)

Reward calls (XP, points, airtime) credit the logged-in player and require `JiweAuth.IsLoggedIn` to be true first. Leaderboard, wallet balance, and transaction status are scoped to your app rather than a player, so they work any time — no login needed.

> **There is no "purchase" (charge-the-player) call.** The previous SDK's `Purchase()` doesn't correspond to anything in the current Jiwe Wallet API and has been removed rather than left pointing at a stale host. If Jiwe adds a player-purchase endpoint later, it can be added to `JiweWallet.cs` following the same pattern as the reward methods below.

**a) XP reward** — for progress/skill milestones; XP has no monetary value in Jiwe:

```csharp
jiweWallet.GiveXpReward(20, "Reward for passing boss#1", result => {
    if (result.Success) Debug.Log("XP awarded!");
    else Debug.LogWarning($"XP reward failed: {result.Error}");
});
```

**b) Points (Cowrie) reward** — Jiwe's monetary in-game currency:

```csharp
jiweWallet.GivePointsReward(20, "Reward for passing boss#1", result => { /* same result shape */ });
```

**c) Airtime reward** — credits real phone airtime to a recipient number, at least 5 units:

```csharp
jiweWallet.GiveAirtimeReward(20, "254722334455", "Weekly top-grinder payout", result => { /* same result shape */ });
```

All three take an optional `transactionId` (auto-generated if omitted) and return a `JiweWalletResult` (`Success`, `Error`, `RawResponse`) in the callback — check `result.Success` to show failures (e.g. reward amount below the API's minimum, or your app's wallet at 0) to the player instead of only reading the console log.

**d) Leaderboard:**

```csharp
jiweWallet.GetLeaderboard("xp", maxEntries: 20, period: null, bestPointsRanking: "highest", result => {
    if (result.Success) foreach (var e in result.Entries) Debug.Log($"{e.rank}. {e.name} — {e.currentXP}");
});
```
`rewardType` is `"xp"` or `"cowrie"`; `period` is `"day"`/`"week"`/`"month"`/`"year"`, or `null` for all-time.

**e) Wallet balance** (your app's own Jiwe wallet, not a player's):

```csharp
jiweWallet.GetWalletBalance(result => {
    if (result.Success) Debug.Log($"Available: {result.Available}");
});
```

**f) Transaction status** — check any `ledger_transaction_id` returned by the calls above:

```csharp
jiweWallet.GetTransactionStatus(transactionId, result => Debug.Log(result.RawResponse));
```

* * *

**4)Uploading your game to Jiwe IO**

The upload functionality is currently disabled, please send your game by filling this[form](https://docs.google.com/forms/d/e/1FAIpQLSdhm05d9BqPreGTqGIIFeqWZl47hhP0jOgIKTPsfDFaOVIk7Q/viewform) and informing [rock@jiwe.io](mailto:rock@jiwe.io) and copy [charles@jiwe.io](mailto:charles@jiwe.io) who will upload your game within 1 day.

Once uploaded, to play the game:

1. Navigate to[www.jiwe.io](http://www.jiwe.io)
2. Login to Jiwe IO
3. Select the game

* * *

**5)Withdrawing**

a)From Jiwe IO to Celo

b)From Celo to Mpesa using Kotani Pay

**a)Withdrawing from Jiwe IO to Celo**  
-Navigate to your wallet page and select withdraw to Celo and enter the amount to withdraw and click ok.

The amount will be transferred to your Celo wallet

**b)Transfering from Celo to Mpesa using Kotani Pay**

1. Link your Celo wallet with Kotani Pay to withdraw to Mpesa
2. Dial USSD code \*483\*354# and link your Celo wallet and Kotani Pay
3. Dial USSD code \*483\*354# and select widthraw to withdraw your funds to Mpesa

* * *
