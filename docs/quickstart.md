# Jiwe Unity SDK: Quickstart

This guide gets a Unity game to sign a player in with Jiwe and read their earned XP. You need no API keys and no server code. Everything in the Quickstart runs on the player's device, so it must never contain a secret.

**Status:** This release candidate is not yet verified in the Unity Editor or on a shipping platform. See [release-verification.md](release-verification.md) before using it in a real game.

**What you will build:** a scene where the player signs in through the Jiwe web page, sees their earned XP, and signs out.

---

## Step 1: Install the SDK

**Supported Unity version:** Unity **2022.3** LTS (declared in `package.json`; not yet verified in the Editor).

**Platform:** the intended target is Windows and macOS standalone builds. Neither has been verified on a device yet, and Android, iOS, WebGL and Linux are not supported in this release.

**Install with the Package Manager**, using a version-pinned Git URL:

1. In Unity, open **Window → Package Manager**.
2. Click **+ → Add package from git URL…**.
3. Paste the URL below and press **Add**.

```text
https://github.com/Jiwe-Studio/wallet-sdk-unity.git#v2.0.0-rc.1
```

> **Note:** The `v2.0.0-rc.1` tag is not created yet. Until it exists, use the release-candidate branch: `https://github.com/Jiwe-Studio/wallet-sdk-unity.git#feature/unity-public-client-sdk`. Do not use `main`; it does not contain the new SDK.

The package installs its one dependency, **Newtonsoft Json**, automatically. If Unity reports a missing `Newtonsoft` namespace, add `com.unity.nuget.newtonsoft-json` from the Package Manager's Unity Registry.

**Legacy SDK users:** the old `Jiwe/` folder still works. Do not install both copies. See [legacy-migration.md](legacy-migration.md).

---

## Step 2: Get a Jiwe client ID

You need a **public client ID** for your game. It is not a secret, but it must be issued for your game.

**How it is issued today:** there is no self-service portal for public clients yet. Ask Jiwe to register one. The only contact in the existing SDK README is `rock@jiwe.io`, which is listed there for game uploads; Jiwe has not yet confirmed it as the route for client registration, so confirm it first. Give them:

- Your game's name and a contact email for the developer.
- The **exact redirect URI** you want: `http://127.0.0.1:51772/callback`.
- That you need the **`rewards:read`** scope. The registration must also include the other scopes Jiwe's public-client contract requires; Jiwe will set this up.

Jiwe returns a client ID, which looks like a short ASCII string. Paste it into your scene (Step 3). **Do not ask for, or paste, any other key or secret.** A public client does not have one.

> If Jiwe cannot register the client for you yet, the Quickstart cannot run against production. The SDK will still compile and its offline tests will pass.

---

## Step 3: Configure the SDK

1. Import the **Quickstart** sample: **Package Manager → Jiwe Wallet SDK → Samples → Quickstart → Import**.
2. Open the sample scene (or create an empty GameObject) and add the **JiweQuickstartSample** component.
3. Paste your **Client Id** into the component's field.

Your configuration is two values, both public:

| Setting | Value | Notes |
|---|---|---|
| Client Id | From Step 2 | Public. Never a secret. |
| Redirect URI | `http://127.0.0.1:51772/callback` | Default. Must match the registration **exactly**. |

Your Unity build contains no API key, no client secret and no other credential.

---

## Step 4: Add sign-in

The sample already does this. Here is the code you are relying on:

```csharp
using UnityEngine;

public class MyGame : MonoBehaviour
{
    [SerializeField] private string clientId = "";

    private async void Start()
    {
        await JiweSdk.InitializeAsync(new JiweConfig { ClientId = clientId });
    }

    public async void OnSignInClicked()
    {
        var player = await JiweSdk.Auth.SignInAsync();
        Debug.Log("Signed in. Player ID: " + player.Id);
    }
}
```

When the player clicks sign-in:

1. Your default browser opens the Jiwe sign-in page.
2. The player signs in and approves access.
3. The browser returns to `127.0.0.1` on your computer, and the game continues.

If the player closes the page, `SignInAsync` throws a `JiweException` with `Kind == LoginCancelled`. Catch it and show a message; it is not a crash.

---

## Step 5: Read earned XP

Once the player is signed in:

```csharp
var xp = await JiweSdk.Rewards.GetEarnedXpAsync();
Debug.Log("Earned XP: " + xp.Amount + " " + xp.Unit);
```

