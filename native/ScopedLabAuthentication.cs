// Lab-only delegated device authentication. Configuration is fixed once per process.
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Fixed device protocol, no broker/cache/fallback. JWT reads are lab context
/// guards, NOT signature validation; success also requires Graph /me acceptance.
/// Token values never enter logs, files, command lines or public error messages.
/// A future guarded entrypoint must obtain exact live approval before calling.
/// </summary>
internal sealed class ScopedLabAuthentication : IAuthenticationService, IMicrosoftGraphTokenProvider, IDisposable
{
    internal static string Tenant=>global::LabSettings.Tenant;
    internal static string Client=>global::LabSettings.Client;
    internal static string Operator=>global::LabSettings.Operator;
    internal static string Account=>global::LabSettings.Account;
    internal static string Authority=>"https://login.microsoftonline.com/"+Tenant;
    internal static string DeviceUrl=>Authority+"/oauth2/v2.0/devicecode";
    internal static string TokenUrl=>Authority+"/oauth2/v2.0/token";
    internal const string MeUrl="https://graph.microsoft.com/v1.0/me?$select=id,userPrincipalName";
    internal const string RequestedScopes="https://graph.microsoft.com/.default openid profile";
    private string? _token;
    private readonly long _expiry;
    private readonly HashSet<string> _scopes;
    private readonly Func<DateTimeOffset> _clock;
    private ScopedLabAuthentication(string token,long expiry,HashSet<string> scopes,Func<DateTimeOffset> clock)
    { _token=token; _expiry=expiry; _scopes=scopes; _clock=clock; }
    private enum AuthCheck { Unspecified,TokenShape,TokenType,AccessTenant,AccessOperator,AccessExpiry,IdTenant,IdOperator,IdExpiry,NotBefore,Delegated,Client,Audience,AccessIssuer,IdClient,IdIssuer,Account,UserReadScope,IdentityReadScope,RegistryWriteScope,MeIdentity }
    private enum AuthStage { DeviceRequest,DeviceResponse,TokenPoll,TokenResponse,Context,GraphMe,Complete }
    private enum AuthReason { CheckFailed,Transport,Timeout,Cancelled,ConsentRequired,Declined,Expired,InvalidGrant,OAuthStopped }
    private static InvalidOperationException Stop(AuthCheck check=AuthCheck.Unspecified)
    {var ex=new InvalidOperationException("scoped_lab_auth_stopped");ex.Data["check"]=check;return ex;}
    private static void Require(bool value) {if(!value) throw Stop();}
    private static void Check(AuthCheck check,Func<bool> predicate)
    {try {if(!predicate())throw Stop(check);}catch {throw Stop(check);}}
    internal static Dictionary<string,object?> Diagnostic(Exception ex)
    {
        var result=new Dictionary<string,object?>();
        if(ex.Message!="scoped_lab_auth_stopped")return result;
        if(ex.Data["check"] is AuthCheck check)result["check"]=check.ToString();
        if(ex.Data["stage"] is AuthStage stage)result["stage"]=stage.ToString();
        if(ex.Data["reason"] is AuthReason reason)result["reason"]=reason.ToString();
        if(ex.Data["http_status"] is int status && status is >=100 and <=599)result["http_status"]=status;
        return result;
    }

    internal static HttpClient CreateHttpClient()=>new(new SocketsHttpHandler
    {
        AllowAutoRedirect=false, UseCookies=false, UseProxy=false,
        Credentials=null, ConnectTimeout=TimeSpan.FromSeconds(5)
    }) { Timeout=TimeSpan.FromSeconds(15), MaxResponseContentBufferSize=65536 };

