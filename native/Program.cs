using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using P=Microsoft.Agents.A365.DevTools.Cli.Services.ScopedLabAuthentication;
using static NativeApi;
void Report(object value){Console.WriteLine(JsonSerializer.Serialize(value));Console.Out.Flush();}
var cliVersion=typeof(Microsoft.Agents.A365.DevTools.Cli.Services.GraphApiService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
if(cliVersion!="1.1.221"){Report(new{status="stopped",reason="pinned_cli_version_required",authentication_attempted=false});return 2;}
if(args.SequenceEqual(new[]{"--self-test"})){
    try{LabSettings.Synthetic();await AuthSelfTest.Run();var result=await NativeSelfTest.Run();return result==0?await PlatformSelfTest.Run():result;}
    catch(NativeStop ex){Report(new{status="offline_test_failed",reason=ex.Message,diagnostic=ex.Data});return 2;}
}
if(args.Length==0){Report(new{status="offline_help",component="wrapper_native_adapter",instruction="Use python lab.py --help; self-tests: --self-test",authentication_attempted=false});return 0;}
bool validateOnly=args.Length==4&&args[0]=="--validate-config";
if(!validateOnly&&!(args.Length==8&&args[0]=="--live"&&args[1]=="--ack-stop-on-consent"&&args[2]=="--config"&&args[4]=="--sha256"&&args[6]=="--approval-reference")){
    Report(new{status="stopped",reason="exact_arguments_required",authentication_attempted=false});return 2;
}
string phase="local_config";
try{
    string path=validateOnly?args[1]:args[3],expected=validateOnly?args[2]:args[5],reference=validateOnly?args[3]:args[7];
    Need(Path.IsPathFullyQualified(path)&&new FileInfo(path).Length<=32768,"native_config_file");
    var bytes=File.ReadAllBytes(path);Need(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()==expected,"native_config_hash");
    var raw=Parse(Encoding.UTF8.GetString(bytes));LabSettings.Configure(raw,Path.GetDirectoryName(Path.GetFullPath(path))!);var config=NativeConfig.Read(raw);Need(config.Reference==reference,"native_config_reference");
    if(validateOnly){Report(new{status="native_config_valid",scope_sha256=config.Scope,allow_writes=config.Writes,authentication_attempted=false});return 0;}
    // Command flags record external human approval. They NEVER substitute for it.
    Directory.CreateDirectory(config.Journal);
    using(var marker=new FileStream(Path.Combine(config.Journal,"native-session-started.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){
        JsonSerializer.Serialize(marker,new{approval_reference=reference,config_sha256=expected,scope_sha256=config.Scope,at_utc=DateTimeOffset.UtcNow});marker.Flush(true);
    }
    using var cancel=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancel.Cancel();};
    phase="authentication";
    using var authHttp=P.CreateHttpClient();
    using var auth=await P.AcquireAsync(authHttp,(uri,code)=>Report(new{status="device_code_ready",verification_uri=uri,user_code=code,expected_account=P.Account,expected_tenant=P.Tenant,expected_client=P.Client,instruction="Use only the configured lab account. Stop on consent or any unexpected account/application."}),cancel.Token);
    using var work=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);work.CancelAfter(TimeSpan.FromSeconds(1800));
    string token=(await auth.GetMgGraphAccessTokenAsync(P.Tenant,["User.Read","AgentIdentity.Read.All","AgentRegistration.ReadWrite.All"],clientAppId:P.Client,loginHint:P.Account,ct:work.Token))!;
    using var network=new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false,Credentials=null,ConnectTimeout=TimeSpan.FromSeconds(5)};
    using var api=new NativeApi(config.Targets,auth,auth,token,network,config.Journal,config.Writes);
    var protocol=new NativeProtocol(config,api,async ct=>{await auth.GetMgGraphAccessTokenAsync(P.Tenant,["User.Read","AgentIdentity.Read.All","AgentRegistration.ReadWrite.All"],clientAppId:P.Client,loginHint:P.Account,ct:ct);});
    Report(new{status="auth_ready",tenant_id=P.Tenant,client_id=P.Client,operator_id=P.Operator,scope_sha256=config.Scope});
    for(int index=0;index<=32;index++){
        phase="await_command";string? line=await Console.In.ReadLineAsync(work.Token);
        if(line is null){Report(new{status="native_session_complete",commands=index});return 0;}
        Need(line.Length<=16384,"native_command_size");phase="native_operation";
        Report(await protocol.Handle(Parse(line),work.Token));
    }
    throw new NativeStop("native_command_cap");
}catch(Exception ex){
    Report(new{status="stopped",phase,reason=ex is NativeStop?ex.Message:"native_host_stopped",auth_diagnostic=P.Diagnostic(ex),retry_authorized=false});return 2;
}
