using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Canonical Jiwe Quickstart. Attach to a GameObject, paste your public client ID into the
/// Inspector, and press Play. It uses only the public Jiwe API: no HTTP, no tokens, no API keys.
/// The XP award button requires bearer XP writes enabled for this game.
/// </summary>
public sealed class JiweQuickstartSample : MonoBehaviour
{
    [Tooltip("Public client ID issued by Jiwe. This is not a secret. Never put an API key or client secret in a Unity build.")]
    [SerializeField] private string clientId = "";

    private CancellationTokenSource _operation;
    private bool _busy;
    private string _status = "Not initialised.";
    private string _error = "";
    private string _xp = "";
    private JiweLeaderboard _board;
    private Guid? _pendingAward;
    private string _awardPlayer;

    private async void Start()
    {
        try
        {
            await JiweSdk.InitializeAsync(new JiweConfig { ClientId = clientId });
            _status = "Initialised. Sign in to continue.";
        }
        catch (JiweException e)
        {
            ShowError(e);
        }
    }

    private void OnDestroy()
    {
        // Ends any browser sign-in still waiting, and clears the local session.
        _operation?.Cancel();
        JiweSdk.Shutdown();
    }

    private void OnGUI()
    {
        const float x = 16f, width = 320f, buttonHeight = 40f;
        float y = 16f;

        GUI.Label(new Rect(x, y, width, 24f), "Jiwe Quickstart");
        y += 28f;
        GUI.Label(new Rect(x, y, width * 2, 24f), "Status: " + StatusLine());
        y += 28f;

        if (_busy)
        {
            if (GUI.Button(new Rect(x, y, width, buttonHeight), "Cancel")) _operation?.Cancel();
            y += buttonHeight + 8f;
            GUI.Label(new Rect(x, y, width, 24f), "Working…");
            y += 28f;
        }
        else
        {
            bool signedIn = JiweSdk.Auth.IsSignedIn;
            bool ready = JiweSdk.IsInitialized;

            GUI.enabled = ready && !signedIn;
            if (GUI.Button(new Rect(x, y, width, buttonHeight), "Sign in")) Run(SignInAsync);
            GUI.enabled = true;
            y += buttonHeight + 8f;

            GUI.enabled = ready && signedIn;
            if (GUI.Button(new Rect(x, y, width, buttonHeight), "Read earned XP")) Run(ReadXpAsync);
            y += buttonHeight + 8f;
            if (GUI.Button(new Rect(x, y, width, buttonHeight), "Award sample XP")) Run(AwardXpAsync);
            y += buttonHeight + 8f;
            if (GUI.Button(new Rect(x, y, width, buttonHeight), "Read leaderboard")) Run(ReadBoardAsync);
            y += buttonHeight + 8f;
            if (GUI.Button(new Rect(x, y, width, buttonHeight), "Switch account")) Run(SwitchAccountAsync);
            y += buttonHeight + 8f;
            GUI.enabled = true;

            GUI.enabled = signedIn;
            if (GUI.Button(new Rect(x, y, width, buttonHeight), "Sign out")) Run(SignOutAsync);
            y += buttonHeight + 8f;
            GUI.enabled = true;
        }

        if (!string.IsNullOrEmpty(_xp))
        {
            GUI.Label(new Rect(x, y, width * 2, 24f), _xp);
            y += 28f;
        }
        if (_board != null)
        {
            foreach (var entry in _board.Entries)
            {
                GUI.Label(new Rect(x, y, width * 2, 24f), $"{entry.Rank}. {entry.DisplayName}   {entry.Score:0.##} XP");
                y += 24f;
            }
            if (_board.Caller != null)
            {
                GUI.Label(new Rect(x, y, width * 2, 24f), $"Your rank: {_board.Caller.Rank}");
                y += 24f;
            }
        }
        if (!string.IsNullOrEmpty(_error))
        {
            GUI.Label(new Rect(x, y, width * 2, 48f), _error);
        }
    }

    private async Task SignInAsync(CancellationToken ct)
    {
        var player = await JiweSdk.Auth.SignInAsync(ct);
        _status = "Signed in as " + player.Id;
    }

    private async Task ReadXpAsync(CancellationToken ct)
    {
        var xp = await JiweSdk.Rewards.GetEarnedXpAsync(ct);
        _xp = "Earned XP: " + xp.Amount.ToString("0.##") + " " + xp.Unit + " (lifetime, not spendable)";
    }

    private async Task AwardXpAsync(CancellationToken ct)
    {
        var player = JiweSdk.Auth.PlayerId;
        if (_pendingAward == null || _awardPlayer != player)
        {
            _pendingAward = Guid.NewGuid();
            _awardPlayer = player;
        }
        await JiweSdk.Rewards.AwardXpAsync(1.5m, "Quickstart sample", _pendingAward.Value, cancellationToken: ct);
        _pendingAward = null;
        await ReadBoardAsync(ct);
    }

    private async Task ReadBoardAsync(CancellationToken ct)
    {
        _board = await JiweSdk.Leaderboard.GetLeaderboardAsync(5, ct);
    }

    private async Task SwitchAccountAsync(CancellationToken ct)
    {
        _xp = "";
        _board = null;
        var player = await JiweSdk.Auth.SwitchAccountAsync(ct);
        _status = "Switched to " + player.Id;
    }

    private async Task SignOutAsync(CancellationToken ct)
    {
        await JiweSdk.Auth.SignOutAsync();
        _xp = "";
        _board = null;
        _status = "Signed out on this device.";
    }

    /// <summary>Runs one operation at a time and turns every failure into a readable message.</summary>
    private async void Run(Func<CancellationToken, Task> operation)
    {
        if (_busy) return;
        _busy = true;
        _error = "";
        _operation = new CancellationTokenSource();
        try
        {
            await operation(_operation.Token);
        }
        catch (OperationCanceledException)
        {
            _error = "Cancelled.";
        }
        catch (JiweException e)
        {
            ShowError(e);
        }
        finally
        {
            _busy = false;
            _operation.Dispose();
            _operation = null;
        }
    }

    private void ShowError(JiweException e)
    {
        var wait = e.RetryAfter.HasValue ? " Wait " + Math.Ceiling(e.RetryAfter.Value.TotalSeconds) + " s." : "";
        _error = e.Kind + ": " + e.Message + " " + e.Recovery + wait;
    }

    private string StatusLine()
    {
        if (JiweSdk.Auth.IsSignedIn) return "Signed in (" + JiweSdk.Auth.PlayerId + ")";
        return _status;
    }
}
