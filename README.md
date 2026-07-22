# Wallet API Documentation

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
- On **JiweAuth**, fill in your app credentials from your Jiwe profile page:
  - `clientId`, `apiKey`, `apiSecret`, `gameId`
  - `testMode` — check this while testing against Jiwe's sandbox
  - `mobileRedirectScheme` — **Android/iOS builds only**; a custom URI scheme (e.g. `yourgame`) that you must also register with Jiwe as an allowed redirect URI. Not used on Standalone or WebGL.

**The login redirect itself works differently per platform** (this is inherent to how each platform's browser integration works, not a config choice):

| Platform | How the redirect gets back to your game |
|---|---|
| Standalone / Editor | A local loopback HTTP server catches the browser's redirect automatically — no extra setup. |
| Android / iOS | The system browser redirects to your custom URI scheme, which reopens the app. Requires registering that redirect URI with Jiwe first. |
| WebGL | Jiwe's login page redirects back to your **same hosted game URL** with the auth code attached; the page reloads and the SDK resumes automatically. **Your Jiwe app must allow CORS from your hosted domain**, since the token exchange happens as a browser-side request — ask Jiwe to whitelist your domain if this fails with a CORS error in the browser console. |

Leave `loginOnStart` checked (default) to log in automatically when the scene loads, or uncheck it and call `jiweAuth.Login()` yourself (e.g. from a "Log in with Jiwe" button) if you'd rather not block on login before showing any menu.

1. Test by running the game (or a WebGL/Android build) — it should open the Jiwe login page, and once you log in, `JiweAuth.OnLoginSuccess` fires and `jiweAuth.IsLoggedIn` becomes true.
2. If login fails, `JiweAuth.OnLoginFailed` fires with a message (also logged as a `Debug.LogWarning`) — nothing else in the SDK force-stops your game, so how you gate gameplay on login is up to you.

* * *

**3. Adding purchase and reward functionality through the JiweWallet SDK**

**3a. How the reward and purchase functionality works:**

![](https://lh3.googleusercontent.com/nxz3WieVAfGAm-ftFibi9yAX5AP_y1YgH94opxO2PMVmd9lPZXA2Z4e0hzFFs-e_M2ItXbKBdps6hxGuLJYbPMUtuzKNwf35Pi6RrYmZgfBMui_a0xA4lYyVF9JzK5SlPVLJvWnil-sys6R08kegwowMNMJnyM4va120DzuHz4GSqTM_GSZn3AtELQ)

Call these from anywhere in the game once `JiweAuth.IsLoggedIn` is true. Make sure you have the correct game wallet id and enough budget for each future transaction.

**a) Rewards:**

```csharp
jiweWallet.RewardPlayer(0.1, "ORDER_ID", "Reward for passing boss#1", result => {
    if (result.Success) Debug.Log("Reward sent!");
    else Debug.LogWarning($"Reward failed: {result.Error}");
});
```

![](https://lh4.googleusercontent.com/_wKPE6vQFGV9kkgGOHOAh-qOipbsQd6UWgt1YMJ5Da4Zf9Y7XvbOzaBGwyBVrUJqMQROGWNkaneiH68pmlHhI73Wk2HbixOpfgQv3_gDTAaGfb1w41REY-RM_PNY0udoYc0SWLPScPKqHetiI6DKFKXDGukVIP2qSoR0VHLvCnyUnfXkmyTCPcvQxg)

Arguments:

- `0.1` — the amount to be rewarded
- `"ORDER_ID"` — useful for your own records to know which reward you gave
- `"Reward for passing boss#1"` — a description of the reward
- the callback (optional) — receives a `JiweWalletResult` with `Success`, `Error`, and the raw response body, instead of only a `Debug.Log` you have to go find in the console

The reward acts as a debit to the developer's wallet and a credit to the player's wallet.

If the game wallet is at 0, `result.Success` will be `false` with the HTTP status in `result.Error` (previously: only visible as a 429 in the console log) — check `result.Success` in your callback to show this to the player instead of digging through logs.

**b) Purchases:**

```csharp
jiweWallet.Purchase(0.1, "ORDER_ID", "Purchasing pigs in a blanket", result => {
    if (result.Success) Debug.Log("Purchase complete!");
    else Debug.LogWarning($"Purchase failed: {result.Error}");
});
```

![](https://lh4.googleusercontent.com/_wKPE6vQFGV9kkgGOHOAh-qOipbsQd6UWgt1YMJ5Da4Zf9Y7XvbOzaBGwyBVrUJqMQROGWNkaneiH68pmlHhI73Wk2HbixOpfgQv3_gDTAaGfb1w41REY-RM_PNY0udoYc0SWLPScPKqHetiI6DKFKXDGukVIP2qSoR0VHLvCnyUnfXkmyTCPcvQxg)

Same arguments as `RewardPlayer`, but this debits the player's wallet and credits the developer's. If the player's wallet is at 0, `result.Success` is `false` — same pattern, check it in your callback rather than only logging.

> **Note:** these calls hit `bursment.jiwe.io`, a different host from the wallet API described in some newer Jiwe docs (`abacus.jiwe.io`) that use a different auth header scheme (`X-API-USERNAME`/`X-API-KEY`/`X-API-TOKEN` instead of `api_key`/`api_secret`/`Authorization`). If you're integrating both, confirm with the Jiwe team which one is current for reward/purchase calls — this SDK targets `bursment.jiwe.io` as that's what the original SDK used.

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
