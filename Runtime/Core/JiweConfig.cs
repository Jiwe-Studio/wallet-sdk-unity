using System;

/// <summary>
/// The only hosts the public SDK may contact. Fixed in code: there are no endpoint
/// overrides, so a build cannot be pointed at an arbitrary issuer or API.
/// </summary>
public static class JiweEndpoints
{
    public const string Issuer = "https://id.jiwe.io";
    public const string Authorization = "https://id.jiwe.io/auth";
    public const string Token = "https://id.jiwe.io/token";
    public const string Jwks = "https://id.jiwe.io/jwks";

    /// <summary>Abacus resource audience; must be sent as the OAuth `resource` parameter.</summary>
    public const string ResourceIndicator = "https://api.jiwe.io";

    public const string AbacusApi = "https://abacus.jiwe.io/rest/api/v1";
}

/// <summary>Player-facing configuration. Holds only public values; never place a secret here.</summary>
public sealed class JiweConfig
{
    /// <summary>Loopback redirect used on Standalone desktop builds. Register this exact URI for the client.</summary>
    public const string DefaultLoopbackRedirectUri = "http://127.0.0.1:51772/callback";

    /// <summary>Public client identifier issued by Jiwe. Not a secret.</summary>
    public string ClientId;

    /// <summary>Exact redirect URI registered for this client. Must match the registration character for character.</summary>
    public string RedirectUri = DefaultLoopbackRedirectUri;

    /// <summary>
    /// Returns null when valid, otherwise a developer-facing description of the first problem.
    /// </summary>
    public string Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
            return "ClientId is empty. Set the public client ID issued for this game.";
        if (ClientId.Length > 128)
            return "ClientId is longer than 128 characters.";
        foreach (var c in ClientId)
        {
            if (char.IsWhiteSpace(c) || c < 0x21 || c > 0x7E)
                return "ClientId contains whitespace or non-ASCII characters.";
        }

        return ValidateRedirectUri(RedirectUri);
    }

    /// <summary>
    /// Accepts only http://127.0.0.1:&lt;port&gt;/&lt;path&gt; in this release. Custom-scheme
    /// mobile redirects are not supported yet, so they are rejected rather than half-configured.
    /// </summary>
    public static string ValidateRedirectUri(string redirectUri)
    {
        if (string.IsNullOrEmpty(redirectUri))
            return "RedirectUri is empty.";
        if (redirectUri.Contains("*"))
            return "RedirectUri must be exact; wildcards are not allowed.";
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
            return "RedirectUri is not an absolute URI.";
        if (!string.IsNullOrEmpty(uri.Fragment))
            return "RedirectUri must not contain a fragment.";
        if (!string.IsNullOrEmpty(uri.Query))
            return "RedirectUri must not contain a query string; the SDK adds its own parameters.";
        if (uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1")
            return "On Standalone builds RedirectUri must be http://127.0.0.1:<port>/<path>. Mobile and WebGL redirects are not supported in this release.";
        // The port must be written out. Otherwise the parser silently fills in 80, a privileged port.
        if (!System.Text.RegularExpressions.Regex.IsMatch(redirectUri, @"^http://127\.0\.0\.1:[0-9]{1,5}/"))
            return "RedirectUri must include an explicit port, for example http://127.0.0.1:51772/callback.";
        if (uri.Port <= 0 || uri.Port > 65535)
            return "RedirectUri must include a valid port.";
        if (uri.AbsolutePath == "/" || uri.AbsolutePath.Length < 2)
            return "RedirectUri must include a non-root path such as /callback.";
        return null;
    }
}
