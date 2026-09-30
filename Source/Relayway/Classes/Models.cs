using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Authentication;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;

namespace Relayway;

public class Configuration
{
    public SmtpConfiguration Smtp { get; init; } = new();
    public MicrosoftGraphConfiguration? Graph { get; init; }
    public string? SendFrom { get; init; }
}

public class SmtpConfiguration
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 2525;
    public string AllowedNetworks { get; init; } = "";
    public int MaxMessageSizeMb { get; init; } = 35;
}

public class MicrosoftGraphConfiguration
{
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string Cloud { get; init; } = "Global";
}

public record GraphCloud(Uri AuthorityHost, string GraphHost)
{
    public static readonly IReadOnlyDictionary<string, GraphCloud> All = new Dictionary<string, GraphCloud>(StringComparer.OrdinalIgnoreCase)
    {
        ["Global"] = new(AzureAuthorityHosts.AzurePublicCloud, "graph.microsoft.com"),
        ["USGovernment"] = new(AzureAuthorityHosts.AzureGovernment, "graph.microsoft.us"),
        ["USGovernmentDoD"] = new(AzureAuthorityHosts.AzureGovernment, "dod-graph.microsoft.us"),
        ["China"] = new(AzureAuthorityHosts.AzureChina, "microsoftgraph.chinacloudapi.cn"),
    };

    public (GraphServiceClient Graph, IRequestAdapter Uploads) Connect(string tenantId, string clientId, string clientSecret)
    {
        HttpClient http = GraphClientFactory.Create();
        ClientSecretCredential credential = new(tenantId, clientId, clientSecret, new ClientSecretCredentialOptions { AuthorityHost = AuthorityHost });
        AzureIdentityAuthenticationProvider auth = new(credential, [GraphHost], null, true, $"https://{GraphHost}/.default");
        return (new GraphServiceClient(http, auth, $"https://{GraphHost}/v1.0"), new BaseGraphRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: http));
    }
}
