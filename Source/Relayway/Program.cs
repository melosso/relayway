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

Log.Information("Tenant {TenantId}, client {ClientId}, sending as {SendFrom}", config.Graph.TenantId, config.Graph.ClientId, config.SendFrom);

GraphServiceClient graphClient = new(
    new ClientSecretCredential(config.Graph.TenantId, config.Graph.ClientId, config.Graph.ClientSecret),
    ["https://graph.microsoft.com/.default"]);

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
    server = Relay.CreateServer(config.Smtp, new MessageHandler(graphClient, Log.ForContext<MessageHandler>(), config.SendFrom));
}
catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
{
    Log.Fatal("Cannot resolve Smtp:Host {Host}: {Error}", config.Smtp.Host, ex.Message);
    return 1;
}

System.Net.IPAddress[] bound = System.Net.Dns.GetHostAddresses(config.Smtp.Host);
Log.Information("SMTP server listening on {Addresses} port {Port}", string.Join(", ", bound.Select(a => a.ToString())), config.Smtp.Port);
if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" && bound.All(System.Net.IPAddress.IsLoopback))
{
    Log.Warning("Smtp:Host {Host} resolves to {Addresses}, the container's loopback interface. Published ports (-p {Port}:{Port}) and other containers arrive on the container's network interface and get connection refused. Set Smtp__Host=0.0.0.0 to listen on all container interfaces", config.Smtp.Host, string.Join(", ", bound.Select(a => a.ToString())), config.Smtp.Port);
}

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
