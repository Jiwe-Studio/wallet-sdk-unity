using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Jiwe.Tests
{
    public class PublicSdkTests
    {
        [SetUp]
        public void Initialize()
        {
            JiweSdk.InitializeAsync(new JiweConfig { ClientId = "fixture-public-client" }).GetAwaiter().GetResult();
        }

        [TearDown]
        public void Shutdown() => JiweSdk.Shutdown();

        [TestCase("0")]
        [TestCase("-1")]
        [TestCase("0.001")]
        public void RejectsInvalidXpAmount(string amount)
        {
            var xp = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(JiweErrorKind.InvalidRequest,
                ThrowsJiwe(() => JiweSdk.Rewards.AwardXpAsync(xp, "fixture", Guid.NewGuid())).Kind);
        }

        [Test]
        public void RejectsMissingAwardIdAndDescription()
        {
            Assert.AreEqual(JiweErrorKind.InvalidRequest,
                ThrowsJiwe(() => JiweSdk.Rewards.AwardXpAsync(1, "fixture", Guid.Empty)).Kind);
            Assert.AreEqual(JiweErrorKind.InvalidRequest,
                ThrowsJiwe(() => JiweSdk.Rewards.AwardXpAsync(1, null, Guid.NewGuid())).Kind);
        }

        [Test]
        public void RejectsCancelledSignInBeforeOpeningBrowser()
        {
            Assert.AreEqual(JiweErrorKind.LoginCancelled,
                ThrowsJiwe(() => JiweSdk.Auth.SignInAsync(new CancellationToken(true))).Kind);
            Assert.AreEqual(JiweSessionStatus.SignedOut, JiweSdk.Auth.Status);
        }

        [Test]
        public void SigningInNotificationCanSignOutBeforeOpeningBrowser()
        {
            void SignOutOnStart()
            {
                if (JiweSdk.Auth.Status == JiweSessionStatus.SigningIn)
                    _ = JiweSdk.Auth.SignOutAsync();
            }

            JiweSdk.Auth.StatusChanged += SignOutOnStart;
            try
            {
                var attempt = JiweSdk.Auth.SignInAsync();
                Assert.IsTrue(attempt.IsCompleted, "A superseded attempt must exit before waiting for a browser.");
                Assert.AreEqual(JiweErrorKind.LoginSuperseded, ThrowsJiwe(() => attempt).Kind);
                Assert.IsNull(JiweSdk.Auth.PlayerId);
                Assert.AreEqual(JiweErrorKind.NotSignedIn,
                    ThrowsJiwe(() => JiweSdk.Rewards.GetEarnedXpAsync()).Kind);
            }
            finally
            {
                JiweSdk.Auth.StatusChanged -= SignOutOnStart;
            }
        }

        [Test]
        public void LapRoutesValidateInputsAndRequireSignIn()
        {
            Assert.AreEqual(JiweErrorKind.InvalidRequest,
                ThrowsJiwe(() => JiweSdk.Laps.SubmitAsync(0, Guid.NewGuid())).Kind);
            Assert.AreEqual(JiweErrorKind.InvalidRequest,
                ThrowsJiwe(() => JiweSdk.Laps.SubmitAsync(1000, Guid.Empty)).Kind);
            Assert.AreEqual(JiweErrorKind.InvalidRequest,
                ThrowsJiwe(() => JiweSdk.Laps.QueryAsync(101)).Kind);
            Assert.AreEqual(JiweErrorKind.NotSignedIn,
                ThrowsJiwe(() => JiweSdk.Laps.SubmitAsync(1000, Guid.NewGuid())).Kind);
            Assert.AreEqual(JiweErrorKind.NotSignedIn,
                ThrowsJiwe(() => JiweSdk.Laps.QueryAsync()).Kind);
        }

        [Test]
        public void LapResultsUseIntegerMilliseconds()
        {
            Assert.AreEqual(1234, JiweLapApi.ParseBest(JObject.Parse("{\"bestMilliseconds\":1234}")));
            var rows = JiweLapApi.ParseEntries(JObject.Parse(
                "{\"entries\":[{\"rank\":1,\"milliseconds\":1234,\"isCaller\":true}]}"), 1);
            Assert.AreEqual(1, rows[0].Rank);
            Assert.AreEqual(1234, rows[0].Milliseconds);
            Assert.IsTrue(rows[0].IsCaller);
        }

        [TestCase("0")]
        [TestCase("1.5")]
        [TestCase("2147483648")]
        [TestCase("\"1234\"")]
        public void RejectsInvalidLapResponseMilliseconds(string value)
        {
            var response = JObject.Parse("{\"bestMilliseconds\":" + value + "}");
            Assert.AreEqual(JiweErrorKind.UnexpectedResponse,
                Assert.Throws<JiweException>(() => JiweLapApi.ParseBest(response)).Kind);
        }
        private static JiweException ThrowsJiwe(Func<Task> action)
        {
            var task = action();
            Assert.IsTrue(task.IsCompleted, "Validation must finish before any asynchronous I/O.");
            return Assert.Throws<JiweException>(() => task.GetAwaiter().GetResult());
        }
    }
}
