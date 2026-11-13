using System;
using System.Collections.Generic;
using System.Text;

/// <summary>A prepared authorization request. Keep it for the lifetime of one attempt only.</summary>
public sealed class JiweAuthorizationRequest
{
    public string Url;
    public string State;
    public string Nonce;
    public string CodeVerifier;
    public string RedirectUri;
}

/// <summary>Builds the Hermes authorization and token requests and validates the browser callback.</summary>
public static class JiweAuthorization
{
    /// <summary>
    /// Least-privilege scope for this release. `profile` and `user-data:read` are excluded because
    /// public native clients are not permitted to request them (see docs/authentication.md).
    /// </summary>
    public const string Scope = "openid rewards:read";

    public static JiweAuthorizationRequest Create(string clientId, string redirectUri, bool forceAccountChooser)
    {
        var verifier = JiwePkce.CreateVerifier();
        var state = JiwePkce.CreateState();
        var nonce = JiwePkce.CreateNonce();

        var query = new List<string>
        {
            "response_type=code",
            "client_id=" + Escape(clientId),
            "redirect_uri=" + Escape(redirectUri),
            "scope=" + Escape(Scope),
            "state=" + Escape(state),
            "nonce=" + Escape(nonce),
            "code_challenge=" + Escape(JiwePkce.CreateChallenge(verifier)),
            "code_challenge_method=S256",
            "resource=" + Escape(JiweEndpoints.ResourceIndicator),
        };
        if (forceAccountChooser)
        {
            query.Add("prompt=login");
        }

        return new JiweAuthorizationRequest
        {
            Url = JiweEndpoints.Authorization + "?" + string.Join("&", query),
            State = state,
            Nonce = nonce,
            CodeVerifier = verifier,
            RedirectUri = redirectUri,
        };
    }

    /// <summary>Form body for the authorization_code exchange. Public clients send no client secret.</summary>
    public static string BuildTokenRequestBody(string clientId, string code, string redirectUri, string codeVerifier)
    {
        return string.Join("&", new[]
        {
            "grant_type=authorization_code",
            "code=" + Escape(code),
            "redirect_uri=" + Escape(redirectUri),
            "client_id=" + Escape(clientId),
            "code_verifier=" + Escape(codeVerifier),
            "resource=" + Escape(JiweEndpoints.ResourceIndicator),
        });
    }

    /// <summary>
    /// Validates the callback URL and returns the authorization code. Order matters:
    /// the path and state are checked before any error parameter is trusted, so a stale or
    /// forged callback can never be mistaken for the current attempt's outcome.
    /// </summary>
    /// <exception cref="JiweException">LoginFailed, LoginCancelled or UnexpectedResponse.</exception>
    public static string ValidateCallback(string callbackUrl, string redirectUri, string expectedState)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var callback))
            throw Failure("The sign-in callback was not a valid URL.");
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var expected))
            throw new JiweException(JiweErrorKind.InvalidConfiguration, "RedirectUri is not valid.");

        if (callback.Scheme != expected.Scheme || callback.Host != expected.Host
            || callback.Port != expected.Port || callback.AbsolutePath != expected.AbsolutePath)
        {
            throw Failure("The sign-in callback did not arrive at the registered redirect URI.");
        }

        var parameters = ParseQuery(callback.Query);

        if (!parameters.TryGetValue("state", out var state) || !ConstantTimeEquals(state, expectedState))
        {
            throw new JiweException(JiweErrorKind.LoginSuperseded, "The sign-in callback belongs to another attempt.");
        }

        if (parameters.TryGetValue("error", out var error))
        {
            if (error == "access_denied")
                throw new JiweException(JiweErrorKind.LoginCancelled, "The player declined sign-in.");
            throw Failure("Sign-in failed with error code '" + Truncate(error, 64) + "'.");
        }

        // RFC 9207: the authorization server advertises authorization_response_iss_parameter_supported,
        // so the issuer must be present and exact.
        if (!parameters.TryGetValue("iss", out var iss) || iss != JiweEndpoints.Issuer)
            throw Failure("The sign-in callback came from an unexpected issuer.");

        if (!parameters.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            throw Failure("The sign-in callback did not include an authorization code.");

        return code;
    }

    /// <summary>Parses a query string strictly: duplicate keys are rejected rather than resolved.</summary>
    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
            return result;

        var trimmed = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
        foreach (var pair in trimmed.Split('&'))
        {
            if (pair.Length == 0) continue;
            var eq = pair.IndexOf('=');
            var rawKey = eq >= 0 ? pair.Substring(0, eq) : pair;
            var rawValue = eq >= 0 ? pair.Substring(eq + 1) : string.Empty;
            var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            var value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
            if (result.ContainsKey(key))
                throw Failure("The sign-in callback repeated the parameter '" + Truncate(key, 32) + "'.");
            result[key] = value;
        }
        return result;
    }

    /// <summary>
    /// True when the callback query carries exactly this attempt's state. The loopback listener uses it
    /// to ignore stray or forged requests instead of ending the attempt. Never throws.
    /// </summary>
    public static bool IsCallbackForState(string query, string expectedState)
    {
        try
        {
            var parameters = ParseQuery(query);
            return parameters.TryGetValue("state", out var state) && ConstantTimeEquals(state, expectedState);
        }
        catch (JiweException)
        {
            return false;
        }
    }

    internal static bool ConstantTimeEquals(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static string Escape(string value) => Uri.EscapeDataString(value ?? string.Empty);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max);

    private static JiweException Failure(string message) =>
        new JiweException(JiweErrorKind.LoginFailed, message);
}
