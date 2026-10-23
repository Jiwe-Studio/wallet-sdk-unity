# Use cases from shipped Jiwe games

Three games, three different shapes. Each section says what the game uses Jiwe for, shows
the code that does it, and lists what went wrong along the way. Snippets labelled
**verbatim** are copied from the game with only secrets and unrelated lines removed;
snippets labelled **adapted** are the same calls written against this SDK (KentePunk
wraps the same endpoints in a client of its own).

| | [KentePunk](#1-kentepunk--a-unity-mobile-game-with-no-backend) | [Sheng Word League](#2-sheng-word-league--a-web-game-with-a-backend-in-front-of-jiwe) | [JiweChess](#3-jiwechess--a-unity-webgl-game-that-is-guest-first) |
|---|---|---|---|
| Platform | Unity, Android | Browser (React) + Node/Express | Unity WebGL |
| Backend of its own | **None** | **Yes** — holds the Jiwe keys | None |
| Uses Jiwe for | XP + leaderboard, airtime milestone, cosmetics, cloud save, update gate | XP, airtime prizes, cowrie purchases, daily pool, leaderboards | Sign-in, XP, leaderboard |
| Trust model | Client calls Jiwe directly; caps are advisory | Server validates, then calls Jiwe; caps are enforced | Sign-in only when a feature needs it |

**Want to run one?** Each pattern below has a drop-in starter in [`Samples/`](../Samples) — add the component to a
GameObject and press Play.

**Which should you copy?** If a forged call costs you real money (airtime, cash, a
daily pool), use Sheng Word League's shape: your server between the game and Jiwe. If it
doesn't, KentePunk's client-only shape ships faster. If your players are young or you
want people playing before they sign up, JiweChess's guest-first shape applies on top of
either. The reasoning is in README §9.

---

## 1. KentePunk — a Unity mobile game with no backend

A bumper-arena game. Players earn XP per run; XP feeds a leaderboard and an airtime
milestone; cosmetic skins cost cowrie; progress syncs to the cloud. Everything talks to
Jiwe straight from the device.

### 1.1 Startup guard: rotated keys and a minimum version

Two independent "please update" signals, checked once at launch and **both failing
open** — a network blip must never lock a player out.

```csharp
public bool UpdateRequired { get; private set; }
public string MinVersion { get; private set; } = "";

void Start()
{
    // 1) Key rotation: false ONLY on a definitive 401/403.
    wallet.CheckCredentialsValid(valid => { if (!valid) UpdateRequired = true; });

    // 2) Minimum version, kept in the app's Game Data as {"gameData":{"minVersion":"1.7"}}.
    wallet.GetGameData(r => {
        if (!r.Success) return;
        MinVersion = JsonUtility.FromJson<GameDataEnvelope>(r.RawResponse)?.gameData?.minVersion ?? "";
        if (IsOlderThan(Application.version, MinVersion)) UpdateRequired = true;
    });
}

// verbatim from KentePunk: an empty or unreadable minimum never blocks anyone
public static bool IsOlderThan(string current, string minimum)
{
    if (string.IsNullOrWhiteSpace(minimum)) return false;
    return System.Version.TryParse(current, out var cur)
        && System.Version.TryParse(minimum.Trim(), out var min) && cur < min;
}
```

Setting the minimum is a manual `POST /metadata/update/game-data` with
`{"gameData":{"minVersion":"1.8"}}`. KentePunk shows its store page URL next to the
message so the player can act on it. (README §10–§11.)

### 1.2 Reporting a run's XP

The visible counter works for everyone; only the Jiwe call is gated. **Adapted.**

```csharp
public void ReportRun(int score, string mode)
{
    sessionXp += score;                                   // counts whether or not the player is logged in

    if (UpdateRequired)      { SetStatus("Update the app to keep syncing XP"); return; }   // checked BEFORE the login gate
    if (!auth.IsLoggedIn)    { SetStatus("Log in with Jiwe to save this and earn airtime"); return; }

    SetStatus("Syncing to the Jiwe leaderboard…");
    wallet.GiveXpReward(score, $"Kente Punk {mode} run", r =>
        SetStatus(r.Success ? "Synced" : $"Couldn't sync right now ({r.Error}). It'll retry next launch."));
}
```

An outdated build's own API key can be rejected whether or not the player is logged in,
which is why the update check comes first.

### 1.3 A leaderboard that also shows each player's level

A leaderboard row can't carry your own fields, and one player can't read another's saved
data — so KentePunk keeps one key per player in the shared Game Data and joins them
client-side. **Adapted** (KentePunk reads the map with a regex; this parses it the same way
you'd parse anything under `gameData`).

```csharp
// write: merges only this player's own key, leaving everyone else's (and minVersion) untouched
wallet.SetGameData("{\"gameData\":{\"lvl_" + auth.UserInfo.sub + "\":" + level + "}}", r => { });

// read: rows come back in whatever order the server likes — sort and use Score, not xp
wallet.GetLeaderboard("xp", 20, period: "week", "highest", board => {
    if (!board.Success) return;
    var rows = new List<JiweLeaderboardEntry>(board.Entries);
    rows.Sort((a, b) => b.Score.CompareTo(a.Score));
    for (int i = 0; i < rows.Count; i++)
        Show(rank: i + 1, rows[i].name, rows[i].Score, level: levels.TryGetValue(rows[i].id, out var lv) ? lv : 0);
});
```

Two live findings baked in: a period board's score arrives as an **uppercase `XP`**
(reading `xp` gives 0 for every row — `entry.Score` handles it), and the server's rank can
disagree with its own order, so KentePunk sorts locally and recomputes the rank.
Remember the level map is **public** and grows with your player count.

### 1.4 Airtime milestone: check the pool, then claim

2,500 XP earns KES 5 of airtime (Jiwe's minimum is 5). The phone number is typed at claim
time — Jiwe's profile has none. **Adapted** from `AirtimeRewardUI`.

```csharp
void OnClaimTapped(string phone)
{
    claimButton.interactable = false;                      // Jiwe has no idempotency key here: guard double-taps yourself

    // The pool figure on the main menu can be minutes stale. Re-check right before spending the call.
    wallet.GetWalletBalance(bal => {
        if (bal.Success && bal.AvailableAmount < pendingKes) {
            claimButton.interactable = true;
            ShowMessage($"The airtime pool is low right now (KES {bal.Available} available) — try again later");
            return;
        }
        // If the balance call itself failed, fall through: Jiwe's own answer is still authoritative.

        wallet.GiveAirtimeReward(pendingKes, phone, "Kente Punk milestone reward", r => {
            claimButton.interactable = true;
            if (r.Success) { PlayChime(); ConsumeMilestone(); }
            else ShowMessage($"Couldn't claim right now: {r.Error}");
        }, transactionId: Guid.NewGuid().ToString());
    });
}
```

Without the pre-check, a player who walked through the whole flow saw Jiwe's raw
"Account balance is insufficient." An accepted airtime reward is also **asynchronous** — it
carries a `LedgerTransactionId`; confirm delivery with `GetTransactionStatus` rather than
assuming the response means the player was paid.

### 1.5 Buying a skin with cowrie

The idempotency key is made once per item and kept in `PlayerPrefs` until the outcome is
definite. **Verbatim structure** from `GameFlowController`, written against this SDK.

```csharp
void BuySkin(Skin skin)
{
    if (!auth.IsLoggedIn) { Say("Log in to buy skins."); return; }

    string keyPref = $"kp_purchase_key_{skin.ItemId}";
    string key = PlayerPrefs.GetString(keyPref, "");
    if (string.IsNullOrEmpty(key)) { key = Guid.NewGuid().ToString("N"); PlayerPrefs.SetString(keyPref, key); PlayerPrefs.Save(); }

    SetBusy(true);
    wallet.PurchaseWithWallet(skin.PriceCowrie, skin.ItemId, $"Kente Punk skin: {skin.Name}", key, r => {
        SetBusy(false);
        if (r.Success && !r.RequiresApproval) {
            PlayerPrefs.DeleteKey(keyPref);                 // settled — next purchase gets a fresh key
            Grant(skin);
        } else if (r.RequiresApproval) {
            Say("Purchase needs approval.");                // accepted but NOT paid: do not grant, keep the key
        } else if (r.Retryable) {
            Say("Couldn't confirm the purchase. Try again, you won't be charged twice.");   // same key next time
        } else {
            PlayerPrefs.DeleteKey(keyPref);                 // definite rejection: a later attempt is a new purchase
            Say(string.IsNullOrEmpty(r.Error) ? $"Purchase failed ({r.HttpStatus})." : r.Error);
        }
    });
}
```

KentePunk decides "ambiguous" the same way the SDK's `Retryable` does: no response, or a
5xx. It notes in code that Jiwe has no balance endpoint, so "can't afford it" shows up
only as a rejection.

### 1.6 Cloud save

Highest level, a cosmetic-unlock bitmask and a packed progress string, in one User Data
record, with the local save kept authoritative. **Adapted**; the types are in README §11.

```csharp
void PushSave()
{
    var body = JsonUtility.ToJson(new SaveWrite { userData = new Save {
        levelUnlocked = local.Level, unlockedSkinsMask = local.SkinMask, journey = local.Packed, _version = cloudVersion + 1 } });
    wallet.SetUserData(body, r => {
        if (!r.Success) Debug.LogWarning($"Cloud save failed, progress is still saved locally: {r.Error}");
    });
}
```

### What KentePunk learned

- Both "update" signals fail **open**; only a definite answer blocks anyone.
- Count the XP locally first; gate only the Jiwe call.
- Re-check the pool before a claim, but never block on a failed check.
- One persisted key per item; clear it only on a settled success or a definite rejection.
- No backend means every cap is advisory. KentePunk accepts that deliberately.

---

## 2. Sheng Word League — a web game with a backend in front of Jiwe

A Sheng/Swahili word game with daily puzzles, cowrie-priced hints and passes, a daily
airtime pool and prize draws. It isn't a Unity project — it's the same API called from
Node — but it's the right model for a Unity game that grows a backend, and its code is
the clearest statement of the rules. The browser never sees an API key; the Express server
holds it, validates what the player did, and only then calls Jiwe.

### 2.1 Signing in from a server

A confidential client: client authentication goes in an `Authorization: Basic` header, and
`client_id`/`client_secret` must **not** also be in the body. **Verbatim excerpt.**

```js
const basicAuth = Buffer.from(`${process.env.JIWE_CLIENT_ID}:${process.env.JIWE_API_KEY || ''}`).toString('base64')

const tokenRes = await fetch(process.env.JIWE_TOKEN_URL, {
  method: 'POST',
  headers: { 'Content-Type': 'application/x-www-form-urlencoded', Authorization: `Basic ${basicAuth}` },
  body: new URLSearchParams({
    grant_type: 'authorization_code',
    code,
    redirect_uri: process.env.JIWE_REDIRECT_URI,
    code_verifier: codeVerifier,        // PKCE verifier, kept in an httpOnly cookie across the redirect
  }),
})
```

Jiwe rejected sending the credentials both ways at once ("client authentication must only
be provided using one mechanism") and rejected a JSON body outright.

### 2.2 Reading the player's profile from the ID token

Jiwe's `/me` endpoint rejected every access and ID token (confirmed live 2026-09-14), so the
server reads the profile from the ID token's own claims. **Verbatim.**

```js
function decodeIdTokenClaims(idToken) {
  const payload = idToken?.split('.')[1]
  if (!payload) throw new Error('id_token missing or malformed')
  const padded = payload.padEnd(payload.length + ((4 - (payload.length % 4)) % 4), '=')
  return JSON.parse(Buffer.from(padded.replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8'))
}
```

It then keys the player by `sub`. This SDK now does the same on the Unity side
(`JiweAuth.UserInfo`).

### 2.3 Charging the wallet, and telling failures apart

**Verbatim excerpts.**

```js
// Any 4xx other than 429 is Jiwe saying the request itself is bad — a definitive rejection.
// Replaying it with the same Idempotency-Key returns the identical cached rejection forever
// (confirmed live 2026-09-15: a player topped up and retried, and stayed stuck because the
// stored key kept being honored). 429 and 5xx are the only statuses worth retrying with the same key.
export function isDefinitiveRejection(err) {
  return typeof err?.status === 'number' && err.status >= 400 && err.status < 500 && err.status !== 429
}

// 422 = "well formed but could not be applied", the natural fit for "this wallet can't cover it".
// Not confirmed against a real drained wallet — Jiwe documents no insufficient-balance message.
export function isInsufficientFundsError(err) {
  if (err?.status === 422) return true
  return /insufficient|not enough|balance/i.test(err?.message || '')
}

export async function chargeWallet({ idToken, itemId, amount, description, idempotencyKey }) {
  const res = await fetch(`${process.env.JIWE_API_BASE}/purchases/wallet`, {
    method: 'POST',
    headers: { ...gameAuthHeaders(), 'X-API-TOKEN': idToken, 'Idempotency-Key': idempotencyKey, 'Content-Type': 'application/json' },
    body: JSON.stringify({ amount, itemId, description, metadata: {} }),
  })
  // ...throw an Error carrying res.status on !res.ok or type === 'ERROR'
}
```

The comment above `chargeWallet` records a live correction worth remembering: Jiwe's
hand-written "Take a payment" guide shows `entityId`/`entityName`/`gamerWalletId` for this
endpoint; Jiwe **rejected that shape live** ("An X-API-TOKEN identity token is required"),
while the `itemId` shape worked. Player-facing wording never promises "you weren't charged"
after a 5xx, since a 500 can land after the debit.

### 2.4 Reserve before you pay — the part a client can't do

A reward or hint must be claimed **before** the first `await`, so two simultaneous
requests can't both take it, and released if the Jiwe call then fails. **Verbatim excerpt, abridged.**

```js
// Reserved before any await so concurrent requests can't both take it.
addHint(session, today, puzzleId, position)

try {
  const idempotencyKey = crypto.randomUUID()
  await chargeWallet({ idToken: session.idToken, itemId: 'daily-hint', amount: HINT_PRICE_COWRIE,
                       description: `Dokezo ya ${hintNumber} - ${puzzleId} - ${today}`, idempotencyKey })
  res.json({ status: 'revealed', position, letter: puzzle.answer[position] })
} catch (err) {
  removeHint(session, today, puzzleId, position)    // release the reservation: the player wasn't charged
  // ...map the error to player-facing wording
}
```

The same shape guards prizes (`reservePrize` marks a winner's prize claimed *before*
paying; `releasePrize` undoes it if the send fails) and the daily airtime pool
(`routes/claim.js` reserves against one shared pool tally, rolls back on failure, and
re-saves the tally after every step so a server restart can't reset who has been paid).

**Key every "who has been paid" record by `sub`, never by phone number.** Sheng Word
League learned this live (2026-08-03): claims were keyed by phone number, Jiwe's profile
doesn't reliably return one, so the same account looked like two different players across
two attempts and claimed airtime twice for one solved puzzle. The phone number is now only
ever *where the airtime goes*, never *who has already been paid*.

### 2.5 Mock modes for development

Every Jiwe call that costs real money has an environment switch that returns a canned
response, so the whole game runs without a funded wallet:

```js
if (isMockAirtime()) {
  return { type: 'SUCCESS', message: 'Airtime reward sent successfully. (mock)', ledger_transaction_id: `mock-${transactionId}` }
}
```

### What Sheng Word League learned

- Put the key on the server the moment a forged call could cost you.
- Reserve first, pay second, release on failure — and key the reservation by `sub`.
- Classify failures by **status**, and treat only 429/5xx as retry-with-the-same-key.
- Don't depend on `/me`; read the ID token.
- Mock the expensive calls so development doesn't spend money.

---

## 3. JiweChess — a Unity WebGL game that is guest-first

A chess learning app for students. Solo play against the engine, and the guides, never ask
for an account; only multiplayer, the leaderboard and rewards do. Jiwe sign-in happens when a
player chooses a feature that needs it.

### 3.1 Wiring the SDK in without breaking the WebGL redirect

The bootstrap loads credentials from a gitignored asset and runs **before** `JiweAuth.Awake()`
(on WebGL that method resumes an in-flight login using whatever credentials are present at
that moment). It also hooks `OnLoginSuccess` **permanently**, because the WebGL redirect is a
full page reload — a brand-new bootstrap with no memory of the button click. The full
credentials pattern is in README §9. **Adapted** from `JiweAccountBridge`.

```csharp
[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(JiweAuth), typeof(JiweWallet))]
public class JiweAccountBridge : MonoBehaviour
{
    public bool IsConfigured { get; private set; }
    JiweAuth _auth; JiweWallet _wallet;

    void Awake()
    {
        _auth = GetComponent<JiweAuth>(); _wallet = GetComponent<JiweWallet>(); _wallet.auth = _auth;

        // Permanent: after the redirect reloads the page this is the only thing that tells the
        // rest of the app a login completed.
        _auth.OnLoginSuccess += () => Accounts.CompleteSignIn();
        _auth.OnLoginFailed  += msg => Debug.LogWarning($"Jiwe login failed: {msg}");

        var cfg = Resources.Load<JiweCredentialsConfig>("JiweCredentials");
        if (cfg != null && !string.IsNullOrEmpty(cfg.clientId)) {
            _auth.clientId = cfg.clientId; _auth.apiSecret = cfg.apiSecret; _auth.walletResource = cfg.walletResource;
            _wallet.apiUsername = cfg.apiUsername; _wallet.apiKey = cfg.apiKey;
            IsConfigured = true;
        } else Debug.LogWarning("No Jiwe credentials on this build — sign-in and XP are disabled.");
    }
}
```

`loginOnStart` is left **off**: nothing calls Jiwe until the player taps "Continue".

### 3.2 Signing in only when asked, with a way out

```csharp
public void BeginSignIn(Action onSuccess, Action<string> onFailure)
{
    if (!IsConfigured) { onFailure?.Invoke("Jiwe sign-in isn't set up on this build yet."); return; }   // fail fast, don't open a browser to a blank client_id

    void Done()        { _auth.OnLoginSuccess -= Done; _auth.OnLoginFailed -= Failed; onSuccess?.Invoke(); }
    void Failed(string m) { _auth.OnLoginSuccess -= Done; _auth.OnLoginFailed -= Failed; onFailure?.Invoke(m); }
    _auth.OnLoginSuccess += Done; _auth.OnLoginFailed += Failed;
    _auth.Login();
}

public void CancelSignIn() => _auth?.Cancel();      // wired to the gate's Cancel button — stays live the whole time
```

### 3.3 Rewards that quietly do nothing for guests

```csharp
public void AwardXp(int amount, string description, Action<bool> onComplete = null)
{
    if (!IsConfigured || !_auth.IsLoggedIn) { onComplete?.Invoke(false); return; }   // a guest has no token: nothing to credit
    _wallet.GiveXpReward(amount, description, r => {
        if (!r.Success) Debug.LogWarning($"XP award failed: {r.Error}");
        onComplete?.Invoke(r.Success);
    });
}

// App-scoped: works logged out, so a guest can still look at the board.
public void GetLeaderboard(string period, int max, Action<JiweLeaderboardResult> cb)
    => _wallet.GetLeaderboard("xp", max, period, "highest", cb);
```

### 3.4 What the WebGL build taught

- **Login must stay in the same tab.** Unity's `Application.OpenURL` opens a new one on
  WebGL; the original tab sat on "Signing in…" forever. This SDK now navigates in place.
- **The PKCE verifier must survive the redirect.** `PlayerPrefs` is asynchronous on WebGL;
  JiweChess moved it to `localStorage` through a `.jslib` (now `JiweStorage.jslib` in this SDK).
- **The redirect URI must be stable.** JiweChess registers its host's origin plus `/` because a
  host that serves each release under a versioned path would otherwise change the URI every
  release (`JiweAuth.webglRedirectUri`).
- **Token choice changed once.** JiweChess first sent the ID token as `X-API-TOKEN`, then
  switched to the access token (with `walletResource`) after reading Jiwe's token docs;
  other games still send the ID token and it works. `JiweWallet.playerTokenKind` covers both.
- **Credentials load order matters** — `[DefaultExecutionOrder(-100)]`, above.

---

## Patterns that held across all three

1. **Fail open on every startup check.** An update gate, a pool check, a profile fetch:
   only a definite answer from Jiwe blocks the player.
2. **Local state first, Jiwe second.** Count XP, keep saves and remember pending purchase
   keys on the device; treat Jiwe as the thing you sync to.
3. **One idempotency key per logical purchase**, persisted before the call, kept through
   ambiguous failures (no response, 429, 5xx) and replaced after a definite rejection.
4. **Branch on the HTTP status, not the response's `type` or `message`.**
5. **Never treat "accepted" as "paid".** Check `RequiresApproval` and `Status`; store the
   `LedgerTransactionId`; poll with backoff.
6. **A shipped client can't enforce anything.** Decide per feature what a forged call costs,
   and move those behind a server (README §9).
7. **Treat the docs as a claim and a live request as the answer.** Three separate
   discrepancies in this SDK's history (the purchase fields, the client-secret placement, the
   `/me` endpoint) were settled by a real call, not by reading.
