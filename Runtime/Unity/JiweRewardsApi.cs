using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

/// <summary>Lifetime earned XP for the signed-in player in this game. Read-only.</summary>
public sealed class JiweEarnedXp
{
    /// <summary>Earned XP in major units. Cumulative earnings, not a spendable balance.</summary>
    public decimal Amount { get; }

    /// <summary>Unit of <see cref="Amount"/>. Always "XP".</summary>
    public string Unit => "XP";

    internal JiweEarnedXp(decimal amount)
    {
        Amount = amount;
    }
}

/// <summary>Reads of the signed-in player's earned XP. Requires an active session.</summary>
public sealed class JiweRewardsApi
{
    /// <summary>Awards game-reported XP. Reuse the same award ID when retrying.</summary>
    public async Task<string> AwardXpAsync(
        decimal xpEarned, string description, Guid awardId, string gameSessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (xpEarned <= 0m || decimal.Round(xpEarned, 2) != xpEarned ||
            awardId == Guid.Empty || description == null)
            throw new JiweException(JiweErrorKind.InvalidRequest, "Provide a positive XP amount, description and award ID.");

        var body = new JObject { ["xpEarned"] = xpEarned, ["description"] = description };
        if (gameSessionId != null) body["gameSessionId"] = gameSessionId;

        var response = await JiweXpRequest.SendAsync(
            token => JiweHttpClient.PostJsonAsync(
                JiweEndpoints.AbacusApi + "/oidc/rewards/xp", token,
                body.ToString(Newtonsoft.Json.Formatting.None), awardId.ToString("D"), cancellationToken));
        var result = JiweXpRequest.Parse(response.Body);
        if (result["type"]?.Value<string>() != "SUCCESS" ||
            !Guid.TryParse(result["ledger_transaction_id"]?.Value<string>(), out var transactionId))
            throw new JiweException(JiweErrorKind.UnexpectedResponse, "XP award response was missing its transaction ID.");
        return transactionId.ToString("D");
    }

    private const string EarnedPath = "/oidc/rewards/earned";

    /// <summary>Reads lifetime earned XP for the signed-in player in this game.</summary>
    /// <exception cref="JiweException">NotSignedIn, SessionExpired, SessionChanged, Unauthorized, InsufficientScope, RateLimited, ServiceUnavailable, Network or UnexpectedResponse.</exception>
    public async Task<JiweEarnedXp> GetEarnedXpAsync(CancellationToken cancellationToken = default)
    {
        JiweRuntime.RequireInitialized();
        var session = JiweRuntime.Session;

        long generation = session.Generation;
        var snapshot = session.RequireUsable(DateTime.UtcNow);

        var response = await JiweHttpClient.GetAsync(
            JiweEndpoints.AbacusApi + EarnedPath,
            snapshot.AccessToken,
            cancellationToken);

        // A result from an earlier account or session must never reach the caller.
        if (!session.IsCurrent(generation))
        {
            throw new JiweException(JiweErrorKind.SessionChanged, "The signed-in account changed while the request ran.");
        }

        if (response.Status == 401)
        {
            // The server rejected the token, so the local session cannot be trusted any further.
            session.SignOut();
        }

        var error = JiweHttpStatus.ToException(response.Status, response.RetryAfter);
        if (error != null)
            throw error;

        return new JiweEarnedXp(JiweRewardsParser.ParseEarnedXp(response.Body));
    }
}
