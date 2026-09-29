using System.Text.Json;
using System.Text.RegularExpressions;
using P=Microsoft.Agents.A365.DevTools.Cli.Services.ScopedLabAuthentication;
using static NativeApi;

internal sealed class NativeConfig
{
    internal static string Workspace=>LabSettings.Workspace;
    internal static string RunRoot=>LabSettings.RunRoot;
    internal required NativeTarget[] Targets;internal required string Scope,Reference,Journal;
    internal required string[] Passes;internal bool Writes;
    internal static void Keys(JsonElement value,params string[] fields){Need(value.ValueKind==JsonValueKind.Object,"protocol_shape");var keys=value.EnumerateObject().Select(x=>x.Name).ToArray();Need(keys.Length==fields.Length&&keys.ToHashSet().SetEquals(fields),"protocol_fields");}
    internal static NativeConfig Read(JsonElement b)
    {
        try{
            Keys(b,"schema","tenant_id","blueprint_id","client_id","operator_id","operator_upn","workspace_id","scope_sha256","approval_reference","pass_ids","allow_writes","targets","journal_directory");
            Need(b.GetProperty("schema").GetInt32()==1&&Text(b,"tenant_id")==P.Tenant&&Text(b,"client_id")==P.Client&&Text(b,"operator_id")==P.Operator&&Text(b,"operator_upn")==P.Account&&Text(b,"blueprint_id")==Blueprint&&Text(b,"workspace_id")==Workspace,"native_config_context");
            string scope=Text(b,"scope_sha256"),reference=Text(b,"approval_reference"),journal=Text(b,"journal_directory");
            Need(Regex.IsMatch(scope,"\\A[0-9a-f]{64}\\z")&&Regex.IsMatch(reference,"\\A[a-z0-9][a-z0-9-]{1,63}\\z"),"native_config_binding");
            Need(Path.IsPathFullyQualified(journal)&&Path.GetFullPath(journal).StartsWith(RunRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"native_journal_path");
            var passes=b.GetProperty("pass_ids").EnumerateArray().Select(x=>x.GetString()??"").ToArray();Need(passes.Length is >=1 and <=2&&passes.All(GuidId)&&passes.Distinct().Count()==passes.Length,"native_passes");
            var targets=new List<NativeTarget>();
            foreach(var t in b.GetProperty("targets").EnumerateArray()){
                bool named=t.TryGetProperty("registration_name",out _);
                if(named)Keys(t,"key","source_id","identity_name","identity_id","registration_id","registration_name");else Keys(t,"key","source_id","identity_name","identity_id","registration_id");
                string registrationName=named?Text(t,"registration_name"):Text(t,"identity_name");
                Need(registrationName.Length is >=1 and <=256&&!registrationName.Any(char.IsControl),"registration_name");
                targets.Add(new(Text(t,"key"),Text(t,"source_id"),Text(t,"identity_name"),t.GetProperty("identity_id").GetString(),t.GetProperty("registration_id").GetString()){RegistrationName=registrationName});
            }
            ValidateTargets(targets.ToArray());
            return new(){Targets=targets.ToArray(),Scope=scope,Reference=reference,Journal=Path.GetFullPath(journal),Passes=passes,Writes=b.GetProperty("allow_writes").GetBoolean()};
        }catch(NativeStop){throw;}catch{throw new NativeStop("native_config_shape");}
    }
    internal NativeTarget Action(JsonElement b,string operation)
    {
        var fields=new List<string>{"operation","scope_sha256","run_id","tenant_id","blueprint_id","client_id","operator_id","workspace_id","target_key","source_id","identity_name"};
        if(operation=="register_agent"){fields.Add("identity_id");fields.Add("registration_name");}Keys(b,fields.ToArray());
        Need(Writes&&Text(b,"operation")==operation&&Text(b,"scope_sha256")==Scope&&Passes.Contains(Text(b,"run_id"))&&Text(b,"tenant_id")==P.Tenant&&Text(b,"blueprint_id")==Blueprint&&Text(b,"client_id")==P.Client&&Text(b,"operator_id")==P.Operator&&Text(b,"workspace_id")==Workspace,"native_action_scope");
        var t=Targets.SingleOrDefault(t=>t.Key==Text(b,"target_key"));Need(t is not null&&t.Source==Text(b,"source_id")&&t.Name==Text(b,"identity_name"),"native_action_target");
        if(operation=="register_agent")Need(Text(b,"registration_name")==t!.RegistrationName,"registration_name_scope");return t!;
    }
}

internal sealed class NativeProtocol(NativeConfig config,NativeApi api,Func<CancellationToken,Task> tokenCheck)
{
    readonly HashSet<string> seen=[];
    internal async Task<JsonElement> Handle(JsonElement command,CancellationToken ct)
    {
        string request=Text(command,"request_id"),method=Text(command,"method");Need(GuidId(request)&&seen.Count<32&&seen.Add(request),"native_request_reused");
        object? result;
        switch(method){
            case "context":NativeConfig.Keys(command,"request_id","method");await tokenCheck(ct);result=new{tenant_id=P.Tenant,blueprint_id=NativeApi.Blueprint,client_id=P.Client,operator_id=P.Operator};break;
            case "inspect_identity":NativeConfig.Keys(command,"request_id","method","id");result=await api.InspectIdentity(Text(command,"id"),ct);break;
            case "get_identity":NativeConfig.Keys(command,"request_id","method","id");result=await api.GetIdentity(Text(command,"id"),ct);break;
            case "find_identity":NativeConfig.Keys(command,"request_id","method","name");result=await api.FindIdentity(Text(command,"name"),ct);break;
            case "get_registration":NativeConfig.Keys(command,"request_id","method","id");result=await api.GetRegistration(Text(command,"id"),ct);break;
            case "create_identity":{
                NativeConfig.Keys(command,"request_id","method","action");var a=command.GetProperty("action");var t=config.Action(a,"create_identity");result=await api.CreateIdentity(t.Key,t.Source,ct);break;
            }
            case "register_agent":{
                NativeConfig.Keys(command,"request_id","method","action");var a=command.GetProperty("action");var t=config.Action(a,"register_agent");result=await api.Register(t.Key,t.Source,Text(a,"identity_id"),ct);break;
            }
            default:throw new NativeStop("native_method_denied");
        }
        return Element(new{status="native_response",request_id=request,method,result});
    }
}
