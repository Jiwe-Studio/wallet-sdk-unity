using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: InternalsVisibleTo("JiweSdkTests")]

public sealed class JiweLapEntry
{
    [JsonProperty("rank", Required = Required.Always)]
    public int Rank { get; private set; }

    [JsonProperty("milliseconds", Required = Required.Always)]
    public int Milliseconds { get; private set; }

    [JsonProperty("isCaller", Required = Required.Always)]
    public bool IsCaller { get; private set; }
}

/// <summary>Public-client access to a server-configured, lowest-time-wins lap board.</summary>
public sealed class JiweLapApi
{
    private const string Path = "/oidc/leaderboard/laps";

    public async Task<int> SubmitAsync(int milliseconds, Guid attemptId, CancellationToken cancellationToken = default)
    {
        if (milliseconds < 1 || attemptId == Guid.Empty)
            throw new JiweException(JiweErrorKind.InvalidRequest, "Provide a positive lap time and an attempt ID.");

        var body = await SendAsync(Path, new JObject
        {
            ["milliseconds"] = milliseconds,
            ["attemptId"] = attemptId.ToString("D"),
        }, cancellationToken);
        return ParseBest(body);
    }

    public async Task<IReadOnlyList<JiweLapEntry>> QueryAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        if (limit < 1 || limit > 100)
            throw new JiweException(JiweErrorKind.InvalidRequest, "Leaderboard limit must be between 1 and 100.");

        var body = await SendAsync(Path + "/query", new JObject { ["limit"] = limit }, cancellationToken);
        return ParseEntries(body, limit);
    }

    internal static int ParseBest(JObject body) => ReadPositiveInteger(body["bestMilliseconds"]);

    internal static IReadOnlyList<JiweLapEntry> ParseEntries(JObject body, int limit)
    {
        if (!(body["entries"] is JArray entries) || entries.Count > limit)
            throw InvalidResponse();

        var result = new List<JiweLapEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (!(entry is JObject row) || row["isCaller"]?.Type != JTokenType.Boolean)
                throw InvalidResponse();
            ReadPositiveInteger(row["rank"]);
            ReadPositiveInteger(row["milliseconds"]);
            result.Add(row.ToObject<JiweLapEntry>());
        }
        return result.AsReadOnly();
    }

    private static async Task<JObject> SendAsync(string path, JObject payload, CancellationToken ct)
    {
        JiweRuntime.RequireInitialized();
        var session = JiweRuntime.Session;
        var snapshot = session.RequireUsable(DateTime.UtcNow);
        var generation = session.Generation;
        var response = await JiweHttpClient.PostJsonAsync(JiweEndpoints.AbacusApi + path,
            snapshot.AccessToken, payload.ToString(Formatting.None), null, ct);

        session.ExpireIfDue(DateTime.UtcNow);
        if (!session.IsCurrent(generation))
            throw new JiweException(JiweErrorKind.SessionChanged, "The signed-in account changed while the request ran.");

        if (response.Status == 401) session.SignOut();
        if (response.Status == 404)
            throw new JiweException(JiweErrorKind.ApplicationUnavailable, "The lap leaderboard is unavailable for this game.", 404);
        var error = JiweHttpStatus.ToException(response.Status, response.RetryAfter);
        if (error != null) throw error;

        JObject body;
        try { body = JObject.Parse(response.Body ?? ""); }
        catch (JsonException) { throw InvalidResponse(); }
        if (body["type"]?.Type != JTokenType.String || (string)body["type"] != "OK")
            throw InvalidResponse();
        return body;
    }

    private static int ReadPositiveInteger(JToken value)
    {
        if (value?.Type != JTokenType.Integer ||
            !int.TryParse(value.ToString(Formatting.None), NumberStyles.None, CultureInfo.InvariantCulture, out var result) ||
            result < 1)
            throw InvalidResponse();
        return result;
    }

    private static JiweException InvalidResponse() =>
        new JiweException(JiweErrorKind.UnexpectedResponse, "Invalid lap leaderboard response.");
}