- `xp.Amount` is a `decimal` in major units.
- `xp.Unit` is always `"XP"`.
- This is **lifetime earned XP**: the total the player has earned in your game. It is **not a spendable balance**, and it does not change if the player spends XP elsewhere.

If the player is not signed in, the call throws `NotSignedIn`. Call `SignInAsync` first.

> Cowrie and spendable balances are not part of this release. See [api-reference.md](api-reference.md#not-included-in-this-release).

---

## Step 6: Sign out

```csharp
await JiweSdk.Auth.SignOutAsync();
```

**What this does:** it clears the player's session from memory on this device, and cancels any sign-in that is still waiting for the browser.

**What it does not do:** it does not sign the player out of their Jiwe account in the browser. For a public client, Jiwe does not guarantee that. So the next `SignInAsync` may complete immediately for the same player, without asking.

To sign a **different** player in on the same device, use `SwitchAccountAsync` instead. It signs out locally and asks the account chooser to show:

```csharp
var other = await JiweSdk.Auth.SwitchAccountAsync();
```

When your game quits or the object is destroyed, call `JiweSdk.Shutdown()`. The sample does this in `OnDestroy`.

---

## Troubleshooting

Every error is a `JiweException` with a `Kind`, a `Message` and a `Recovery` line. Read the `Recovery` line first.

| `Kind` | What it means | What to do |
|---|---|---|
| `InvalidConfiguration` | Client ID is empty or has spaces, or the redirect URI is wrong. | Check Step 3. Use `http://127.0.0.1:51772/callback` exactly. |
| `LoginFailed` with "could not listen on the registered redirect port" | Another program is using port 51772. | Close the other program, or ask Jiwe to register a different port (then change `RedirectUri` to match). |
| `LoginFailed` with "did not arrive at the registered redirect URI" | The browser returned to a different address than the registered one. | Confirm the redirect URI in your configuration matches the registration exactly. |
| `LoginFailed` with "unexpected issuer" or "ID token … did not verify" | The sign-in response did not come from Jiwe, or the signing keys changed. | Retry once. If it persists, report it with the time of the attempt. |
| `LoginCancelled` | The player closed the sign-in page. | Normal. Offer to sign in again. |
| `Timeout` | Sign-in was not finished within 5 minutes. | Call `SignInAsync` again. |
| `LoginSuperseded` | A newer sign-in or sign-out replaced this one. | Nothing to do. |
| `NotSignedIn` | You read data before signing in. | Call `SignInAsync` first. |
| `SessionExpired` | The session lifetime ended. Public clients cannot refresh silently. | Call `SignInAsync` again. |
| `SessionChanged` | The account changed during the read. | Discard the result and read again. |
| `Unauthorized` | Jiwe rejected the token. The session was cleared. | Sign in again. |
| `InsufficientScope` (HTTP 403) | The client is not permitted to read this data. | Confirm with Jiwe that the client has `rewards:read`. |
| `RateLimited` (HTTP 429) | Too many requests. | Wait for `RetryAfter` (shown in the sample) before retrying. |
| `ServiceUnavailable` (HTTP 5xx) | Jiwe is having problems. | Retry later with backoff. |
| `Network` | No connection. | Check the connection and retry. |
| `UnexpectedResponse` | The response did not match what this SDK version expects. | Update the SDK. If you are on the latest version, report the failure. |
| `InvalidRequest` (HTTP 400) | The SDK sent something Jiwe rejected. | This is a bug. Report it with the `Message`. |

**Nothing happens when I click sign-in:** check that the Unity Console shows no `InvalidConfiguration` error, and that your browser opened. Some browsers block a new window; allow it for the Jiwe page.

**The browser shows a page but the game does not continue:** the redirect port is probably blocked by a firewall, or another program is listening on it. Check the Console for the `Network` or port error above.

For how sign-in works internally, see [authentication.md](authentication.md).

## Award XP and show the leaderboard

Once bearer XP writes are enabled for your game, use the signed-in session.
Create an award ID once for each award and retain it with the award details
for retries.

```csharp
var awardId = Guid.NewGuid();
var transaction = await JiweSdk.Rewards.AwardXpAsync(20m, "Completed level", awardId);
var board = await JiweSdk.Leaderboard.GetLeaderboardAsync();
```

Each row has a rank, display name and score in XP. The caller placement is
available even below the displayed rows and is null for an unranked player.
