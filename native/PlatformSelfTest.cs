using System.Net;
using System.Reflection;
using System.Text.Json;
internal static class PlatformSelfTest
{
    internal static async Task<int> Run()
    {
        NativeTarget[] Items(int count)=>Enumerable.Range(1,count).Select(i=>new NativeTarget("item"+i,$"10000000-0000-4000-8000-{i:000000000000}","Synthetic "+i,null,null)).ToArray();
        NativeApi.ValidateTargets(Items(5));
        bool denied=false;try{NativeApi.ValidateTargets(Items(6));}catch(NativeStop){denied=true;}
        if(!denied)throw new Exception("six_target_bound_not_enforced");
        var method=typeof(NativeApi).GetMethod("InspectIdentity",BindingFlags.Instance|BindingFlags.NonPublic);
        NativeApi.Need(method is not null,"identity_inspection_missing");
        const string id="20000000-0000-4000-8000-000000000001";
        int reads=0;
        var auth=new NativeSelfTest.FakeAuth();
        var target=new NativeTarget("known","10000000-0000-4000-8000-000000000001","Planned Name",id,null);
        using var api=new NativeApi([target],auth,auth,"synthetic-token",new NativeSelfTest.Network(req=>{
            reads++;NativeApi.Need(req.Method==HttpMethod.Get&&req.RequestUri!.AbsoluteUri==NativeApi.IdentityUrl(id),"inspect_network_scope");
            return Task.FromResult(new HttpResponseMessage(reads==1?HttpStatusCode.OK:HttpStatusCode.NotFound){Content=new StringContent(JsonSerializer.Serialize(reads==1?new{id,appId=id,displayName="Actual Existing Name",accountEnabled=true,agentIdentityBlueprintId=NativeApi.Blueprint}:(object)new{}))});
        }),Path.GetTempPath(),false);
        async Task<JsonElement> Inspect(string value)=>await (Task<JsonElement>)method!.Invoke(api,[value,CancellationToken.None])!;
        var result=await Inspect(id);
        NativeApi.Need(result.GetProperty("http_status").GetInt32()==200&&result.GetProperty("identity").GetProperty("displayName").GetString()=="Actual Existing Name"&&!target.IdentityVerified,"inspection_is_not_adoption");
        result=await Inspect(id);NativeApi.Need(result.GetProperty("http_status").GetInt32()==404,"inspection_missing_status");
        denied=false;try{await Inspect("20000000-0000-4000-8000-000000000099");}catch(NativeStop){denied=true;}
        NativeApi.Need(denied&&reads==2,"inspection_outside_scope");
        var displayField=typeof(NativeTarget).GetField("RegistrationName",BindingFlags.Instance|BindingFlags.NonPublic);
        NativeApi.Need(displayField is not null,"registration_display_name_missing");
        var named=new NativeTarget("named","10000000-0000-4000-8000-000000000002","Legacy Identity Name",id,null);
        displayField!.SetValue(named,"Discovered Source Name");int posts=0;
        var dir=Path.Combine(Path.GetTempPath(),"dify-platform-name-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        using(var namingApi=new NativeApi([named],auth,auth,"synthetic-token",new NativeSelfTest.Network(async req=>{
            object body;
            if(req.Method==HttpMethod.Get)body=req.RequestUri!.AbsoluteUri==NativeApi.IdentityUrl(id)?new{id,appId=id,displayName="Legacy Identity Name",accountEnabled=true,agentIdentityBlueprintId=NativeApi.Blueprint}:(object)new{id=Microsoft.Agents.A365.DevTools.Cli.Services.ScopedLabAuthentication.Operator};
            else{posts++;using var json=JsonDocument.Parse(await req.Content!.ReadAsStringAsync());NativeApi.Need(json.RootElement.GetProperty("displayName").GetString()=="Discovered Source Name","source_display_name_not_used");body=new{id="30000000-0000-4000-8000-000000000002"};}
            return new HttpResponseMessage(req.Method==HttpMethod.Post?HttpStatusCode.Created:HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(body))};
        }),dir,true)){
            await namingApi.GetIdentity(id,default);await namingApi.Register(named.Key,named.Source,id,default);
            NativeApi.Need(posts==1&&named.Name=="Legacy Identity Name","identity_renamed_by_registration");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{status="platform_adapter_tests_passed",checks=9,live_http=false}));return 0;
    }
}
