using Jiwe;
using UnityEngine;

namespace JiweSamples
{
    /// <summary>Tiny IMGUI helpers so every sample runs with zero scene or prefab setup (also on WebGL and phones).</summary>
    internal static class SampleGui
    {
        static float _scale = 1f;

        public static void Begin(string title)
        {
            _scale = Mathf.Max(1f, Screen.height / 640f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1f));
            GUI.skin.label.richText = true;
            GUILayout.BeginArea(new Rect(12, 12, Screen.width / _scale - 24, Screen.height / _scale - 24));
            GUILayout.Label("<b>" + title + "</b>");
        }

        public static void End()
        {
            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity;
        }

        /// <summary>Login state and the two buttons every Jiwe game needs: Log in, and a Cancel that stays live the whole time.</summary>
        public static void LoginBar(JiweAuth auth)
        {
            if (auth == null) return;
            GUILayout.BeginHorizontal();
            if (auth.IsLoggedIn)
                GUILayout.Label("Signed in as <b>" + (auth.UserInfo?.nickname ?? auth.UserInfo?.sub ?? "?") + "</b>");
            else if (auth.IsLoggingIn)
            {
                GUILayout.Label("Waiting for the browser…");
                if (GUILayout.Button("Cancel", GUILayout.Width(120))) auth.Cancel();   // never disable your own escape route while waiting
            }
            else
            {
                GUILayout.Label("Playing as a guest.");
                if (GUILayout.Button("Log in with Jiwe", GUILayout.Width(220))) auth.Login();
            }
            GUILayout.EndHorizontal();
        }
    }
}
