using System;

public enum JiweSessionStatus
{
    SignedOut,
    SigningIn,
    SignedIn,
}

/// <summary>What the SDK holds for one signed-in player. Held in memory only; never persisted.</summary>
public sealed class JiweSessionSnapshot
{
    public string Subject { get; }
    public string AccessToken { get; }
    public DateTime AccessTokenExpiresUtc { get; }

    public JiweSessionSnapshot(string subject, string accessToken, DateTime accessTokenExpiresUtc)
    {
        Subject = subject;
        AccessToken = accessToken;
        AccessTokenExpiresUtc = accessTokenExpiresUtc;
    }
}

/// <summary>
/// Session state machine with a generation counter. Every sign-in attempt, sign-out and account
/// change advances the generation, so a callback or response captured earlier is recognised as
/// obsolete and rejected. Every transition that changes <see cref="Status"/> raises
/// <see cref="StatusChanged"/>, including expiry detected during a read. Single-threaded: use it
/// only on the Unity main thread.
/// </summary>
public sealed class JiweSessionCore
{
    /// <summary>Safety margin subtracted from the server's expires_in, so a token is never used at its edge.</summary>
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    private long _generation;
    private JiweSessionSnapshot _session;
    private JiweSessionStatus _status = JiweSessionStatus.SignedOut;

    /// <summary>Raised after every change to <see cref="Status"/>.</summary>
    public event Action StatusChanged;

    public JiweSessionStatus Status { get { ExpireIfDue(DateTime.UtcNow); return _status; } }

    public long Generation => _generation;

    /// <summary>The current session, or null. Only non-null while <see cref="Status"/> is SignedIn.</summary>
    public JiweSessionSnapshot Current { get { ExpireIfDue(DateTime.UtcNow); return _status == JiweSessionStatus.SignedIn ? _session : null; } }

    /// <summary>Starts an attempt and returns its generation. Previously captured work becomes obsolete.</summary>
    public long BeginSignIn()
    {
        long generation = ++_generation;
        SetStatus(JiweSessionStatus.SigningIn);
        return generation;
    }

    public bool IsCurrent(long generation) => generation == _generation;

    /// <summary>Commits a successful attempt. Returns false, changing nothing, if the attempt is obsolete.</summary>
    public bool CompleteSignIn(long generation, JiweSessionSnapshot snapshot)
    {
        if (!IsCurrent(generation) || _status != JiweSessionStatus.SigningIn)
            return false;
        _session = snapshot;
        SetStatus(JiweSessionStatus.SignedIn);
        return true;
    }

    /// <summary>
    /// Abandons a failed or cancelled attempt. A previously valid session is restored so a failed
    /// re-login does not sign the player out. Obsolete attempts are ignored.
    /// </summary>
    public bool AbortSignIn(long generation)
    {
        if (!IsCurrent(generation) || _status != JiweSessionStatus.SigningIn)
            return false;
        if (_session != null && DateTime.UtcNow < _session.AccessTokenExpiresUtc - ExpiryMargin)
        {
            SetStatus(JiweSessionStatus.SignedIn);
        }
        else
        {
            _session = null;
            SetStatus(JiweSessionStatus.SignedOut);
        }
        return true;
    }

    /// <summary>Clears the session locally and invalidates all outstanding work. Does not contact the provider.</summary>
    public void SignOut()
    {
        _generation++;
        _session = null;
        SetStatus(JiweSessionStatus.SignedOut);
    }

    /// <summary>Returns the session if it can authorise a call now. Expired sessions are cleared.</summary>
    /// <exception cref="JiweException">NotSignedIn or SessionExpired.</exception>
    public JiweSessionSnapshot RequireUsable(DateTime nowUtc)
    {
        if (_status != JiweSessionStatus.SignedIn || _session == null)
            throw new JiweException(JiweErrorKind.NotSignedIn, "No player is signed in.");
        if (nowUtc >= _session.AccessTokenExpiresUtc - ExpiryMargin)
        {
            SignOut();
            throw new JiweException(JiweErrorKind.SessionExpired, "The player's session has expired.");
        }
        return _session;
    }

    /// <summary>Converts a server expires_in into an absolute expiry time.</summary>
    public static DateTime ComputeExpiry(DateTime nowUtc, int expiresInSeconds) =>
        nowUtc.AddSeconds(expiresInSeconds);

    /// <summary>
    /// Ends a session whose access token has lapsed, so status and identity never report an expired
    /// player as signed in. Raises StatusChanged when it does.
    /// </summary>
    public void ExpireIfDue(DateTime nowUtc)
    {
        if (_status == JiweSessionStatus.SignedIn && _session != null
            && nowUtc >= _session.AccessTokenExpiresUtc - ExpiryMargin)
        {
            SignOut();
        }
    }

    private void SetStatus(JiweSessionStatus status)
    {
        _status = status;
        var handlers = StatusChanged;
        if (handlers == null) return;
        // One faulty subscriber must not corrupt the state machine or mask the real outcome.
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception) { }
        }
    }
}
