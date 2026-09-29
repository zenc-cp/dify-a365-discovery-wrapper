using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using P=Microsoft.Agents.A365.DevTools.Cli.Services.ScopedLabAuthentication;

internal sealed class NativeStop(string code):Exception(code);
internal sealed class NativeTarget(string key,string source,string name,string? identity,string? registration)
{
    internal string Key=key,Source=source,Name=name,RegistrationName=name;
    internal string? Identity=identity,Registration=registration;
    internal bool EmptyLookup,IdentityVerified;
}
internal sealed partial class NativeApi:IDisposable
{
    internal static string Blueprint=>LabSettings.Blueprint;
    internal const string RegistryBase="https://graph.microsoft.com/beta/copilot/agentRegistrations";
    internal const string IdentityBase="https://graph.microsoft.com/v1.0/servicePrincipals";
    internal const string IdentityCreateUrl=IdentityBase+"/microsoft.graph.agentIdentity";
    internal const string MeUrl="https://graph.microsoft.com/v1.0/me?$select=id";
    const string Fields="id,appId,displayName,accountEnabled,agentIdentityBlueprintId";
    readonly NativeTarget[] targets;readonly IAuthenticationService auth;readonly IMicrosoftGraphTokenProvider tokens;
    readonly Gate gate;readonly HttpClient http;readonly string directory;readonly bool writes;
    internal static void Need(bool ok,string code="native_contract"){if(!ok)throw new NativeStop(code);}
    internal static bool GuidId(string? value)=>Guid.TryParseExact(value,"D",out var id)&&id.ToString()==value;
    internal static bool RegistryId(string? value)=>value is not null&&Regex.IsMatch(value,"\\A[A-Za-z0-9_-]{1,128}\\z");
    internal static string Text(JsonElement b,string key)=>b.GetProperty(key).GetString()??throw new NativeStop("native_shape");
    internal static JsonElement Parse(string text){Need(text.Length<=65536,"native_size");using var doc=JsonDocument.Parse(text);var b=doc.RootElement;Need(b.ValueKind==JsonValueKind.Object,"native_shape");var keys=new HashSet<string>();foreach(var x in b.EnumerateObject())Need(keys.Add(x.Name),"duplicate_json_key");return b.Clone();}
    internal static JsonElement Element(object value)=>JsonSerializer.SerializeToElement(value);
    static string Canonical(string uri)=>new Uri(uri).AbsoluteUri;
    internal static string IdentityUrl(string id){Need(GuidId(id));return IdentityBase+"/"+id+"/microsoft.graph.agentIdentity?$select="+Fields;}
    internal static string RegistrationUrl(string id){Need(RegistryId(id));return RegistryBase+"/"+Uri.EscapeDataString(id)+"?$select=id,sourceAgentId,agentIdentityId,agentIdentityBlueprintId";}
    static string FindUrl(string name)=>IdentityCreateUrl+"?$filter="+Uri.EscapeDataString("displayName eq '"+name.Replace("'","''")+"'")+"&$select="+Fields+"&$top=100";
    internal static void ValidateTargets(NativeTarget[] targets)
    {
        Need(targets.Length is >=1 and <=5,"native_target_count");
        Need(targets.All(t=>Regex.IsMatch(t.Key,"\\A[a-z][a-z0-9-]{0,47}\\z")&&GuidId(t.Source)&&t.Name.Length is >=1 and <=256&&!t.Name.Any(char.IsControl)&&(t.Identity is null||GuidId(t.Identity))&&(t.Registration is null||(t.Identity is not null&&RegistryId(t.Registration)))),"native_targets");
        Need(targets.Select(t=>t.Key).Distinct().Count()==targets.Length&&targets.Select(t=>t.Source).Distinct().Count()==targets.Length&&targets.Select(t=>t.Name.ToLowerInvariant()).Distinct().Count()==targets.Length,"native_duplicates");
        foreach(var values in new[]{targets.Where(t=>t.Identity is not null).Select(t=>t.Identity),targets.Where(t=>t.Registration is not null).Select(t=>t.Registration)})Need(values.Distinct().Count()==values.Count(),"native_duplicates");
    }
    internal NativeApi(NativeTarget[] targets,IAuthenticationService auth,IMicrosoftGraphTokenProvider tokens,string token,HttpMessageHandler network,string directory,bool writes)
    {
        ValidateTargets(targets);
        this.targets=targets;this.auth=auth;this.tokens=tokens;this.directory=directory;this.writes=writes;
        gate=new Gate(token,network);http=new HttpClient(gate,false){Timeout=TimeSpan.FromSeconds(20),MaxResponseContentBufferSize=65536};
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
    }
    async Task Token(string scope,CancellationToken ct)=>await tokens.GetMgGraphAccessTokenAsync(P.Tenant,[scope],clientAppId:P.Client,loginHint:P.Account,ct:ct);
    NativeTarget Target(string key,string source){var t=targets.SingleOrDefault(t=>t.Key==key);Need(t is not null&&t.Source==source,"native_target_scope");return t!;}
    StreamWriter Intent(NativeTarget target,string kind)
    {
        Need(writes,"native_writes_disabled");Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,target.Source+"-"+kind+".jsonl");Need(!File.Exists(path),"native_prior_intent");
        var log=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false));
        Append(log,new{status="native_intent",operation=kind,tenant_id=P.Tenant,source_id=target.Source,identity_id=target.Identity,blueprint_id=Blueprint});return log;
    }
    static void Append(StreamWriter log,object value){log.WriteLine(JsonSerializer.Serialize(value));log.Flush();((FileStream)log.BaseStream).Flush(true);}
    async Task<JsonElement?> Get(string url,CancellationToken ct)
    {
        gate.Begin([url],null,null,null);
        try{using var response=await http.GetAsync(url,ct);if(response.StatusCode==HttpStatusCode.NotFound)return null;Need(response.StatusCode==HttpStatusCode.OK,"native_http_status");return Parse(await response.Content.ReadAsStringAsync(ct));}
        catch{gate.Poisoned=true;throw new NativeStop("native_read_failed");}
    }
    internal async Task<JsonElement?> GetIdentity(string id,CancellationToken ct)
    {
        var t=targets.SingleOrDefault(t=>t.Identity==id);Need(t is not null,"identity_outside_scope");t!.IdentityVerified=false;await Token("AgentIdentity.Read.All",ct);
        var b=await Get(IdentityUrl(id),ct);if(b is null)return null;
        var v=b.Value;Need(Text(v,"id")==id&&Text(v,"appId")==id&&Text(v,"displayName")==t!.Name&&Text(v,"agentIdentityBlueprintId")==Blueprint&&v.GetProperty("accountEnabled").ValueKind==JsonValueKind.True,"identity_binding");
        if(v.TryGetProperty("@odata.type",out var type))Need(type.GetString()=="#microsoft.graph.agentIdentity","identity_type");
        t!.IdentityVerified=true;return Element(new{id,appId=id,displayName=t.Name,accountEnabled=true,agentIdentityBlueprintId=Blueprint});
    }
    internal async Task<JsonElement> FindIdentity(string name,CancellationToken ct)
    {
        var t=targets.SingleOrDefault(t=>t.Name==name);Need(t is not null&&t.Identity is null,"identity_lookup_scope");t!.EmptyLookup=false;await Token("AgentIdentity.Read.All",ct);
        var b=await Get(FindUrl(name),ct);Need(b is not null,"lookup_failed");var v=b!.Value;
        Need(!v.TryGetProperty("@odata.nextLink",out _)&&v.GetProperty("value").ValueKind==JsonValueKind.Array&&v.GetProperty("value").GetArrayLength()<=100,"lookup_incomplete");
        var rows=new List<object>();var seen=new HashSet<string>();
        foreach(var item in v.GetProperty("value").EnumerateArray()){
            string id=Text(item,"id");Need(GuidId(id)&&seen.Add(id)&&Text(item,"appId")==id&&string.Equals(Text(item,"displayName"),name,StringComparison.OrdinalIgnoreCase),"lookup_binding");rows.Add(new{id});
        }
        t.EmptyLookup=rows.Count==0;return Element(new{complete=true,items=rows});
    }
    internal async Task<JsonElement> CreateIdentity(string key,string source,CancellationToken ct)
    {
        var t=Target(key,source);Need(writes&&t.Identity is null&&t.EmptyLookup&&!gate.Poisoned,"identity_create_gate");await Token("AgentIdentity.Create.All",ct);
        t.EmptyLookup=false;using var log=Intent(t,"identity");
        var body=new Dictionary<string,object>{{"displayName",t.Name},{"agentIdentityBlueprintId",Blueprint},{"sponsors@odata.bind",new[]{"https://graph.microsoft.com/v1.0/users/"+P.Operator}}};
        gate.Begin([],IdentityCreateUrl,b=>{
            Need(b.EnumerateObject().Select(x=>x.Name).ToHashSet().SetEquals(body.Keys),"identity_payload");Need(Text(b,"displayName")==t.Name&&Text(b,"agentIdentityBlueprintId")==Blueprint,"identity_payload");var sponsor=b.GetProperty("sponsors@odata.bind");Need(sponsor.GetArrayLength()==1&&sponsor[0].GetString()=="https://graph.microsoft.com/v1.0/users/"+P.Operator,"identity_sponsor");
        },log);
        try{
            using var content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json");using var response=await http.PostAsync(IdentityCreateUrl,content,ct);
            Need(response.StatusCode==HttpStatusCode.Created,"identity_create_unverified");var b=Parse(await response.Content.ReadAsStringAsync(ct));string id=Text(b,"id");
            Need(GuidId(id)&&!targets.Any(x=>x.Identity==id)&&id!=Blueprint&&id!=P.Client&&id!=P.Operator&&id!=P.Tenant&&id!=source,"identity_create_id");t.Identity=id;
            Append(log,new{status="identity_candidate",id,http_status=201});return Element(new{id,http_status=201});
        }catch{gate.Poisoned=true;throw new NativeStop("identity_outcome_unknown");}finally{gate.End();}
    }
    internal async Task<JsonElement> Register(string key,string source,string identity,CancellationToken ct)
    {
        var t=Target(key,source);Need(writes&&t.Identity==identity&&t.IdentityVerified&&t.Registration is null&&!gate.Poisoned,"registration_create_gate");await Token("AgentRegistration.ReadWrite.All",ct);
        Need(t.RegistrationName.Length is >=1 and <=256&&!t.RegistrationName.Any(char.IsControl),"registration_name");
        using var log=Intent(t,"registration");
        gate.Begin([MeUrl],RegistryBase,b=>{
            var fields=new[]{"id","displayName","ownerIds","createdBy","sourceCreatedDateTime","sourceLastModifiedDateTime","agentIdentityBlueprintId","sourceAgentId","agentIdentityId","managedByAppId"};
            Need(b.EnumerateObject().Select(x=>x.Name).ToHashSet().SetEquals(fields),"registration_payload");Need(GuidId(Text(b,"id"))&&Text(b,"displayName")==t.RegistrationName&&Text(b,"createdBy")==P.Operator,"registration_payload");
            var owners=b.GetProperty("ownerIds");Need(owners.GetArrayLength()==1&&owners[0].GetString()==P.Operator,"registration_owner");
            Need(Text(b,"agentIdentityBlueprintId")==Blueprint&&Text(b,"sourceAgentId")==identity&&Text(b,"agentIdentityId")==identity&&Text(b,"managedByAppId")=="59eca866-2f46-40b8-96ff-63f663121ef9","registration_binding");
            Need(DateTimeOffset.TryParse(Text(b,"sourceCreatedDateTime"),out var now)&&Math.Abs((DateTimeOffset.UtcNow-now).TotalSeconds)<60&&Text(b,"sourceCreatedDateTime")==Text(b,"sourceLastModifiedDateTime"),"native_timestamp");
        },log);
        try{
            var native=new GraphApiService(NullLogger<GraphApiService>.Instance,new CommandExecutor(NullLogger<CommandExecutor>.Instance),auth,handler:gate,tokenProvider:tokens,loginHintResolver:()=>Task.FromResult<string?>(P.Account));native.CustomClientAppId=P.Client;
            var result=await native.RegisterAgentInstanceAsyncV2(P.Tenant,t.RegistrationName,null,Blueprint,identity,P.Client,ct);
            Need(RegistryId(result.Id)&&gate.PostCount==1&&((gate.LastStatus==201&&!result.AlreadyExisted)||(gate.LastStatus==409&&result.AlreadyExisted)),"native_registration_unverified");
            Need(!targets.Any(x=>x.Registration==result.Id),"duplicate_registration");t.Registration=result.Id;
            Append(log,new{status="registration_candidate",id=result.Id,reused=result.AlreadyExisted,http_status=gate.LastStatus});
            return Element(new{id=result.Id,http_status=gate.LastStatus,created=!result.AlreadyExisted,native_method="GraphApiService.RegisterAgentInstanceAsyncV2"});
        }catch(Exception ex){gate.Poisoned=true;var stop=new NativeStop("registration_outcome_unknown");stop.Data["exception_type"]=ex.GetType().Name;stop.Data["post_count"]=gate.PostCount;stop.Data["http_status"]=gate.LastStatus;stop.Data["check"]=ex is NativeStop?ex.Message:null;stop.Data["guard_category"]=ex.Data["guard_category"];stop.Data["guard_check"]=ex.Data["guard_check"];if(ex is FileNotFoundException missing)stop.Data["missing_assembly"]=missing.FileName;throw stop;}finally{gate.End();}
    }
    internal async Task<JsonElement?> GetRegistration(string id,CancellationToken ct)
    {
        var t=targets.SingleOrDefault(t=>t.Registration==id);Need(t is not null,"registration_outside_scope");await Token("AgentRegistration.ReadWrite.All",ct);
        var b=await Get(RegistrationUrl(id),ct);if(b is null)return null;var v=b.Value;
        Need(Text(v,"id")==id&&Text(v,"agentIdentityId")==t!.Identity&&Text(v,"sourceAgentId")==t.Identity&&Text(v,"agentIdentityBlueprintId")==Blueprint,"registration_binding");
        return Element(new{id,agentIdentityId=t!.Identity,sourceAgentId=t.Identity,agentIdentityBlueprintId=Blueprint});
    }
    public void Dispose(){http.Dispose();gate.Dispose();}
    internal sealed class Gate(string token,HttpMessageHandler network):DelegatingHandler(network)
    {
        HashSet<string> gets=[];string? post;Action<JsonElement>? validate;StreamWriter? log;
        internal int PostCount;internal int? LastStatus;internal bool Poisoned;
        internal void Begin(IEnumerable<string> urls,string? postUrl,Action<JsonElement>? check,StreamWriter? journal){Need(!Poisoned,"native_session_stopped");gets=urls.Select(Canonical).ToHashSet();post=postUrl;validate=check;log=journal;PostCount=0;LastStatus=null;}
        internal void End(){post=null;validate=null;log=null;gets.Clear();}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            try{
                Need(!Poisoned&&request.Headers.Authorization?.Scheme=="Bearer"&&request.Headers.Authorization.Parameter==token&&!request.Headers.Contains("Cookie"),"native_http_guard");
                var url=request.RequestUri!.AbsoluteUri;
                if(request.Method==HttpMethod.Get)Need(request.Content is null&&gets.Remove(url),"native_get_scope");
                else{
                    Need(request.Method==HttpMethod.Post&&post is not null&&url==Canonical(post)&&validate is not null&&log is not null&&PostCount==0,"native_post_scope");
                    string raw=await request.Content!.ReadAsStringAsync(ct);var b=Parse(raw);validate!(b);PostCount++;
                    Append(log!,new{status="post_attempt",attempt=PostCount,payload_sha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(),payload_id=b.TryGetProperty("id",out var id)?id.GetString():null});
                }
                using var bounded=CancellationTokenSource.CreateLinkedTokenSource(ct);bounded.CancelAfter(TimeSpan.FromSeconds(15));
                var response=await base.SendAsync(request,bounded.Token);Need((int)response.StatusCode is <300 or >=400,"native_redirect");
                await response.Content.LoadIntoBufferAsync(65536).WaitAsync(bounded.Token);
                if(request.Method==HttpMethod.Post){LastStatus=(int)response.StatusCode;Append(log!,new{status="post_status",http_status=LastStatus});}
                return response;
            }catch(Exception ex){Poisoned=true;var stop=new NativeStop("native_http_stopped");stop.Data["guard_category"]=ex.GetType().Name;stop.Data["guard_check"]=ex is NativeStop?ex.Message:null;throw stop;}
        }
    }
}
