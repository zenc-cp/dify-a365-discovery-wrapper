using System.Text.Json;
internal sealed partial class NativeApi
{
    // A read-only observation is NOT adoption or authorization to register.
    internal async Task<JsonElement> InspectIdentity(string id,CancellationToken ct)
    {
        Need(!writes&&targets.Any(t=>t.Identity==id),"identity_inspection_scope");
        await Token("AgentIdentity.Read.All",ct);
        var value=await Get(IdentityUrl(id),ct);
        if(value is null)return Element(new{http_status=404,identity=(object?)null});
        var b=value.Value;Need(Text(b,"id")==id,"identity_inspection_id");
        string name=Text(b,"displayName");Need(name.Length<=256&&!name.Any(char.IsControl),"identity_inspection_name");
        return Element(new{http_status=200,identity=new{id,appId=Text(b,"appId"),displayName=name,accountEnabled=b.GetProperty("accountEnabled").GetBoolean(),agentIdentityBlueprintId=Text(b,"agentIdentityBlueprintId")}});
    }
}