    private static JsonDocument ObjectJson(string text)
    {
        Require(text.Length<=65536);
        var doc=JsonDocument.Parse(text);
        try
        {
            Require(doc.RootElement.ValueKind==JsonValueKind.Object);
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in doc.RootElement.EnumerateObject()) Require(seen.Add(property.Name));
            return doc;
        }
        catch {doc.Dispose(); throw;}
    }
    private static string Text(JsonElement value,string key)=>value.GetProperty(key).GetString()??throw Stop();
    private static JsonDocument Claims(string token)
    {
        Require(token.Length<=32768);
        var parts=token.Split('.'); Require(parts.Length==3);
        string data=parts[1].Replace('-','+').Replace('_','/');
        data=data.PadRight((data.Length+3)/4*4,'=');
        return ObjectJson(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
    }
    private static (string Token,long Expiry,HashSet<string> Scopes) Parse(string result,DateTimeOffset now)
    {
        using var doc=ObjectJson(result); var body=doc.RootElement;
        Check(AuthCheck.TokenType,()=>Text(body,"token_type")=="Bearer");
        var token=Text(body,"access_token");
        using var access=Claims(token); using var id=Claims(Text(body,"id_token"));
        var a=access.RootElement; var i=id.RootElement; long seconds=now.ToUnixTimeSeconds();
        Check(AuthCheck.AccessTenant,()=>Text(a,"tid")==Tenant);
        Check(AuthCheck.AccessOperator,()=>Text(a,"oid")==Operator);
        Check(AuthCheck.AccessExpiry,()=>a.GetProperty("exp").GetInt64()>seconds);
        Check(AuthCheck.IdTenant,()=>Text(i,"tid")==Tenant);
        Check(AuthCheck.IdOperator,()=>Text(i,"oid")==Operator);
        Check(AuthCheck.IdExpiry,()=>i.GetProperty("exp").GetInt64()>seconds);
        Check(AuthCheck.NotBefore,()=>a.GetProperty("nbf").GetInt64()<=seconds);
        Check(AuthCheck.Delegated,()=>!a.TryGetProperty("roles",out _));
        bool hasClient=false;
        foreach(var name in new[]{"appid","azp"})
            if(a.TryGetProperty(name,out var value)) {hasClient=true;Check(AuthCheck.Client,()=>value.GetString()==Client);}
        Check(AuthCheck.Client,()=>hasClient);
        Check(AuthCheck.Audience,()=>new[]{"00000003-0000-0000-c000-000000000000","https://graph.microsoft.com","https://graph.microsoft.com/"}.Contains(Text(a,"aud")));
        Check(AuthCheck.AccessIssuer,()=>new[]{Authority+"/v2.0","https://sts.windows.net/"+Tenant+"/"}.Contains(Text(a,"iss")));
        Check(AuthCheck.IdClient,()=>Text(i,"aud")==Client);
        Check(AuthCheck.IdIssuer,()=>Text(i,"iss")==Authority+"/v2.0");
        Check(AuthCheck.Account,()=>string.Equals(Text(i,"preferred_username"),Account,StringComparison.OrdinalIgnoreCase));
        var scopes=Text(a,"scp").Split(' ',StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        Check(AuthCheck.UserReadScope,()=>scopes.Contains("User.Read"));
        Check(AuthCheck.IdentityReadScope,()=>scopes.Contains("AgentIdentity.Read.All"));
        Check(AuthCheck.RegistryWriteScope,()=>scopes.Contains("AgentRegistration.ReadWrite.All"));
        return (token,Math.Min(a.GetProperty("exp").GetInt64(),i.GetProperty("exp").GetInt64()),scopes);
    }
    internal static void Validate(string result,DateTimeOffset now)
    {try {Parse(result,now);} catch(InvalidOperationException ex) when(ex.Message=="scoped_lab_auth_stopped") {throw;} catch {throw Stop(AuthCheck.TokenShape);}}

    internal static async Task<ScopedLabAuthentication> AcquireAsync(HttpClient http,Action<string,string> showCode,
        CancellationToken ct,Func<DateTimeOffset>? clock=null,Func<TimeSpan,CancellationToken,Task>? delay=null)
    {
        clock??=()=>DateTimeOffset.UtcNow; delay??=Task.Delay;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(600));
        var elapsed=Stopwatch.StartNew();
        http.MaxResponseContentBufferSize=65536;
        var stage=AuthStage.DeviceRequest; int? httpStatus=null; var reason=AuthReason.CheckFailed;
        try
        {
            using var deviceRequest=new HttpRequestMessage(HttpMethod.Post,DeviceUrl)
            { Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"client_id",Client},{"scope",RequestedScopes}}) };
            using var device=await http.SendAsync(deviceRequest,deadline.Token);
            stage=AuthStage.DeviceResponse;httpStatus=(int)device.StatusCode;
            Require(device.StatusCode==HttpStatusCode.OK);
            using var flow=ObjectJson(await device.Content.ReadAsStringAsync(deadline.Token));
            var value=flow.RootElement; string uri=Text(value,"verification_uri"), code=Text(value,"user_code");
            Require(new[]{"https://login.microsoft.com/device","https://microsoft.com/devicelogin","https://www.microsoft.com/devicelogin"}.Contains(uri));
            Require(Regex.IsMatch(code,"^[A-Z0-9-]{6,20}$"));
            var deviceCode=Text(value,"device_code"); Require(deviceCode.Length is >0 and <=8192);
            int expires=value.GetProperty("expires_in").GetInt32();
            int interval=value.TryGetProperty("interval",out var field)?field.GetInt32():5;
            Require(expires>0 && interval is >0 and <=60);
            var remaining=TimeSpan.FromSeconds(Math.Min(expires,600))-elapsed.Elapsed;
            Require(remaining>TimeSpan.Zero); deadline.CancelAfter(remaining);
            showCode(uri,code); // Only the projected public code, never server message or private code.
            while(true)
            {
                stage=AuthStage.TokenPoll;httpStatus=null;
                await delay(TimeSpan.FromSeconds(interval),deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                using var request=new HttpRequestMessage(HttpMethod.Post,TokenUrl)
                {Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"client_id",Client},
                    {"grant_type","urn:ietf:params:oauth:grant-type:device_code"},{"device_code",deviceCode}})};
                using var response=await http.SendAsync(request,deadline.Token);
                stage=AuthStage.TokenResponse;httpStatus=(int)response.StatusCode;
                var raw=await response.Content.ReadAsStringAsync(deadline.Token);
                if(response.StatusCode==HttpStatusCode.BadRequest)
                {
                    using var error=ObjectJson(raw); var oauthError=Text(error.RootElement,"error");
                    if(oauthError=="authorization_pending") continue;
                    if(oauthError=="slow_down") {interval=checked(interval+5);Require(interval<=120);continue;}
                    reason=oauthError switch {"consent_required"=>AuthReason.ConsentRequired,"authorization_declined" or "access_denied"=>AuthReason.Declined,"expired_token"=>AuthReason.Expired,"invalid_grant"=>AuthReason.InvalidGrant,_=>AuthReason.OAuthStopped};
                    throw Stop(); // Consent and all other errors stop without retry/recovery.
                }
                Require(response.StatusCode==HttpStatusCode.OK);
                stage=AuthStage.Context;
                var parsed=Parse(raw,clock());
                stage=AuthStage.GraphMe;httpStatus=null;
                using var meRequest=new HttpRequestMessage(HttpMethod.Get,MeUrl);
                meRequest.Headers.Authorization=new AuthenticationHeaderValue("Bearer",parsed.Token);
                using var me=await http.SendAsync(meRequest,deadline.Token);
                httpStatus=(int)me.StatusCode;
                Require(me.StatusCode==HttpStatusCode.OK);
                using var profile=ObjectJson(await me.Content.ReadAsStringAsync(deadline.Token));
                Check(AuthCheck.MeIdentity,()=>Text(profile.RootElement,"id")==Operator &&
                    string.Equals(Text(profile.RootElement,"userPrincipalName"),Account,StringComparison.OrdinalIgnoreCase));
                Require(parsed.Expiry>clock().ToUnixTimeSeconds());
                return new(parsed.Token,parsed.Expiry,parsed.Scopes,clock);
            }
        }
        catch(Exception ex)
        {
            var safe=Stop(stage==AuthStage.Context?AuthCheck.TokenShape:AuthCheck.Unspecified);
            if(ex.Message=="scoped_lab_auth_stopped" && ex.Data["check"] is AuthCheck check)safe.Data["check"]=check;
            if(ex is HttpRequestException)reason=AuthReason.Transport;
            if(ex is OperationCanceledException)reason=ct.IsCancellationRequested?AuthReason.Cancelled:AuthReason.Timeout;
            safe.Data["stage"]=stage;safe.Data["reason"]=reason;
            if(httpStatus.HasValue)safe.Data["http_status"]=httpStatus.Value;
            throw safe; // Typed allowlisted categories only; never raw errors or claims.
        }
    }

    public async Task<string?> GetMgGraphAccessTokenAsync(string tenantId,IEnumerable<string> scopes,
        bool useDeviceCode=false,string? clientAppId=null,CancellationToken ct=default,string? loginHint=null,bool forceRefresh=false)
    {
        await Task.CompletedTask; ct.ThrowIfCancellationRequested();
        Require(_token is not null && _expiry>_clock().ToUnixTimeSeconds());
        Require(tenantId==Tenant && clientAppId==Client && (loginHint is null || string.Equals(loginHint,Account,StringComparison.OrdinalIgnoreCase)));
        foreach(var scope in scopes)
        {
            if(scope=="https://graph.microsoft.com/.default") continue; // Existing token only; no new acquisition/grant.
            var name=scope.StartsWith("https://graph.microsoft.com/",StringComparison.Ordinal)?scope[28..]:scope;
            Require(_scopes.Contains(name));
        }
        return _token;
    }
    public async Task<string> GetAccessTokenAsync(string resourceUrl,string? tenantId=null,bool forceRefresh=false,
        string? clientId=null,IEnumerable<string>? scopes=null,bool useInteractiveBrowser=true,string? userId=null,CancellationToken ct=default)
    {
        Require(resourceUrl is "https://graph.microsoft.com" or "https://graph.microsoft.com/");
        return (await GetMgGraphAccessTokenAsync(tenantId??"",scopes??["User.Read"],clientAppId:clientId,ct:ct,loginHint:userId,forceRefresh:forceRefresh))!;
    }
    public Task<string?> ResolveLoginHintFromCacheAsync()=>Task.FromResult<string?>(Account); // Configured value, no cache access.
    public Task ClearTokenCacheAsync()=>Task.FromException(Stop());
    public void Dispose(){_token=null;_scopes.Clear();}
}
