using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

/// <summary>Result of one HTTP exchange. Status 0 means no response was received.</summary>
internal sealed class JiweHttpResponse
{
    public long Status;
    public string Body;
    public string RetryAfter;
}

/// <summary>
/// UnityWebRequest wrapper that exposes Task-based calls. Must be invoked from the Unity main thread,
/// which the SDK's async entry points already guarantee.
/// </summary>
internal static class JiweHttpClient
{
    internal const int TimeoutSeconds = 20;

    internal static Task<JiweHttpResponse> GetAsync(string url, string bearerToken, CancellationToken ct)
    {
        var request = UnityWebRequest.Get(url);
        request.SetRequestHeader("Accept", "application/json");
        if (!string.IsNullOrEmpty(bearerToken))
        {
            request.SetRequestHeader("Authorization", "Bearer " + bearerToken);
        }
        return SendAsync(request, ct);
    }

    internal static Task<JiweHttpResponse> PostJsonAsync(
        string url, string bearerToken, string json, string idempotencyKey, CancellationToken ct)
    {
        var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
            downloadHandler = new DownloadHandlerBuffer(),
        };
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Accept", "application/json");
        request.SetRequestHeader("Authorization", "Bearer " + bearerToken);
        if (idempotencyKey != null)
            request.SetRequestHeader("Idempotency-Key", idempotencyKey);
        return SendAsync(request, ct);
    }

    internal static Task<JiweHttpResponse> PostFormAsync(string url, string formBody, CancellationToken ct)
    {
        var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(formBody)),
            downloadHandler = new DownloadHandlerBuffer(),
        };
        request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
        request.SetRequestHeader("Accept", "application/json");
        return SendAsync(request, ct);
    }

    private static async Task<JiweHttpResponse> SendAsync(UnityWebRequest request, CancellationToken ct)
    {
        using (request)
        {
            request.timeout = TimeoutSeconds;
            // Never follow redirects: the token request body and the bearer header must stay on the fixed host.
            request.redirectLimit = 0;
            ct.ThrowIfCancellationRequested();

            var operation = request.SendWebRequest();

            // Poll on the Unity main thread instead of aborting from a cancellation callback, because the
            // callback can run on any thread and UnityWebRequest must be aborted on the main thread.
            while (!operation.isDone)
            {
                if (ct.IsCancellationRequested)
                {
                    request.Abort();
                    throw new OperationCanceledException(ct);
                }
                await Task.Yield();
            }

            ct.ThrowIfCancellationRequested();

            var response = new JiweHttpResponse
            {
                Status = request.responseCode,
                Body = request.downloadHandler != null ? request.downloadHandler.text : null,
                RetryAfter = request.GetResponseHeader("Retry-After"),
            };

            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                response.Status = 0;
            }
            return response;
        }
    }
}
