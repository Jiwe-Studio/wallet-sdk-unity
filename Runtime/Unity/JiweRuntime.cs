using System;
using System.Threading;

/// <summary>Process-wide SDK state. Internal so the public surface stays small.</summary>
internal static class JiweRuntime
{
    private static CancellationTokenSource _lifetime = new CancellationTokenSource();

    internal static readonly JiweSessionCore Session = new JiweSessionCore();

    internal static JiweConfig Config { get; set; }

    /// <summary>Cancelled by JiweSdk.Shutdown(); every sign-in attempt links to this token.</summary>
    internal static CancellationToken LifetimeToken => _lifetime.Token;

    internal static void RequireInitialized()
    {
        if (Config == null)
            throw new JiweException(JiweErrorKind.InvalidConfiguration, "JiweSdk.InitializeAsync must be awaited before using the SDK.");
    }

    internal static void CancelLifetime()
    {
        var previous = _lifetime;
        _lifetime = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
    }
}
