using System.Net;
using System.Text;
using System.Text.Json;
using P = Microsoft.Agents.A365.DevTools.Cli.Services.ScopedLabAuthentication;
using static NativeApi;

// These are context-guard tests, NOT cryptographic JWT signature-validation tests.
internal static class AuthSelfTest
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        var now = DateTimeOffset.UtcNow;
        Dictionary<string, object> Access() => new() {
            ["tid"] = P.Tenant, ["oid"] = P.Operator, ["appid"] = P.Client,
            ["exp"] = now.ToUnixTimeSeconds() + 600, ["nbf"] = now.ToUnixTimeSeconds() - 60,
            ["aud"] = "https://graph.microsoft.com", ["iss"] = P.Authority + "/v2.0",
            ["scp"] = "User.Read AgentIdentity.Read.All AgentRegistration.ReadWrite.All AgentIdentity.Create.All"
        };
        Dictionary<string, object> Id() => new() {
            ["tid"] = P.Tenant, ["oid"] = P.Operator, ["aud"] = P.Client,
            ["exp"] = now.ToUnixTimeSeconds() + 600, ["iss"] = P.Authority + "/v2.0",
            ["preferred_username"] = P.Account
        };
        string Token(object claims) => "synthetic-header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claims)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic-signature";
        string Response(object access, object id) => JsonSerializer.Serialize(new {
            token_type = "Bearer", access_token = Token(access), id_token = Token(id)
        });
        void Denied(Action action) {
            bool rejected = false;
            try { action(); } catch (InvalidOperationException) { rejected = true; }
            Need(rejected, "auth_negative_test_failed"); checks++;
        }
        P.Validate(Response(Access(), Id()), now); checks++;
        foreach (string key in new[] { "tid", "oid", "appid", "aud", "iss", "scp" }) {
            var bad = Access(); bad[key] = "incorrect";
            Denied(() => P.Validate(Response(bad, Id()), now));
        }
        var expired = Access(); expired["exp"] = now.ToUnixTimeSeconds() - 1;
        Denied(() => P.Validate(Response(expired, Id()), now));
        var application = Access(); application["roles"] = new[] { "unexpected" };
        Denied(() => P.Validate(Response(application, Id()), now));
        var wrongAccount = Id(); wrongAccount["preferred_username"] = "other@example.invalid";
        Denied(() => P.Validate(Response(Access(), wrongAccount), now));
        var wrongClient = Id(); wrongClient["aud"] = "incorrect";
        Denied(() => P.Validate(Response(Access(), wrongClient), now));
        Denied(() => P.Validate("{}", now));
        bool contextLocked = false;
        try { LabSettings.Configure(Element(new { }), Path.GetTempPath()); }
        catch (NativeStop ex) when (ex.Message == "lab_settings_already_configured") { contextLocked = true; }
        Need(contextLocked, "auth_context_reconfiguration_allowed"); checks++;

        int requests = 0, displays = 0;
        using var http = new HttpClient(new NativeSelfTest.Network(async request => {
            requests++;
            object body;
            if (requests == 1) {
                Need(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == P.DeviceUrl, "auth_device_endpoint");
                var form = await request.Content!.ReadAsStringAsync();
                Need(form.Contains(Uri.EscapeDataString(P.RequestedScopes).Replace("%20", "+")) && form.Contains(P.Client), "auth_existing_grants_scope");
                body = new { verification_uri = "https://microsoft.com/devicelogin", user_code = "ABCDEF123",
                    device_code = "synthetic-private-device-code", expires_in = 600, interval = 1 };
            } else if (requests == 2) {
                Need(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == P.TokenUrl, "auth_token_endpoint");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response(Access(), Id())) };
            } else {
                Need(requests == 3 && request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == P.MeUrl, "auth_me_endpoint");
                Need(request.Headers.Authorization?.Scheme == "Bearer", "auth_me_bearer");
                body = new { id = P.Operator, userPrincipalName = P.Account };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
        }));
        using var auth = await P.AcquireAsync(http, (uri, code) => {
            Need(uri == "https://microsoft.com/devicelogin" && code == "ABCDEF123", "auth_display_projection"); displays++;
        }, CancellationToken.None, () => now, (_, _) => Task.CompletedTask);
        Need(requests == 3 && displays == 1, "auth_request_count"); checks++;
        await auth.GetMgGraphAccessTokenAsync(P.Tenant, ["AgentIdentity.Create.All"], clientAppId: P.Client); checks++;
        bool refused = false;
        try { await auth.GetMgGraphAccessTokenAsync(P.Tenant, ["Mail.Send"], clientAppId: P.Client); }
        catch (InvalidOperationException) { refused = true; }
        Need(refused, "auth_scope_broadening"); checks++;
        Console.WriteLine(JsonSerializer.Serialize(new { status = "offline_auth_context_tests_passed", checks,
            live_http = false, signature_validation_tested = false }));
        return 0;
    }
}
