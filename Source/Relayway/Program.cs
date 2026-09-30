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

IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

if (!Enum.TryParse(configuration["LogLevel"], ignoreCase: true, out LogEventLevel logLevel))
{
    logLevel = LogEventLevel.Information;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(logLevel)
    .WriteTo.Console(
        theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Literate,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

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

if (config?.Graph is not { TenantId.Length: > 0, ClientId.Length: > 0, ClientSecret.Length: > 0 } || string.IsNullOrWhiteSpace(config.SendFrom))
{
    Log.Fatal("Missing configuration. Graph:TenantId, Graph:ClientId, Graph:ClientSecret and SendFrom are required, see README");
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
    Log.Fatal("Smtp:Host {Host} resolves to {Addresses}, the container's loopback interface. Published ports (-p {Port}:{Port}) and other containers arrive on the container's network interface and get connection refused. Remove Smtp__Host or set Smtp__Host=0.0.0.0", config.Smtp.Host, string.Join(", ", bound.Select(a => a.ToString())), config.Smtp.Port);
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

SmtpServer.SmtpServer server;
try
{
    server = Relay.CreateServer(config.Smtp, new MessageHandler(graphClient, uploadAdapter, Log.ForContext<MessageHandler>(), config.SendFrom), Log.ForContext<ClientFilter>());
}
catch (FormatException ex)
{
    Log.Fatal(ex.Message);
    return 1;
}
catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
{
    Log.Fatal("Cannot resolve Smtp:Host {Host}: {Error}", config.Smtp.Host, ex.Message);
    return 1;
}

Log.Information("SMTP server listening on {Addresses} port {Port}, clients {Allowed}, max {Size} MB", string.Join(", ", bound.Select(a => a.ToString())), config.Smtp.Port, config.Smtp.AllowedNetworks.Length == 0 ? "any" : string.Join(", ", config.Smtp.AllowedNetworks), config.Smtp.MaxMessageSizeMb);

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
