using System.Threading.Tasks;

/// <summary>
/// Entry point for the Jiwe Unity SDK.
/// <code>
/// await JiweSdk.InitializeAsync(new JiweConfig { ClientId = "YOUR_PUBLIC_CLIENT_ID" });
/// var player = await JiweSdk.Auth.SignInAsync();
/// var xp = await JiweSdk.Rewards.GetEarnedXpAsync();
/// await JiweSdk.Auth.SignOutAsync();
/// </code>
/// No API key or client secret is used anywhere in this entry point.
/// </summary>
public static class JiweSdk
{
    /// <summary>Player sign-in, account switching and sign-out.</summary>
    public static readonly JiweAuthApi Auth = new JiweAuthApi();

    /// <summary>Reads of the signed-in player's earned totals.</summary>
    public static readonly JiweRewardsApi Rewards = new JiweRewardsApi();

    /// <summary>Reads the signed-in player's game leaderboard.</summary>
    public static readonly JiweLeaderboardApi Leaderboard = new JiweLeaderboardApi();

    public static bool IsInitialized => JiweRuntime.Config != null;

    /// <summary>
    /// Validates and stores the configuration. Calling it again replaces the configuration and
    /// ends any current session, so a changed ClientId can never inherit the previous account.
    /// </summary>
    /// <exception cref="JiweException">InvalidConfiguration when the config is incomplete or unsafe.</exception>
    public static Task InitializeAsync(JiweConfig config)
    {
        if (config == null)
            throw new JiweException(JiweErrorKind.InvalidConfiguration, "JiweConfig is null.");

        var problem = config.Validate();
        if (problem != null)
            throw new JiweException(JiweErrorKind.InvalidConfiguration, problem);

        if (JiweRuntime.Config != null)
            Shutdown();

        JiweRuntime.Config = new JiweConfig
        {
            ClientId = config.ClientId,
            RedirectUri = config.RedirectUri,
        };
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels any sign-in in progress, clears the local session and stops accepting results from
    /// earlier work. Call from a MonoBehaviour's OnDestroy or on application quit. Does not contact
    /// the identity provider.
    /// </summary>
    public static void Shutdown()
    {
        JiweRuntime.CancelLifetime();
        JiweRuntime.Session.SignOut();
        JiweRuntime.Config = null;
    }
}

/// <summary>The signed-in player. Holds only a verified, stable identifier.</summary>
public sealed class JiwePlayer
{
    /// <summary>Stable player identifier (the verified `sub` claim). Use it as a key, not as a display name.</summary>
    public string Id { get; }

    internal JiwePlayer(string id)
    {
        Id = id;
    }
}
