using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Parses the earned-XP response. Only the XP total is read. Any other field, including points,
/// is ignored, so the SDK never exposes a points reward.
/// </summary>
public static class JiweRewardsParser
{
    /// <summary>
    /// Parses `{ "XP": number, ... }` and returns the lifetime earned XP in major units. A missing or
    /// non-numeric XP field is a schema error, never zero, because zero is a real value.
    /// </summary>
    public static decimal ParseEarnedXp(string json)
    {
        JObject body;
        try
        {
            // Decimal parsing keeps fractional amounts exact instead of passing them through binary double.
            using (var reader = new JsonTextReader(new StringReader(json ?? string.Empty))
                   { FloatParseHandling = FloatParseHandling.Decimal })
            {
                body = JObject.Load(reader);
            }
        }
        catch (JsonException ex)
        {
            throw Unexpected("Earned-rewards response was not JSON.", ex);
        }

        var token = body["XP"];
        if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
            throw Unexpected("Earned-rewards response was missing the numeric field 'XP'.");

        decimal value;
        try
        {
            value = token.Value<decimal>();
        }
        catch (Exception ex) when (ex is OverflowException || ex is FormatException || ex is InvalidCastException)
        {
            throw Unexpected("Earned-rewards field 'XP' was not a representable amount.", ex);
        }
        if (value < 0m)
            throw Unexpected("Earned-rewards field 'XP' was negative, which is not a valid total.");
        return value;
    }

    private static JiweException Unexpected(string message, Exception inner = null) =>
        new JiweException(JiweErrorKind.UnexpectedResponse, message, inner: inner);
}

/// <summary>Maps HTTP outcomes from the Jiwe APIs onto the public error model.</summary>
public static class JiweHttpStatus
{
    /// <summary>Returns null for 2xx. Otherwise the exception the caller should throw.</summary>
    public static JiweException ToException(long status, string retryAfterHeader)
    {
        if (status >= 200 && status < 300)
            return null;

        int code = (int)status;
        switch (code)
        {
            case 0:
                return new JiweException(JiweErrorKind.Network, "No response from Jiwe.");
            case 400:
                return new JiweException(JiweErrorKind.InvalidRequest, "Jiwe rejected the request as invalid.", code);
            case 401:
                return new JiweException(JiweErrorKind.Unauthorized, "The access token was rejected.", code);
            case 403:
                return new JiweException(JiweErrorKind.InsufficientScope, "The client is not permitted to perform this read.", code);
            case 429:
                return new JiweException(JiweErrorKind.RateLimited, "Jiwe is rate limiting this client.", code, ParseRetryAfter(retryAfterHeader));
            default:
                if (code >= 500)
                    return new JiweException(JiweErrorKind.ServiceUnavailable, "Jiwe returned a server error (HTTP " + code + ").", code, ParseRetryAfter(retryAfterHeader));
                return new JiweException(JiweErrorKind.UnexpectedResponse, "Jiwe returned an unexpected status (HTTP " + code + ").", code);
        }
    }

    /// <summary>Accepts Retry-After as delta-seconds. HTTP-date values are ignored rather than guessed.</summary>
    internal static TimeSpan? ParseRetryAfter(string header)
    {
        if (!string.IsNullOrWhiteSpace(header) && int.TryParse(header.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);
        return null;
    }
}
