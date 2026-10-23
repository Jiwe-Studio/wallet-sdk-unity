using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Jiwe.Tests
{
    /// <summary>JiweAuth, Standalone/Editor path (loopback listener) against the mock token endpoint.</summary>
    public class AuthTests : JiweTestBase
    {
        static string BasicHeader(string id, string secret) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret));

        [UnityTest]
        public IEnumerator Login_Succeeds_WithBasicAuth_AndReadsEverythingBack()
        {
            Config("{\"expected_challenge\":\"\"}");
            yield return LoginOk();

            Assert.IsTrue(auth.IsLoggedIn);
            Assert.IsFalse(auth.IsLoggingIn);
            Assert.IsNotEmpty(auth.IdToken);
            StringAssert.StartsWith("access-", auth.AccessToken);
            Assert.AreEqual("player-123", auth.UserInfo.sub);
            Assert.AreEqual("Test Player", auth.UserInfo.name);
            Assert.AreEqual("tester", auth.UserInfo.nickname);
            Assert.AreEqual("wallet-abc", auth.UserInfo.walletId);
            Assert.AreEqual("https://example.test/p.png", auth.UserInfo.picture);
            Assert.That((auth.AccessTokenExpiresAtUtc - DateTime.UtcNow).TotalSeconds, Is.InRange(850, 905));
            Assert.IsFalse(auth.IsAccessTokenExpired);

            var token = Requests("/token", "POST");
            Assert.AreEqual(1, token.Count, "Basic should be accepted first time, no fallback request");
            Assert.AreEqual(BasicHeader("client-1", "secret-1"), Header(token[0], "authorization"));
            StringAssert.Contains("grant_type=authorization_code", token[0].body);
            StringAssert.Contains("code_verifier=", token[0].body);
            StringAssert.DoesNotContain("client_secret", token[0].body, "secret must not ALSO be in the body when Basic is used");
            StringAssert.DoesNotContain("client_id", token[0].body);
            string redirect = Uri.UnescapeDataString(Regex.Match(token[0].body, "redirect_uri=([^&]+)").Groups[1].Value);
            Assert.AreEqual($"http://127.0.0.1:{loopbackPort}/", redirect);
        }

        [UnityTest]
        public IEnumerator LoginUrl_HasPkceStateScopeAndRedirect_ButNoResourceByDefault()
        {
            yield return BeginLogin();
            Assert.AreEqual("code", QueryParam(capturedAuthUrl, "response_type"));
            Assert.AreEqual("client-1", QueryParam(capturedAuthUrl, "client_id"));
            Assert.AreEqual("S256", QueryParam(capturedAuthUrl, "code_challenge_method"));
            Assert.AreEqual("openid profile in-app-purchases rewards", QueryParam(capturedAuthUrl, "scope"));
            Assert.AreEqual($"http://127.0.0.1:{loopbackPort}/", QueryParam(capturedAuthUrl, "redirect_uri"));
            Assert.IsNotEmpty(QueryParam(capturedAuthUrl, "state"));
            Assert.IsNotEmpty(QueryParam(capturedAuthUrl, "code_challenge"));
            Assert.IsNull(QueryParam(capturedAuthUrl, "resource"), "resource must be opt-in so existing games' login is unchanged");
            StringAssert.StartsWith(Base + "/auth?", capturedAuthUrl);
        }

        [UnityTest]
        public IEnumerator LoginUrl_AddsExtraScopesAndResource_WhenSet()
        {
            auth.extraScopes = "rewards:read";
            auth.walletResource = "https://wallet.example/resource";
            yield return BeginLogin();
            Assert.AreEqual("openid profile in-app-purchases rewards rewards:read", QueryParam(capturedAuthUrl, "scope"));
            Assert.AreEqual("https://wallet.example/resource", QueryParam(capturedAuthUrl, "resource"));
        }

        [UnityTest]
        public IEnumerator Pkce_VerifierSentToTokenEndpoint_MatchesTheChallengeInTheLoginUrl()
        {
            yield return BeginLogin();
            Config("{\"expected_challenge\":\"" + QueryParam(capturedAuthUrl, "code_challenge") + "\"}");   // mock re-computes SHA256(verifier)
            BrowserRedirect("code-pkce", QueryParam(capturedAuthUrl, "state"));
            yield return WaitUntil(() => loginDone, 15f, "login");
            Assert.IsTrue(loginOk, loginMessage);
        }

        [UnityTest]
        public IEnumerator Pkce_WrongChallenge_IsRejectedByTheServer()
        {
            yield return BeginLogin();
            Config("{\"expected_challenge\":\"not-the-challenge\"}");
            BrowserRedirect("code-bad", QueryParam(capturedAuthUrl, "state"));
            yield return WaitUntil(() => loginDone, 15f, "login");
            Assert.IsFalse(loginOk);
            StringAssert.Contains("PKCE", loginMessage);
        }

        // ---- client authentication modes --------------------------------------------------
        [UnityTest]
        public IEnumerator Auto_FallsBackToBody_WhenServerWantsBody()
        {
            Config("{\"token_auth\":\"body\"}");
            yield return LoginOk();
            var token = Requests("/token", "POST");
            Assert.AreEqual(2, token.Count, "Basic first, then exactly one retry in the body");
            Assert.IsNotNull(Header(token[0], "authorization"));
            Assert.IsNull(Header(token[1], "authorization"));
            StringAssert.Contains("client_id=client-1", token[1].body);
            StringAssert.Contains("client_secret=secret-1", token[1].body);
        }

        [UnityTest]
        public IEnumerator Auto_Fallback_FailsCleanly_IfTheServerBurnsTheCodeOnAuthFailure()
        {
            // The documented assumption behind Auto: a rejected client authentication does NOT consume the
            // authorization code. If a server did burn it, the fallback can't work — it must fail visibly, not hang.
            Config("{\"token_auth\":\"body\",\"consume_code_on_auth_failure\":true}");
            yield return LoginExpectingFailure("code-burn");
            StringAssert.Contains("invalid_grant", loginMessage);
            Assert.AreEqual(2, Requests("/token", "POST").Count);
        }

        [UnityTest]
        public IEnumerator PinnedBasic_DoesNotFallBack_AndSurfacesInvalidClient()
        {
            auth.clientAuth = JiweAuth.TokenClientAuth.BasicHeader;
            Config("{\"token_auth\":\"body\"}");
            yield return LoginExpectingFailure();
            StringAssert.Contains("invalid_client", loginMessage);
            Assert.AreEqual(1, Requests("/token", "POST").Count);
        }

        [UnityTest]
        public IEnumerator PinnedBody_AgainstABasicServer_Fails()
        {
            auth.clientAuth = JiweAuth.TokenClientAuth.RequestBody;
            Config("{\"token_auth\":\"basic\"}");
            yield return LoginExpectingFailure();
            StringAssert.Contains("invalid_client", loginMessage);
            Assert.AreEqual(1, Requests("/token", "POST").Count);
        }

        [UnityTest]
        public IEnumerator PinnedBody_AgainstABodyServer_Succeeds()
        {
            auth.clientAuth = JiweAuth.TokenClientAuth.RequestBody;
            Config("{\"token_auth\":\"body\"}");
            yield return LoginOk();
            Assert.AreEqual(1, Requests("/token", "POST").Count);
        }

        [UnityTest]
        public IEnumerator EmptySecret_SendsClientIdOnly_AsAPublicClient()
        {
            auth.apiSecret = "";
            Config("{\"token_auth\":\"public\"}");
            yield return LoginOk();
            var token = Requests("/token", "POST");
            Assert.AreEqual(1, token.Count);
            Assert.IsNull(Header(token[0], "authorization"));
            StringAssert.Contains("client_id=client-1", token[0].body);
            StringAssert.DoesNotContain("client_secret", token[0].body);
        }

        [UnityTest]
        public IEnumerator AServerThatAcceptsEitherMechanism_NeedsOnlyOneRequest()
        {
            Config("{\"token_auth\":\"either\"}");
            yield return LoginOk();
            Assert.AreEqual(1, Requests("/token", "POST").Count);
        }

        // ---- redirect validation -------------------------------------------------------
        [UnityTest]
        public IEnumerator StateMismatch_IsRejected_BeforeAnyTokenRequest()
        {
            yield return LoginExpectingFailure(state: "forged-state");
            StringAssert.Contains("State mismatch", loginMessage);
            Assert.AreEqual(0, Requests("/token", "POST").Count);
        }

        [UnityTest]
        public IEnumerator ErrorRedirect_ReportsTheDenial()
        {
            yield return LoginExpectingFailure(error: "access_denied");
            StringAssert.Contains("access_denied", loginMessage);
            Assert.AreEqual(0, Requests("/token", "POST").Count);
        }

        [UnityTest]
        public IEnumerator ReplayedAuthorizationCode_IsRejected()
        {
            yield return LoginOk();
            string used = Regex.Match(Requests("/token", "POST")[0].body, "code=([^&]+)").Groups[1].Value;
            yield return BeginLogin();
            BrowserRedirect(used, QueryParam(capturedAuthUrl, "state"));
            yield return WaitUntil(() => loginDone, 15f, "login");
            Assert.IsFalse(loginOk);
            StringAssert.Contains("invalid_grant", loginMessage);
        }

        // ---- lifecycle ----------------------------------------------------------------
        [UnityTest]
        public IEnumerator Cancel_WhileWaitingForTheBrowser_FailsAsCancelled()
        {
            yield return BeginLogin();
            Assert.IsTrue(auth.IsLoggingIn);
            auth.Cancel();
            yield return WaitUntil(() => loginDone, 5f, "cancellation to surface");
            Assert.IsFalse(loginOk);
            StringAssert.Contains("cancelled", loginMessage.ToLowerInvariant());
            Assert.IsFalse(auth.IsLoggingIn);
        }

        [UnityTest]
        public IEnumerator SecondLogin_WhileOneIsRunning_IsIgnored()
        {
            yield return BeginLogin();
            string first = capturedAuthUrl;
            capturedAuthUrl = null;
            LogAssert.Expect(LogType.Warning, new Regex("already in progress"));
            auth.Login();
            yield return null;
            Assert.IsNull(capturedAuthUrl, "the second Login() must not open a second login");
            Assert.IsTrue(auth.IsLoggingIn);
            capturedAuthUrl = first;
        }

        [UnityTest]
        public IEnumerator LoginCanBeRepeated_AfterSuccess()
        {
            yield return LoginOk();
            yield return LoginOk();
            Assert.AreEqual(2, Requests("/token", "POST").Count);
        }

        [UnityTest]
        public IEnumerator SlowTokenEndpoint_TimesOut_InsteadOfHanging()
        {
            auth.networkTimeoutSeconds = 1f;
            Config("{\"token_delay\":4}");
            yield return LoginExpectingFailure();
            StringAssert.Contains("timed out", loginMessage);
        }

        [UnityTest]
        public IEnumerator NoBrowserRedirect_TimesOut_InsteadOfHanging()
        {
            auth.networkTimeoutSeconds = 1f;
            yield return BeginLogin();               // nobody ever visits the redirect
            yield return WaitUntil(() => loginDone, 6f, "the redirect wait to time out");
            Assert.IsFalse(loginOk);
            StringAssert.Contains("Timed out", loginMessage);
        }

        // ---- profile ----------------------------------------------------------------
        [UnityTest]
        public IEnumerator UserInfoEndpointRejecting_DoesNotBreakLogin_ProfileComesFromTheIdToken()
        {
            Config("{\"me_status\":401}");
            yield return LoginOk();
            Assert.AreEqual("player-123", auth.UserInfo.sub);
            Assert.AreEqual("Test Player", auth.UserInfo.name);
        }

        [UnityTest]
        public IEnumerator IdTokenClaims_DecodeForEveryBase64PaddingCase()
        {
            foreach (string name in new[] { "A", "AB", "ABC", "ABCD", "Zoë Ñandú", "名前" })
            {
                Config("{\"claims\":{\"name\":\"" + name + "\"}}");
                yield return LoginOk();
                Assert.AreEqual(name, auth.UserInfo.name, "name claim must survive base64url decoding");
            }
        }

        [UnityTest]
        public IEnumerator LongNonJsonErrorBody_IsCapped_NotDumpedIntoTheUi()
        {
            Faults("[{\"match\":\"/token\",\"status\":400,\"body\":\"" + new string('x', 1500) + "\"}]");
            yield return LoginExpectingFailure();
            Assert.LessOrEqual(loginMessage.Length, 301);
            StringAssert.EndsWith("…", loginMessage);
        }

        [UnityTest]
        public IEnumerator TokenEndpointOverride_IsHonoured()
        {
            yield return LoginOk();
            Assert.IsTrue(Requests("/token").Count > 0 && Requests("/me").Count > 0, "token and userinfo overrides were used");
        }

        [UnityTest]
        public IEnumerator DefaultEndpoints_PointAtProduction_WhenNothingIsOverridden()
        {
            auth.authorizationEndpoint = ""; auth.tokenEndpoint = ""; auth.userInfoEndpoint = "";
            yield return BeginLogin();
            StringAssert.StartsWith("https://id.jiwe.io/auth?", capturedAuthUrl);
        }
    }
}
