using System;
using System.Linq;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    /// <summary>Sign-in through the CLI (`claude auth …`, measured against 2.1.319 with a temp CLAUDE_CONFIG_DIR).
    /// Account data below is fictional.</summary>
    public class ClaudeAuthTests
    {
        private const string SignedIn = @"{
  ""loggedIn"": true,
  ""authMethod"": ""claude.ai"",
  ""apiProvider"": ""firstParty"",
  ""analyticsDisabled"": false,
  ""projectsDirectory"": ""C:\\Users\\dev\\.claude\\projects"",
  ""configDirectory"": ""C:\\Users\\dev\\.claude"",
  ""loginExpiresAt"": ""2026-12-20T04:48:46.144Z"",
  ""email"": ""dev@sampleshop.example"",
  ""orgId"": ""00000000-0000-0000-0000-000000000000"",
  ""orgName"": ""SampleShop Ltd"",
  ""subscriptionType"": ""team"",
  ""storedClaudeAiLogin"": true,
  ""planTier"": ""other""
}";

        private const string SignedOut = @"{
  ""loggedIn"": false,
  ""authMethod"": ""none"",
  ""apiProvider"": ""firstParty"",
  ""analyticsDisabled"": false,
  ""projectsDirectory"": ""C:\\Temp\\cfg\\projects"",
  ""configDirectory"": ""C:\\Temp\\cfg"",
  ""loginExpiresAt"": null
}";

        [Fact]
        public void ParseStatus_SignedIn_ReadsAccountAndExpiry()
        {
            var s = ClaudeAuth.ParseStatus(SignedIn)!;
            Assert.True(s.LoggedIn);
            Assert.Equal("claude.ai", s.AuthMethod);
            Assert.Equal("dev@sampleshop.example", s.Email);
            Assert.Equal("SampleShop Ltd", s.OrgName);
            Assert.Equal("team", s.SubscriptionType);
            Assert.Equal(new DateTime(2026, 12, 20, 4, 48, 46, 144, DateTimeKind.Utc), s.LoginExpiresAtUtc);
        }

        [Fact]
        public void ParseStatus_SignedOut_AndGarbage()
        {
            var s = ClaudeAuth.ParseStatus(SignedOut)!;
            Assert.False(s.LoggedIn);
            Assert.Equal("none", s.AuthMethod);
            Assert.Null(s.LoginExpiresAtUtc);
            Assert.Null(ClaudeAuth.ParseStatus("Not logged in. Run claude auth login to authenticate."));
            Assert.Null(ClaudeAuth.ParseStatus(""));
        }

        [Fact]
        public void BuildState_OnlyACliLoginCanBeSignedOutHere()
        {
            var signedIn = Bridge.AuthController.BuildState(ClaudeAuth.ParseStatus(SignedIn)!, null);
            Assert.Equal("oauth", signedIn.Value<string>("mode"));
            Assert.True(signedIn.Value<bool>("canLogout"));

            var cloud = Bridge.AuthController.BuildState(new AuthStatus { LoggedIn = false, ApiProvider = "bedrock" }, null);
            Assert.True(cloud.Value<bool>("loggedIn")); // a cloud provider needs no login of ours
            Assert.False(cloud.Value<bool>("canLogout"));

            var key = Bridge.AuthController.BuildState(new AuthStatus { LoggedIn = true, AuthMethod = "api_key" }, null);
            Assert.Equal("apiKey", key.Value<string>("mode"));
            Assert.False(key.Value<bool>("canLogout"));
        }

        [Fact]
        public void LoginOutput_ManualUrlIsFound_CapturedBrowserUrlIsUnquoted()
        {
            const string stdout = "Opening browser to sign in…\r\n"
                + "If the browser didn't open, visit: https://claude.com/cai/oauth/authorize?code=true&client_id=x&response_type=code&redirect_uri=https%3A%2F%2Fplatform.claude.com%2Foauth%2Fcode%2Fcallback&state=abc\r\n"
                + "Paste code here if prompted > ";
            Assert.StartsWith("https://claude.com/cai/oauth/authorize?code=true", ClaudeAuth.ParseManualUrl(stdout));
            Assert.EndsWith("state=abc", ClaudeAuth.ParseManualUrl(stdout));

            const string captured = "\"https://claude.com/cai/oauth/authorize?code=true&client_id=x&redirect_uri=http%3A%2F%2Flocalhost%3A58392%2Fcallback&state=abc\" \r\n";
            Assert.Equal("https://claude.com/cai/oauth/authorize?code=true&client_id=x&redirect_uri=http%3A%2F%2Flocalhost%3A58392%2Fcallback&state=abc",
                ClaudeAuth.ParseCapturedUrl(captured));
        }

        [Fact]
        public void OnlyHttpsPagesOfClaudeOrAnthropicAreOpened()
        {
            Assert.True(ClaudeAuth.IsSignInUrl("https://claude.com/cai/oauth/authorize?x=1"));
            Assert.True(ClaudeAuth.IsSignInUrl("https://platform.claude.com/oauth/code/callback"));
            Assert.True(ClaudeAuth.IsSignInUrl("https://console.anthropic.com/oauth/authorize"));
            Assert.False(ClaudeAuth.IsSignInUrl("http://claude.com/cai/oauth/authorize"));
            Assert.False(ClaudeAuth.IsSignInUrl("https://claude.com.evil.example/x"));
            Assert.False(ClaudeAuth.IsSignInUrl("file:///C:/Windows/System32/calc.exe"));
            Assert.False(ClaudeAuth.IsSignInUrl("cmd /c calc"));
            Assert.Null(ClaudeAuth.ParseCapturedUrl("\"https://example.com/phish\""));
        }

        [Fact]
        public void NotSignedInTurn_IsFlaggedOnTheResult()
        {
            // what 2.1.319 prints for a turn without a login (synthetic assistant message + failed result)
            var parser = new NdjsonParser();
            parser.ParseLine("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s1\",\"model\":\"m\",\"cwd\":\"C:/src\",\"tools\":[]}");
            parser.ParseLine("{\"type\":\"assistant\",\"error\":\"authentication_failed\",\"message\":{\"id\":\"x\",\"model\":\"<synthetic>\",\"role\":\"assistant\",\"type\":\"message\",\"content\":[{\"type\":\"text\",\"text\":\"Not logged in · Please run /login\"}]},\"session_id\":\"s1\"}");
            var result = parser.ParseLine("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"Not logged in · Please run /login\",\"num_turns\":1,\"session_id\":\"s1\"}")
                .OfType<TurnResultEvent>().Single();
            Assert.True(result.AuthFailed);

            // the next turn is clean again
            var ok = parser.ParseLine("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Done\",\"num_turns\":1,\"session_id\":\"s1\"}")
                .OfType<TurnResultEvent>().Single();
            Assert.False(ok.AuthFailed);
        }

        [Fact]
        public void ExpiredLoginText_WithoutTheErrorTag_IsStillRecognized()
        {
            var parser = new NdjsonParser();
            var r = parser.ParseLine("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"OAuth token has expired · Please run /login\",\"num_turns\":1,\"session_id\":\"s1\"}")
                .OfType<TurnResultEvent>().Single();
            Assert.True(r.AuthFailed);
            Assert.False(ClaudeAuth.IsAuthFailureText("Build failed: 3 errors"));
        }
    }
}
