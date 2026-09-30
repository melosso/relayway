using System.Reflection;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Relayway;
using Serilog;
using Serilog.Events;

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("Relayway");
Console.WriteLine(Assembly.GetEntryAssembly()!.GetName().Version?.ToString(3));
Console.ResetColor();

List<string> problems = [];
IConfiguration configuration;
try
{
    configuration = new ConfigurationBuilder()
        .AddRelaywaySources(AppContext.BaseDirectory, Environment.GetEnvironmentVariables(), problems)
        .Build();
}
catch (FileNotFoundException ex)
{
    Log.Logger = CreateLogger(LogEventLevel.Information);
    Log.Fatal(ex.Message);
    return 1;
}

Log.Logger = CreateLogger(Enum.TryParse(configuration["LogLevel"], ignoreCase: true, out LogEventLevel logLevel) ? logLevel : LogEventLevel.Information);
foreach (string problem in problems)
{
    Log.Warning("Configuration: {Problem}", problem);
}

Configuration? config;
try
{
    config = configuration.Get<Configuration>();
}
catch (InvalidOperationException ex)
{
    Log.Fatal("Invalid configuration: {Error}", ex.Message);
    return 1;
}

string[] missing = [.. new (string Key, string? Value)[]
{
    ("Graph:TenantId", config?.Graph?.TenantId),
    ("Graph:ClientId", config?.Graph?.ClientId),
    ("Graph:ClientSecret", config?.Graph?.ClientSecret),
    ("SendFrom", config?.SendFrom),
}.Where(setting => string.IsNullOrWhiteSpace(setting.Value)).Select(setting => setting.Key)];
if (config?.Graph is null || missing.Length > 0)
{
    Log.Fatal("Missing configuration: {Keys}. Set them in appsettings.json, .env or the environment (RELAYWAY_TENANT_ID, RELAYWAY_CLIENT_ID, RELAYWAY_CLIENT_SECRET, RELAYWAY_SEND_FROM)", string.Join(", ", missing));
    return 1;
}

if (!GraphCloud.All.TryGetValue(config.Graph.Cloud, out GraphCloud? cloud))
{
    Log.Fatal("Graph:Cloud {Cloud} is not one of {Clouds}", config.Graph.Cloud, string.Join(", ", GraphCloud.All.Keys));
    return 1;
}

if (config.Smtp.MaxMessageSizeMb is < 1 or > 150)
{
    Log.Fatal("Smtp:MaxMessageSizeMb {Size} is outside 1 to 150, the Exchange Online message size range", config.Smtp.MaxMessageSizeMb);
    return 1;
}

try
{
    Relay.ParseNetworks(config.Smtp.AllowedNetworks);
}
catch (FormatException ex)
{
    Log.Fatal(ex.Message);
    return 1;
}

System.Net.IPAddress[] bound;
try
{
    bound = Relay.Resolve(config.Smtp.Host);
}
catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
{
    Log.Fatal("Cannot resolve Smtp:Host {Host}: {Error}", config.Smtp.Host, ex.Message);
    return 1;
}
if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" && bound.All(System.Net.IPAddress.IsLoopback))
{
    Log.Fatal("Smtp:Host {Host} resolves to {Addresses}, the container's loopback interface. Published ports (-p {Port}:{Port}) and other containers arrive on the container's network interface and get connection refused. Set RELAYWAY_SMTP_HOST=0.0.0.0", config.Smtp.Host, string.Join(", ", bound.Select(a => a.ToString())), config.Smtp.Port);
    return 1;
}
Log.Information("Tenant {TenantId}, client {ClientId}, cloud {Cloud}, sending as {SendFrom}", config.Graph.TenantId, config.Graph.ClientId, config.Graph.Cloud, config.SendFrom);

(GraphServiceClient graphClient, Microsoft.Kiota.Abstractions.IRequestAdapter uploadAdapter) = cloud.Connect(config.Graph.TenantId!, config.Graph.ClientId!, config.Graph.ClientSecret!);

try
{
    Microsoft.Graph.Models.User? user = await graphClient.Users[config.SendFrom].GetAsync();
    if (user?.Mail is null && user?.UserPrincipalName is null)
    {
        Log.Fatal("SendFrom {SendFrom} has no mailbox in the tenant", config.SendFrom);
        return 1;
    }
}
catch (Exception ex)
{
    Log.Fatal("Cannot verify SendFrom {SendFrom} with Microsoft Graph: {Error}", config.SendFrom, ex.Message);
    return 1;
}

SmtpServer.SmtpServer server = Relay.CreateServer(config.Smtp, new MessageHandler(graphClient, uploadAdapter, Log.ForContext<MessageHandler>(), config.SendFrom!), Log.ForContext<ClientFilter>());
Log.Information("SMTP server listening on {Addresses} port {Port}, clients {Allowed}, max {Size} MB", string.Join(", ", bound.Select(a => a.ToString())), config.Smtp.Port, config.Smtp.AllowedNetworks.Length == 0 ? "any" : config.Smtp.AllowedNetworks, config.Smtp.MaxMessageSizeMb);

try
{
    await server.StartAsync(CancellationToken.None);
}
catch (System.Net.Sockets.SocketException ex)
{
    Log.Fatal("Cannot listen on {Host}:{Port}: {Error}", config.Smtp.Host, config.Smtp.Port, ex.Message);
    return 1;
}

return 0;

static Serilog.ILogger CreateLogger(LogEventLevel level) => new LoggerConfiguration()
    .MinimumLevel.Is(level)
    .WriteTo.Console(
        theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Literate,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
