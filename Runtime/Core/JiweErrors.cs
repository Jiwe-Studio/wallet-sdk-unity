using System;

/// <summary>Stable categories for every failure the public SDK surfaces.</summary>
public enum JiweErrorKind
{
    InvalidConfiguration,
    LoginCancelled,
    LoginFailed,
    LoginSuperseded,
    NotSignedIn,
    SessionExpired,
    SessionChanged,
    InvalidRequest,
    Unauthorized,
    InsufficientScope,
    RateLimited,
    ServiceUnavailable,
    Network,
    Timeout,
    UnexpectedResponse,
    ApplicationUnavailable,
    AwardConflict,
}

/// <summary>
/// The only exception type thrown by the public SDK. Messages never contain tokens,
/// client secrets or raw provider payloads.
/// </summary>
public sealed class JiweException : Exception
{
    public JiweErrorKind Kind { get; }

    /// <summary>HTTP status when the failure came from a response; 0 otherwise.</summary>
    public int HttpStatus { get; }

    /// <summary>Server-provided wait before retrying (RateLimited / ServiceUnavailable), if any.</summary>
    public TimeSpan? RetryAfter { get; }

    public JiweException(JiweErrorKind kind, string message, int httpStatus = 0, TimeSpan? retryAfter = null, Exception inner = null)
        : base(message, inner)
    {
        Kind = kind;
        HttpStatus = httpStatus;
        RetryAfter = retryAfter;
    }

    /// <summary>Plain-language next step for a developer or support engineer.</summary>
    public string Recovery
    {
        get
        {
            switch (Kind)
            {
                case JiweErrorKind.InvalidConfiguration:
                    return "Set a valid public ClientId and an exact RedirectUri in JiweConfig.";
                case JiweErrorKind.LoginCancelled:
                    return "The player closed the sign-in page. Call SignInAsync again to retry.";
                case JiweErrorKind.LoginFailed:
                    return "Sign-in did not complete. Confirm the RedirectUri is registered exactly for this client, then retry.";
                case JiweErrorKind.LoginSuperseded:
                    return "A newer sign-in replaced this attempt. No action needed.";
                case JiweErrorKind.NotSignedIn:
                    return "Call JiweSdk.Auth.SignInAsync() before reading player data.";
                case JiweErrorKind.SessionExpired:
                    return "The session has expired. Call JiweSdk.Auth.SignInAsync() again. Public clients cannot refresh silently.";
                case JiweErrorKind.SessionChanged:
                    return "The signed-in account changed while the request ran. Discard the result and read again.";
                case JiweErrorKind.InvalidRequest:
                    return "Jiwe rejected the request. This is an integration bug; check the values the SDK sent.";
                case JiweErrorKind.Unauthorized:
                    return "The access token was rejected, so the local session was cleared. Sign in again.";
                case JiweErrorKind.InsufficientScope:
                    return "This client may not read this data. Confirm the client is registered with the rewards:read scope.";
                case JiweErrorKind.RateLimited:
                    return "Too many requests. Wait for RetryAfter before retrying.";
                case JiweErrorKind.ServiceUnavailable:
                    return "Jiwe is temporarily unavailable. Retry later with backoff.";
                case JiweErrorKind.Network:
                    return "Could not reach Jiwe. Check the connection and retry.";
                case JiweErrorKind.Timeout:
                    return "Sign-in was not completed in time. Call SignInAsync again.";
                case JiweErrorKind.UnexpectedResponse:
                    return "Jiwe returned a response this SDK version does not understand. Update the SDK or report the failure.";
                case JiweErrorKind.ApplicationUnavailable:
                    return "Confirm the public client mapping and that this XP operation is enabled for the game.";
                case JiweErrorKind.AwardConflict:
                    return "Retry with the original award details. Use a new award ID for a new award.";
                default:
                    return "Unexpected SDK error.";
            }
        }
    }
}
