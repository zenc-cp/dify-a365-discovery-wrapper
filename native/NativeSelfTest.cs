using System.Net;
using System.Text.Json;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using P=Microsoft.Agents.A365.DevTools.Cli.Services.ScopedLabAuthentication;
internal static class NativeSelfTest
{
    const string Source="10000000-0000-4000-8000-000000000001",Identity="20000000-0000-4000-8000-000000000001",Registration="30000000-0000-4000-8000-000000000001";
    static int count;
    static string ReadActiveLog(string path){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new StreamReader(stream);return reader.ReadToEnd();}
    static void Check(bool condition){if(!condition)throw new Exception("native_test_failed");count++;}
    static async Task Denied(Func<Task> action){try{await action();throw new Exception("unexpected_success");}catch(NativeStop){count++;}}
    static HttpResponseMessage Response(int status,object body)=>new((HttpStatusCode)status){Content=new StringContent(JsonSerializer.Serialize(body))};
    internal static async Task<int> Run()
    {
        var directory=Path.Combine(Path.GetTempPath(),"dify-native-offline-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var auth=new FakeAuth();
        foreach(int status in new[]{201,409,503})
        {
            var target=new NativeTarget("new",Source,"Synthetic Agent",Identity,null);int posts=0;string? sent=null;
            string receipt=Path.Combine(directory,"registry-"+status);Directory.CreateDirectory(receipt);
            using var api=new NativeApi([target],auth,auth,"synthetic-token",new Network(async req=>{
                Check(req.Headers.Authorization?.Parameter=="synthetic-token");
                if(req.Method==HttpMethod.Get){
                    if(req.RequestUri!.AbsoluteUri==NativeApi.IdentityUrl(Identity))return Response(200,new{id=Identity,appId=Identity,displayName="Synthetic Agent",accountEnabled=true,agentIdentityBlueprintId=NativeApi.Blueprint});
                    Check(req.RequestUri.AbsoluteUri==NativeApi.MeUrl);return Response(200,new{id=P.Operator});
                }
                Check(req.Method==HttpMethod.Post&&req.RequestUri!.AbsoluteUri==NativeApi.RegistryBase);posts++;
                sent=await req.Content!.ReadAsStringAsync();using var doc=JsonDocument.Parse(sent);var body=doc.RootElement;
                Check(body.GetProperty("sourceAgentId").GetString()==Identity&&body.GetProperty("agentIdentityId").GetString()==Identity);
                Check(body.GetProperty("managedByAppId").GetString()=="59eca866-2f46-40b8-96ff-63f663121ef9");
                Check(Directory.GetFiles(receipt,"*.jsonl").Any(p=>ReadActiveLog(p).Contains("post_attempt")));
                return Response(status,new{id=status==409?"T_existing-registration":Registration});
            }),receipt,true);
            await Denied(async()=>{await api.Register("new",Source,Identity,default);});Check(posts==0);
            await api.GetIdentity(Identity,default);
            if(status==503)await Denied(async()=>{await api.Register("new",Source,Identity,default);});
            else {
                var result=await api.Register("new",Source,Identity,default);
                Check(result.GetProperty("created").GetBoolean()==(status==201));
                Check(result.GetProperty("native_method").GetString()=="GraphApiService.RegisterAgentInstanceAsyncV2");
                Check(result.GetProperty("id").GetString()==(status==409?"T_existing-registration":Registration));
                await Denied(async()=>{await api.Register("new",Source,Identity,default);});
            }
            Check(posts==1&&sent is not null);
            Check(!Directory.GetFiles(receipt).Any(p=>File.ReadAllText(p).Contains("synthetic-token")));
        }
        // Existing prefixed registration uses one exact GET and cannot be re-registered.
        int reads=0;
        using(var api=new NativeApi([new("known",Source,"Synthetic Agent",Identity,"T_existing-registration")],auth,auth,"synthetic-token",new Network(req=>{
            reads++;Check(req.Method==HttpMethod.Get&&req.RequestUri!.AbsoluteUri.Contains("/T_existing-registration?"));
            return Task.FromResult(Response(200,new{id="T_existing-registration",sourceAgentId=Identity,agentIdentityId=Identity,agentIdentityBlueprintId=NativeApi.Blueprint}));
        }),directory,false)){
            var record=await api.GetRegistration("T_existing-registration",default);Check(record!.Value.GetProperty("id").GetString()=="T_existing-registration");
            await Denied(async()=>{await api.Register("known",Source,Identity,default);});Check(reads==1);
            await Denied(async()=>{await api.GetRegistration("existing-registration",default);});Check(reads==1);
        }
        // Real v1 identity request generation and single-use intent, with HTTP replaced.
        int identityPosts=0;
        using(var api=new NativeApi([new("new",Source,"Synthetic Agent",null,null)],auth,auth,"synthetic-token",new Network(async req=>{
            if(req.Method==HttpMethod.Get)return Response(200,new{value=Array.Empty<object>()});
            identityPosts++;Check(req.RequestUri!.AbsoluteUri==NativeApi.IdentityCreateUrl);
            using var doc=JsonDocument.Parse(await req.Content!.ReadAsStringAsync());var b=doc.RootElement;
            Check(b.GetProperty("displayName").GetString()=="Synthetic Agent");Check(b.GetProperty("sponsors@odata.bind")[0].GetString()=="https://graph.microsoft.com/v1.0/users/"+P.Operator);
            return Response(201,new{id=Identity});
        }),Path.Combine(directory,"identity"),true)){
            await Denied(async()=>{await api.CreateIdentity("new",Source,default);});Check(identityPosts==0);
            var lookup=await api.FindIdentity("Synthetic Agent",default);Check(lookup.GetProperty("complete").GetBoolean());
            var result=await api.CreateIdentity("new",Source,default);Check(result.GetProperty("id").GetString()==Identity);
            await Denied(async()=>{await api.CreateIdentity("new",Source,default);});Check(identityPosts==1);
        }
        // No write flag means no HTTP POST, even after an empty lookup.
        using(var api=new NativeApi([new("new",Source,"Synthetic Agent",null,null)],auth,auth,"synthetic-token",new Network(_=>Task.FromResult(Response(200,new{value=Array.Empty<object>()}))),directory,false)){
            await api.FindIdentity("Synthetic Agent",default);await Denied(async()=>{await api.CreateIdentity("new",Source,default);});
        }
        // Redirects and incomplete lookup are not evidence of absence.
        foreach(int mode in new[]{0,1})using(var api=new NativeApi([new("new",Source,"Synthetic Agent",null,null)],auth,auth,"synthetic-token",new Network(_=>Task.FromResult(mode==0?Response(302,new{}):Response(200,new Dictionary<string,object>{{"value",Array.Empty<object>()},{"@odata.nextLink","https://example.invalid"}}))),directory,true)){
            await Denied(async()=>{await api.FindIdentity("Synthetic Agent",default);});
            await Denied(async()=>{await api.CreateIdentity("new",Source,default);});
        }
        var config=new Dictionary<string,object>{["schema"]=1,["tenant_id"]=P.Tenant,["client_id"]=P.Client,["operator_id"]=P.Operator,["operator_upn"]=P.Account,["blueprint_id"]=NativeApi.Blueprint,["workspace_id"]=NativeConfig.Workspace,["scope_sha256"]=new string('a',64),["approval_reference"]="offline-unit",["pass_ids"]=new[]{Source},["allow_writes"]=false,["journal_directory"]=Path.Combine(NativeConfig.RunRoot,"offline-unit","native"),["targets"]=new[]{new{key="known",source_id=Source,identity_name="Synthetic Agent",identity_id=Identity,registration_id="T_existing-registration"}}};
        var parsed=NativeConfig.Read(NativeApi.Element(config));Check(parsed.Targets[0].Registration=="T_existing-registration"&&!parsed.Writes);
        foreach(string key in new[]{"tenant_id","client_id","operator_id","blueprint_id","workspace_id","scope_sha256","journal_directory"}){
            var bad=new Dictionary<string,object>(config){[key]="wrong"};await Denied(()=>{NativeConfig.Read(NativeApi.Element(bad));return Task.CompletedTask;});
        }
        int protocolGets=0;
        using(var api=new NativeApi(parsed.Targets,auth,auth,"synthetic-token",new Network(req=>{
            protocolGets++;Check(req.Method==HttpMethod.Get);
            return Task.FromResult(req.RequestUri!.AbsoluteUri==NativeApi.IdentityUrl(Identity)?Response(200,new{id=Identity,appId=Identity,displayName="Synthetic Agent",accountEnabled=true,agentIdentityBlueprintId=NativeApi.Blueprint}):Response(200,new{id="T_existing-registration",agentIdentityId=Identity,sourceAgentId=Identity,agentIdentityBlueprintId=NativeApi.Blueprint}));
        }),directory,false)){
            var protocol=new NativeProtocol(parsed,api,_=>Task.CompletedTask);
            string req=Guid.NewGuid().ToString();var command=NativeApi.Element(new{request_id=req,method="context"});var reply=await protocol.Handle(command,default);
            Check(reply.GetProperty("request_id").GetString()==req&&reply.GetProperty("result").GetProperty("tenant_id").GetString()==P.Tenant);
            await Denied(async()=>{await protocol.Handle(command,default);});
            reply=await protocol.Handle(NativeApi.Element(new{request_id=Guid.NewGuid().ToString(),method="get_identity",id=Identity}),default);Check(reply.GetProperty("result").GetProperty("id").GetString()==Identity);
            reply=await protocol.Handle(NativeApi.Element(new{request_id=Guid.NewGuid().ToString(),method="get_registration",id="T_existing-registration"}),default);Check(reply.GetProperty("result").GetProperty("id").GetString()=="T_existing-registration");
            await Denied(async()=>{await protocol.Handle(NativeApi.Element(new{request_id=Guid.NewGuid().ToString(),method="delete"}),default);});Check(protocolGets==2);
        }
        Console.WriteLine(JsonSerializer.Serialize(new{status="offline_native_tests_passed",assertions=count,live_http=false,native_registration_method_executed=true}));return 0;
    }
    internal sealed class Network(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>send(r);}
    internal sealed class FakeAuth:IAuthenticationService,IMicrosoftGraphTokenProvider
    {
        public Task<string?> GetMgGraphAccessTokenAsync(string tenantId,IEnumerable<string> scopes,bool useDeviceCode=false,string? clientAppId=null,CancellationToken ct=default,string? loginHint=null,bool forceRefresh=false){Check(tenantId==P.Tenant&&clientAppId==P.Client);return Task.FromResult<string?>("synthetic-token");}
        public Task<string> GetAccessTokenAsync(string resourceUrl,string? tenantId=null,bool forceRefresh=false,string? clientId=null,IEnumerable<string>? scopes=null,bool useInteractiveBrowser=true,string? userId=null,CancellationToken ct=default)=>Task.FromResult("synthetic-token");
        public Task<string?> ResolveLoginHintFromCacheAsync()=>Task.FromResult<string?>(P.Account);
        public Task ClearTokenCacheAsync()=>throw new Exception("unexpected_cache_access");
    }
}
