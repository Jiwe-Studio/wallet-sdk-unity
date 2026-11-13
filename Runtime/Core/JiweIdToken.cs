using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Verified public signing keys from the Hermes JWKS.</summary>
public sealed class JiweJwks
{
    private readonly Dictionary<string, RSAParameters> _keys;

    private JiweJwks(Dictionary<string, RSAParameters> keys)
    {
        _keys = keys;
    }

    public int KeyCount => _keys.Count;

    public static JiweJwks Parse(string json)
    {
        JObject document;
        try
        {
            document = JObject.Parse(json ?? string.Empty);
        }
        catch (JsonException ex)
        {
            throw Unexpected("JWKS was not JSON.", ex);
        }

        var entries = document["keys"] as JArray;
        if (entries == null)
            throw Unexpected("JWKS did not contain a keys array.");

        var keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
        foreach (var item in entries)
        {
            var jwk = item as JObject;
            if (jwk == null || Str(jwk, "kty") != "RSA")
                continue; // Only RSA keys are used; other key types are ignored, not trusted.

            var use = Str(jwk, "use");
            if (use != null && use != "sig")
                continue;
            var alg = Str(jwk, "alg");
            if (alg != null && alg != "RS256")
                continue;

            var kid = Str(jwk, "kid");
            var n = Str(jwk, "n");
            var e = Str(jwk, "e");
            if (kid == null || n == null || e == null)
                continue;

            keys[kid] = new RSAParameters
            {
                Modulus = Base64UrlDecode(n),
                Exponent = Base64UrlDecode(e),
            };
        }

        if (keys.Count == 0)
            throw Unexpected("JWKS contained no usable RSA signing keys.");

        return new JiweJwks(keys);
    }

    internal bool TryGet(string kid, out RSAParameters parameters) =>
        _keys.TryGetValue(kid, out parameters);

    private static string Str(JObject obj, string name)
    {
        var token = obj[name];
        return token != null && token.Type == JTokenType.String ? token.Value<string>() : null;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static JiweException Unexpected(string message, Exception inner = null) =>
        new JiweException(JiweErrorKind.UnexpectedResponse, message, inner: inner);
}

/// <summary>The verified identity of the signed-in player.</summary>
public sealed class JiweIdentity
{
    /// <summary>Stable player identifier (the `sub` claim). Safe to use as a key.</summary>
    public string Subject { get; internal set; }

    /// <summary>When the ID token expires. Informational; the access token governs API calls.</summary>
    public DateTime IdTokenExpiresUtc { get; internal set; }
}

/// <summary>
/// Validates the ID token signature and claims. Claims are never read for a security decision
/// until the signature has been verified against the JWKS.
/// </summary>
public static class JiweIdTokenValidator
{
    /// <summary>Largest ID token accepted; guards against oversized input.</summary>
    public const int MaxTokenLength = 16 * 1024;

    public static JiweIdentity Validate(string idToken, JiweJwks jwks, string issuer, string clientId,
        string expectedNonce, DateTime nowUtc, TimeSpan clockSkew)
    {
        if (string.IsNullOrEmpty(idToken) || idToken.Length > MaxTokenLength)
            throw Invalid("ID token is missing or too large.");
        if (jwks == null)
            throw Invalid("No JWKS was supplied.");

        var parts = idToken.Split('.');
        if (parts.Length != 3)
            throw Invalid("ID token is not a three-part JWT.");

        var header = ParseObject(Base64UrlDecodeToString(parts[0]), "header");
        var payload = ParseObject(Base64UrlDecodeToString(parts[1]), "payload");

        // Algorithm must be exactly RS256. This rejects `none` and downgrade to HMAC or other families.
        if (Str(header, "alg") != "RS256")
            throw Invalid("ID token uses an unsupported signing algorithm.");

        var kid = Str(header, "kid");
        if (kid == null || !jwks.TryGet(kid, out var parameters))
            throw Invalid("ID token was signed with an unknown key.");

        var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var signature = Base64UrlDecodeBytes(parts[2]);
        using (var rsa = RSA.Create())
        {
            rsa.ImportParameters(parameters);
            if (!rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw Invalid("ID token signature did not verify.");
        }

        if (Str(payload, "iss") != issuer)
            throw Invalid("ID token issuer did not match.");

        var audiences = ReadAudience(payload);
        if (!audiences.Contains(clientId))
            throw Invalid("ID token audience did not match this client.");
        if (audiences.Count > 1 && Str(payload, "azp") != clientId)
            throw Invalid("ID token has several audiences and authorised party is not this client.");

        var exp = ReadUnix(payload, "exp");
        if (exp == null)
            throw Invalid("ID token has no expiry.");
        var expiresUtc = DateTime.UnixEpoch.AddSeconds(exp.Value);
        if (nowUtc - clockSkew >= expiresUtc)
            throw Invalid("ID token has expired.");

        var nbf = ReadUnix(payload, "nbf");
        if (nbf != null && nowUtc + clockSkew < DateTime.UnixEpoch.AddSeconds(nbf.Value))
            throw Invalid("ID token is not yet valid.");

        // The nonce binds this ID token to this attempt and prevents replay across logins.
        if (string.IsNullOrEmpty(expectedNonce) || Str(payload, "nonce") != expectedNonce)
            throw Invalid("ID token nonce did not match this sign-in attempt.");

        var subject = Str(payload, "sub");
        if (string.IsNullOrEmpty(subject))
            throw Invalid("ID token has no subject.");

        return new JiweIdentity { Subject = subject, IdTokenExpiresUtc = expiresUtc };
    }

    private static HashSet<string> ReadAudience(JObject payload)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var aud = payload["aud"];
        if (aud == null)
            return set;
        if (aud.Type == JTokenType.String)
        {
            set.Add(aud.Value<string>());
        }
        else if (aud is JArray array)
        {
            foreach (var item in array)
            {
                if (item.Type == JTokenType.String)
                    set.Add(item.Value<string>());
            }
        }
        return set;
    }

    private static long? ReadUnix(JObject payload, string name)
    {
        var token = payload[name];
        if (token == null || token.Type != JTokenType.Integer)
            return null;
        return token.Value<long>();
    }

    private static JObject ParseObject(string json, string what)
    {
        try
        {
            return JObject.Parse(json);
        }
        catch (JsonException ex)
        {
            throw Invalid("ID token " + what + " was not valid JSON.", ex);
        }
    }

    private static string Str(JObject obj, string name)
    {
        var token = obj[name];
        return token != null && token.Type == JTokenType.String ? token.Value<string>() : null;
    }

    private static string Base64UrlDecodeToString(string value) =>
        Encoding.UTF8.GetString(Base64UrlDecodeBytes(value));

    private static byte[] Base64UrlDecodeBytes(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw Invalid("ID token contained invalid base64url.");
        }
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException ex)
        {
            throw Invalid("ID token contained invalid base64url.", ex);
        }
    }

    private static JiweException Invalid(string message, Exception inner = null) =>
        new JiweException(JiweErrorKind.LoginFailed, message, inner: inner);
}
