using System;
using System.Security.Cryptography;
using System.Text;

/// <summary>PKCE (RFC 7636) and random-value helpers for the Authorization Code flow.</summary>
public static class JiwePkce
{
    /// <summary>Creates a 43-character verifier from 32 random bytes (the RFC 7636 minimum length).</summary>
    public static string CreateVerifier() => Base64UrlEncode(RandomBytes(32));

    /// <summary>S256 challenge: BASE64URL(SHA256(ASCII(verifier))).</summary>
    public static string CreateChallenge(string verifier)
    {
        if (!IsValidVerifier(verifier))
            throw new ArgumentException("PKCE verifier must be 43–128 unreserved characters.", nameof(verifier));

        using (var sha = SHA256.Create())
        {
            return Base64UrlEncode(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }
    }

    public static string CreateState() => Base64UrlEncode(RandomBytes(32));

    public static string CreateNonce() => Base64UrlEncode(RandomBytes(32));

    public static bool IsValidVerifier(string verifier)
    {
        if (verifier == null || verifier.Length < 43 || verifier.Length > 128)
            return false;
        foreach (var c in verifier)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                      || c == '-' || c == '.' || c == '_' || c == '~';
            if (!ok) return false;
        }
        return true;
    }

    public static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return bytes;
    }
}
