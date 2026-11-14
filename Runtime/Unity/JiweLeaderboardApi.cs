using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class JiweLeaderboardPlacement
{
    [JsonProperty("rank", Required = Required.Always)]
    public int Rank { get; private set; }

    [JsonProperty("score", Required = Required.Always)]
    public decimal Score { get; private set; }
}

public sealed class JiweLeaderboardEntry : JiweLeaderboardPlacement
{
    [JsonProperty("displayName", Required = Required.Always)]
    public string DisplayName { get; private set; }

    [JsonProperty("isCaller", Required = Required.Always)]
    public bool IsCaller { get; private set; }
}

public sealed class JiweLeaderboard
{
    [JsonProperty("totalEntries", Required = Required.Always)]
    public int TotalEntries { get; private set; }

    [JsonProperty("entries", Required = Required.Always)]
    public IReadOnlyList<JiweLeaderboardEntry> Entries { get; private set; }

    [JsonProperty("caller", Required = Required.AllowNull)]
    public JiweLeaderboardPlacement Caller { get; private set; }
}

public sealed class JiweLeaderboardApi
{
    public async Task<JiweLeaderboard> GetLeaderboardAsync(
        int limit = 10, CancellationToken cancellationToken = default)
    {
        if (limit < 1 || limit > 100)
            throw new JiweException(JiweErrorKind.InvalidRequest, "Leaderboard limit must be between 1 and 100.");

        var url = JiweEndpoints.AbacusApi + "/oidc/leaderboard?limit=" +
            limit.ToString(CultureInfo.InvariantCulture);
        var response = await JiweXpRequest.SendAsync(
            token => JiweHttpClient.GetAsync(url, token, cancellationToken));
        try
        {
            var body = JiweXpRequest.Parse(response.Body);
            if (body["type"]?.Value<string>() != "OK" || body["period"]?.Value<string>() != "all")
                throw new JsonException("Unexpected leaderboard response.");
            return body.ToObject<JiweLeaderboard>();
        }
        catch (JsonException e)
        {
            throw new JiweException(JiweErrorKind.UnexpectedResponse, "Invalid leaderboard response.", inner: e);
        }
    }
}

internal static class JiweXpRequest
{
    internal static async Task<JiweHttpResponse> SendAsync(Func<string, Task<JiweHttpResponse>> send)
    {
        JiweRuntime.RequireInitialized();
        var session = JiweRuntime.Session;
        long generation = session.Generation;
        var snapshot = session.RequireUsable(DateTime.UtcNow);
        var response = await send(snapshot.AccessToken);

        session.ExpireIfDue(DateTime.UtcNow);
        if (!session.IsCurrent(generation))
            throw new JiweException(JiweErrorKind.SessionChanged, "The signed-in account changed while the request ran.");

        if (response.Status == 401) session.SignOut();
        if (response.Status == 403 || response.Status == 404)
            throw new JiweException(JiweErrorKind.ApplicationUnavailable, "XP operation unavailable for this game.", (int)response.Status);
        if (response.Status == 409)
            throw new JiweException(JiweErrorKind.AwardConflict, "This award ID was used with different details.", 409);
        var error = JiweHttpStatus.ToException(response.Status, response.RetryAfter);
        if (error != null) throw error;
        return response;
    }

    internal static JObject Parse(string json)
    {
        try
        {
            using (var reader = new JsonTextReader(new StringReader(json ?? ""))
                   { FloatParseHandling = FloatParseHandling.Decimal })
                return JObject.Load(reader);
        }
        catch (JsonException e)
        {
            throw new JiweException(JiweErrorKind.UnexpectedResponse, "Invalid XP response.", inner: e);
        }
    }
}
