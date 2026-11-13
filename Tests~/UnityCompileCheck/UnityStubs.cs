// Minimal stand-ins for the UnityEngine members the SDK and its sample call. Dev-only: used to
// compile-check Runtime/ and Samples/ without the Unity Editor. Not a substitute for an Editor build.
using System;

namespace UnityEngine
{
    public class Object { }
    public class MonoBehaviour : Object { }  // Unity dispatches OnDestroy/OnGUI/Start by message: user classes declare them privately.
    public class AsyncOperation { }

    public static class Application
    {
        public static void OpenURL(string url) { }
    }

    public class SerializeFieldAttribute : Attribute { }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string tip) { } }

    public struct Rect
    {
        public Rect(float x, float y, float width, float height) { }
    }

    public static class GUI
    {
        public static bool enabled { get; set; }
        public static void Label(Rect position, string text) { }
        public static bool Button(Rect position, string text) { return false; }
    }

    namespace Networking
    {
        public class UploadHandler { }
        public class UploadHandlerRaw : UploadHandler { public UploadHandlerRaw(byte[] data) { } }
        public class DownloadHandler { public string text { get; } }
        public class DownloadHandlerBuffer : DownloadHandler { }

        public class UnityWebRequestAsyncOperation
        {
            public bool isDone { get; }
        }

        public class UnityWebRequest : IDisposable
        {
            public const string kHttpVerbPOST = "POST";
            public enum Result { Success, InProgress, ConnectionError, ProtocolError, DataProcessingError }

            public UnityWebRequest(string url, string method) { }
            public static UnityWebRequest Get(string url) => new UnityWebRequest(url, "GET");

            public UploadHandler uploadHandler { get; set; }
            public DownloadHandler downloadHandler { get; set; }
            public long responseCode { get; }
            public int timeout { get; set; }
            public int redirectLimit { get; set; }
            public Result result { get; }

            public void SetRequestHeader(string name, string value) { }
            public string GetResponseHeader(string name) => null;
            public UnityWebRequestAsyncOperation SendWebRequest() => new UnityWebRequestAsyncOperation();
            public void Abort() { }
            public void Dispose() { }
        }
    }
}
