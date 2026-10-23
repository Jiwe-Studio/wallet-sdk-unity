#if UNITY_ANDROID || UNITY_IOS
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Jiwe.Tests
{
    /// <summary>
    /// The Android/iOS login path: the system browser redirects to a custom URI scheme and the OS hands the link to
    /// Application.deepLinkActivated. Compiled only when the active build target is Android or iOS
    /// (BUILD_TARGET=Android Tests~/run-tests.sh). The editor can't receive a real OS deep link, so these tests start the
    /// flow and deliver the link the way the OS would, through the SDK's own private methods.
    /// </summary>
    public class DeepLinkTests : JiweTestBase
    {
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

        void StartLikeLoginDoes()
        {
            // JiweAuth.Login() does this, then picks LoginViaDeepLink on a phone.
            Set("_codeVerifier", "verifier-0123456789012345678901234567890123456789");
            Set("_state", "state-abc");
            Set("_loginInProgress", true);
            Set("_cancelled", false);
            typeof(JiweAuth).GetMethod("LoginViaDeepLink", NP).Invoke(auth, new object[] { "challenge" });
        }
        void Set(string field, object v) => typeof(JiweAuth).GetField(field, NP).SetValue(auth, v);
        void Deliver(string url) => typeof(JiweAuth).GetMethod("HandleDeepLink", NP).Invoke(auth, new object[] { url });

        [UnityTest]
        public IEnumerator DeepLink_Login_Succeeds_AndSendsTheSchemeAsTheRedirectUri()
        {
            StartLikeLoginDoes();
            Assert.AreEqual("jiwewallet://oauth-callback", QueryParam(capturedAuthUrl, "redirect_uri"));
            Deliver("jiwewallet://oauth-callback?code=dl-1&state=state-abc");
            yield return WaitUntil(() => loginDone, 15f, "login");
            Assert.IsTrue(loginOk, loginMessage);
            Assert.AreEqual("player-123", auth.UserInfo.sub);
            var token = Requests("/token", "POST").Single();
            StringAssert.Contains("redirect_uri=jiwewallet%3a%2f%2foauth-callback", token.body.ToLowerInvariant());   // percent-encoding hex case is not significant
        }

        [UnityTest]
        public IEnumerator DeepLink_UsesYourOwnScheme_WhenSet()
        {
            auth.mobileRedirectScheme = "mygame";
            StartLikeLoginDoes();
            Assert.AreEqual("mygame://oauth-callback", QueryParam(capturedAuthUrl, "redirect_uri"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator DeepLink_ArrivingAfterCancel_IsIgnored_NotSilentlyLoggedIn()
        {
            StartLikeLoginDoes();
            auth.Cancel();                                         // the player tapped Skip and moved on…
            Deliver("jiwewallet://oauth-callback?code=late&state=state-abc");   // …then the OS brings the slow redirect back
            yield return new WaitForSeconds(0.5f);
            Assert.IsFalse(auth.IsLoggedIn, "a late deep link must not complete a login the player abandoned");
            Assert.AreEqual(0, Requests("/token", "POST").Count);
        }

        [UnityTest]
        public IEnumerator DeepLink_StateMismatch_IsRejected()
        {
            StartLikeLoginDoes();
            Deliver("jiwewallet://oauth-callback?code=x&state=forged");
            yield return WaitUntil(() => loginDone, 5f, "failure");
            Assert.IsFalse(loginOk); StringAssert.Contains("State mismatch", loginMessage);
            Assert.AreEqual(0, Requests("/token", "POST").Count);
        }

        [UnityTest]
        public IEnumerator DeepLink_ErrorLink_ReportsTheDenial()
        {
            StartLikeLoginDoes();
            Deliver("jiwewallet://oauth-callback?error=access_denied&state=state-abc");
            yield return WaitUntil(() => loginDone, 5f, "failure");
            Assert.IsFalse(loginOk); StringAssert.Contains("access_denied", loginMessage);
        }

        [UnityTest]
        public IEnumerator DeepLink_ADuplicateDelivery_ExchangesTheCodeOnce()
        {
            StartLikeLoginDoes();
            Deliver("jiwewallet://oauth-callback?code=dup&state=state-abc");
            Deliver("jiwewallet://oauth-callback?code=dup&state=state-abc");     // some OSes deliver a link twice
            yield return WaitUntil(() => loginDone, 15f, "login");
            yield return new WaitForSeconds(0.5f);
            Assert.IsTrue(loginOk, loginMessage);
            Assert.AreEqual(1, Requests("/token", "POST").Count, "the second delivery must not trigger a second exchange");
        }
    }
}
#endif
