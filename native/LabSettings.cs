using System.Text.Json;
using static NativeApi;

// No environment/default tenant or account. The hash-checked config is the only input.
internal static class LabSettings
{
    internal static string Tenant { get; private set; } = "";
    internal static string Client { get; private set; } = "";
    internal static string Operator { get; private set; } = "";
    internal static string Account { get; private set; } = "";
    internal static string Blueprint { get; private set; } = "";
    internal static string Workspace { get; private set; } = "";
    internal static string RunRoot { get; private set; } = "";
    private static bool configured;

    internal static void Configure(JsonElement config, string root)
    {
        Need(!configured, "lab_settings_already_configured");
        var keys = new[] { "tenant_id", "client_id", "operator_id", "blueprint_id", "workspace_id" };
        var ids = keys.Select(k => Text(config, k)).ToArray();
        Need(ids.All(GuidId) && ids.Distinct().Count() == ids.Length, "lab_context_ids");
        var account = Text(config, "operator_upn");
        Need(account.Length is >=3 and <=254 && account.Count(c => c == '@') == 1 &&
             !account.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)), "lab_operator_upn");
        Need(Path.IsPathFullyQualified(root), "lab_run_root");
        Tenant = ids[0]; Client = ids[1]; Operator = ids[2]; Blueprint = ids[3]; Workspace = ids[4];
        Account = account; RunRoot = Path.GetFullPath(root); configured = true;
    }

    internal static void Synthetic()
    {
        Configure(Element(new {
            tenant_id = "a0000000-0000-4000-8000-000000000001",
            client_id = "a0000000-0000-4000-8000-000000000002",
            operator_id = "a0000000-0000-4000-8000-000000000003",
            blueprint_id = "a0000000-0000-4000-8000-000000000004",
            workspace_id = "a0000000-0000-4000-8000-000000000005",
            operator_upn = "operator@example.invalid"
        }), Path.Combine(Path.GetTempPath(), "dify-a365-offline-config-" + Guid.NewGuid().ToString("N")));
    }
}
