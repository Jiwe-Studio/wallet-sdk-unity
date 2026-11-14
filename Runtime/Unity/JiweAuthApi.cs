using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Player sign-in with Authorization Code + PKCE. Each attempt has its own generation, so a late
/// browser callback from an earlier attempt, or a response from a previous account, is discarded.
/// </summary>
public sealed class JiweAuthApi
{
    /// <summary>How long the player has to finish the browser sign-in.</summary>
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Clock tolerance applied to ID token time claims.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private CancellationTokenSource _attempt;

    public JiweAuthApi()
    {
        JiweRuntime.Session.StatusChanged += () => StatusChanged?.Invoke();
    }

    /// <summary>Current state: SignedOut, SigningIn or SignedIn.</summary>
    public JiweSessionStatus Status => JiweRuntime.Session.Status;

    public bool IsSignedIn => Status == JiweSessionStatus.SignedIn;

    /// <summary>The signed-in player's identifier, or null when no player is signed in.</summary>
    public string PlayerId => JiweRuntime.Session.Current?.Subject;

    /// <summary>Raised whenever <see cref="Status"/> changes, including expiry and sign-out.</summary>
    public event Action StatusChanged;

    /// <summary>Signs a player in through the browser.</summary>
    /// <exception cref="JiweException">LoginCancelled, LoginFailed, LoginSuperseded or Timeout.</exception>
    public Task<JiwePlayer> SignInAsync(CancellationToken cancellationToken = default)
    {
        return SignInInternalAsync(forceAccountChooser: false, cancellationToken);
    }

    /// <summary>
    /// Ends the current session locally, then signs in again with the account chooser forced on, so a
    /// different player can sign in on the same device.
    /// </summary>
    public Task<JiwePlayer> SwitchAccountAsync(CancellationToken cancellationToken = default)
    {
        ClearLocalSession();
        return SignInInternalAsync(forceAccountChooser: true, cancellationToken);
    }

    /// <summary>
    /// Clears the session on this device and cancels any sign-in in progress. It does not end the
    /// provider's browser session, which Jiwe does not guarantee for public clients.
    /// </summary>
    public Task SignOutAsync()
    {
        ClearLocalSession();
        return Task.CompletedTask;
    }

    private async Task<JiwePlayer> SignInInternalAsync(bool forceAccountChooser, CancellationToken cancellationToken)
    {
        JiweRuntime.RequireInitialized();
        var config = JiweRuntime.Config;

        // A new attempt supersedes any attempt still waiting on the browser.
        CancelAttempt();
        var session = JiweRuntime.Session;
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, JiweRuntime.LifetimeToken);
        _attempt = attempt;

        JiweLoopbackListener listener = null;
        long generation = session.BeginSignIn();
        try
        {
            if (!session.IsCurrent(generation))
                throw Superseded();
            attempt.Token.ThrowIfCancellationRequested();
            var request = JiweAuthorization.Create(config.ClientId, config.RedirectUri, forceAccountChooser);

            // Start listening before the browser opens, so an immediate redirect cannot be missed.
            listener = JiweLoopbackListener.Start(config.RedirectUri);
            Application.OpenURL(request.Url);

            var callbackUrl = await listener.WaitForCallbackAsync(attempt.Token, LoginTimeout, request.State);
            listener.Dispose();
            listener = null;

            var code = JiweAuthorization.ValidateCallback(callbackUrl, request.RedirectUri, request.State);

            var tokenResponse = await JiweHttpClient.PostFormAsync(
                JiweEndpoints.Token,
                JiweAuthorization.BuildTokenRequestBody(config.ClientId, code, request.RedirectUri, request.CodeVerifier),
                attempt.Token);
            if (tokenResponse.Status == 0)
            {
                // No response at all is a network problem, not a refused exchange.
                throw JiweHttpStatus.ToException(0, null);
            }
            if (tokenResponse.Status < 200 || tokenResponse.Status >= 300)
            {
                throw new JiweException(JiweErrorKind.LoginFailed,
                    "The token exchange was refused (HTTP " + tokenResponse.Status + ").", (int)tokenResponse.Status);
            }
            var tokens = JiweTokenResponse.Parse(tokenResponse.Body);

            var jwksResponse = await JiweHttpClient.GetAsync(JiweEndpoints.Jwks, null, attempt.Token);
            var jwksException = JiweHttpStatus.ToException(jwksResponse.Status, jwksResponse.RetryAfter);
            if (jwksException != null)
                throw jwksException;

            var identity = JiweIdTokenValidator.Validate(
                tokens.IdToken,
                JiweJwks.Parse(jwksResponse.Body),
                JiweEndpoints.Issuer,
                config.ClientId,
                request.Nonce,
                DateTime.UtcNow,
                ClockSkew);

            var snapshot = new JiweSessionSnapshot(
                identity.Subject,
                tokens.AccessToken,
                JiweSessionCore.ComputeExpiry(DateTime.UtcNow, tokens.ExpiresInSeconds));

            attempt.Token.ThrowIfCancellationRequested();
            if (!session.CompleteSignIn(generation, snapshot) || !session.IsCurrent(generation))
                throw Superseded();

            return new JiwePlayer(identity.Subject);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            if (!session.IsCurrent(generation))
                throw Superseded();
            session.AbortSignIn(generation);
            throw new JiweException(JiweErrorKind.LoginCancelled, "Sign-in was cancelled.");
        }
        catch (JiweException)
        {
            if (session.IsCurrent(generation))
                session.AbortSignIn(generation);
            throw;
        }
        catch (Exception ex)
        {
            if (session.IsCurrent(generation))
                session.AbortSignIn(generation);
            // Generic message only: the underlying exception may contain request details.
            throw new JiweException(JiweErrorKind.LoginFailed, "Sign-in failed unexpectedly.", inner: ex);
        }
        finally
        {
            if (listener != null) listener.Dispose();
            if (ReferenceEquals(_attempt, attempt)) _attempt = null;
            attempt.Dispose();
        }
    }

    private static JiweException Superseded() =>
        new JiweException(JiweErrorKind.LoginSuperseded, "Sign-in was superseded by a newer sign-in or a sign-out.");

    private void CancelAttempt()
    {
        if (_attempt == null) return;
        var previous = _attempt;
        _attempt = null;
        previous.Cancel();
    }

    private void ClearLocalSession()
    {
        var previous = _attempt;
        _attempt = null;
        previous?.Cancel();
        JiweRuntime.Session.SignOut();
    }
}
