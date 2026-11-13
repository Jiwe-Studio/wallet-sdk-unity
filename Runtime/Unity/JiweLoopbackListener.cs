using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Single-use loopback listener for the browser redirect on Standalone builds. It answers only the
/// registered path, returns a neutral page, and releases its socket on every path, so a retry on the
/// same port is never blocked by a leaked listener.
/// </summary>
internal sealed class JiweLoopbackListener : IDisposable
{
    private const string NeutralPage =
        "<!doctype html><html><head><meta charset='utf-8'><title>Jiwe</title></head>" +
        "<body style='font-family:sans-serif'><p>Return to the game to finish signing in.</p></body></html>";

    private readonly HttpListener _listener;
    private readonly string _redirectUri;
    private readonly string _path;
    private bool _disposed;

    private JiweLoopbackListener(HttpListener listener, string redirectUri, string path)
    {
        _listener = listener;
        _redirectUri = redirectUri;
        _path = path;
    }

    /// <summary>Starts listening on the registered loopback port.</summary>
    /// <exception cref="JiweException">LoginFailed when the port cannot be bound.</exception>
    internal static JiweLoopbackListener Start(string redirectUri)
    {
        var uri = new Uri(redirectUri);
        var listener = new HttpListener();
        listener.Prefixes.Add(uri.Scheme + "://" + uri.Host + ":" + uri.Port + "/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            listener.Close();
            throw new JiweException(JiweErrorKind.LoginFailed,
                "Could not listen on the registered redirect port " + uri.Port + ". Another program may be using it.", inner: ex);
        }
        return new JiweLoopbackListener(listener, redirectUri, uri.AbsolutePath);
    }

    /// <summary>
    /// Waits for the browser to return to the registered path. The callback URL is rebuilt from the
    /// registered redirect URI plus the received query, so a spoofed Host header cannot alter it.
    /// </summary>
    internal async Task<string> WaitForCallbackAsync(CancellationToken ct, TimeSpan timeout, string expectedState)
    {
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(timeout);
            using (deadline.Token.Register(StopListenerQuietly))
            {
                while (true)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception) when (deadline.IsCancellationRequested)
                    {
                        if (ct.IsCancellationRequested)
                            throw new OperationCanceledException(ct);
                        throw new JiweException(JiweErrorKind.Timeout, "Sign-in timed out waiting for the browser.");
                    }

                    if (context.Request.Url.AbsolutePath != _path || context.Request.HttpMethod != "GET")
                    {
                        Respond(context.Response, 404);
                        continue;
                    }

                    // A request on the right path with the wrong state is stray or forged. Refuse it and keep
                    // waiting, so it cannot end this attempt before the real redirect arrives.
                    if (!JiweAuthorization.IsCallbackForState(context.Request.Url.Query, expectedState))
                    {
                        Respond(context.Response, 400);
                        continue;
                    }

                    Respond(context.Response, 200);
                    return _redirectUri + context.Request.Url.Query;
                }
            }
        }
    }

    private void StopListenerQuietly()
    {
        try { _listener.Stop(); } catch (Exception) { /* already stopped */ }
    }

    private static void Respond(HttpListenerResponse response, int status)
    {
        try
        {
            var body = status == 200 ? NeutralPage : string.Empty;
            var bytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = status;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }
        catch (Exception)
        {
            // The browser may have disconnected; the callback is still processed.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener.Stop(); } catch (Exception) { }
        _listener.Close();
    }
}
