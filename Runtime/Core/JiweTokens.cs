using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Parsed token endpoint response. The ID token authenticates the player; the access token
/// is a bearer token for the Abacus resource only. They are never interchanged.
/// </summary>
public sealed class JiweTokenResponse
{
    public string AccessToken { get; private set; }
    public string IdToken { get; private set; }
    public int ExpiresInSeconds { get; private set; }
    public string Scope { get; private set; }

    public static JiweTokenResponse Parse(string json)
    {
        JObject body;
        try
        {
            body = JObject.Parse(json ?? string.Empty);
        }
        catch (JsonException ex)
        {
            throw Unexpected("Token response was not JSON.", ex);
        }

        var accessToken = StringField(body, "access_token");
        var idToken = StringField(body, "id_token");

        var tokenType = StringField(body, "token_type");
        if (!string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw Unexpected("Token response did not declare token_type Bearer.");

        var expiresToken = body["expires_in"];
        if (expiresToken == null || expiresToken.Type != JTokenType.Integer)
            throw Unexpected("Token response did not include an integer expires_in.");
        long expires = expiresToken.Value<long>();
        if (expires <= 0 || expires > 86400)
            throw Unexpected("Token response expires_in was outside the accepted range.");

        return new JiweTokenResponse
        {
            AccessToken = accessToken,
            IdToken = idToken,
            ExpiresInSeconds = (int)expires,
            Scope = body["scope"]?.Type == JTokenType.String ? body["scope"].Value<string>() : null,
        };
    }

    private static string StringField(JObject body, string name)
    {
        var token = body[name];
        if (token == null || token.Type != JTokenType.String)
            throw Unexpected("Token response was missing the string field '" + name + "'.");
        var value = token.Value<string>();
        if (string.IsNullOrEmpty(value))
            throw Unexpected("Token response field '" + name + "' was empty.");
        return value;
    }

    private static JiweException Unexpected(string message, Exception inner = null) =>
        new JiweException(JiweErrorKind.UnexpectedResponse, message, inner: inner);
}
