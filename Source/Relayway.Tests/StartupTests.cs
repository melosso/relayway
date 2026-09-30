using System.Diagnostics;

namespace Relayway.Tests;

public class StartupTests
{
    private const string Valid = "Graph__TenantId=t;Graph__ClientId=c;Graph__ClientSecret=not-a-secret;SendFrom=relay@lidlcloud.test";

    private static async Task<(int Code, string Output)> Run(string environment)
    {
        ProcessStartInfo info = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Relayway.dll"));
        foreach (string key in info.Environment.Keys.Where(k => k.StartsWith("Graph__") || k.StartsWith("Smtp__") || k.StartsWith("RELAYWAY_") || k is "SendFrom" or "LogLevel" or "DOTNET_RUNNING_IN_CONTAINER").ToList())
        {
            info.Environment.Remove(key);
        }
        foreach (string pair in environment.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            info.Environment[parts[0]] = parts[1];
        }
        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }

    [Theory]
    [InlineData("", "Missing configuration: Graph:TenantId, Graph:ClientId, Graph:ClientSecret, SendFrom")]
    [InlineData(Valid + ";Graph__ClientSecret=", "Missing configuration: Graph:ClientSecret")]
    [InlineData(Valid + ";Graph__Cloud=Mars", "Graph:Cloud Mars is not one of Global, USGovernment, USGovernmentDoD, China")]
    [InlineData(Valid + ";Smtp__MaxMessageSizeMb=0", "Smtp:MaxMessageSizeMb 0 is outside 1 to 150")]
    [InlineData(Valid + ";Smtp__MaxMessageSizeMb=151", "Smtp:MaxMessageSizeMb 151 is outside 1 to 150")]
    [InlineData(Valid + ";Smtp__Port=abc", "Invalid configuration")]
    [InlineData(Valid + ";RELAYWAY_ENV_FILE=/nonexistent/.env", "RELAYWAY_ENV_FILE points to /nonexistent/.env, which does not exist")]
    [InlineData(Valid + ";RELAYWAY_CLIENT_SECRET_FILE=/nonexistent/secret", "RELAYWAY_CLIENT_SECRET_FILE: RELAYWAY_CLIENT_SECRET_FILE points to /nonexistent/secret, which does not exist")]
    [InlineData("RELAYWAY_TENANT_ID=t;RELAYWAY_CLIENT_ID=c", "Missing configuration: Graph:ClientSecret, SendFrom")]
    [InlineData(Valid + ";RELAYWAY_ALLOWED_NETWORKS=10.0.0.0/8, lan", "Smtp:AllowedNetworks entry 'lan' is not an IP address or CIDR network")]
    [InlineData(Valid + ";Smtp__Host=relayway.invalid", "Cannot resolve Smtp:Host relayway.invalid")]
    [InlineData(Valid + ";DOTNET_RUNNING_IN_CONTAINER=true;Smtp__Host=localhost", "Smtp:Host localhost resolves to")]
    public async Task InvalidConfigExits(string environment, string expected)
    {
        (int code, string output) = await Run(environment);

        Assert.Equal(1, code);
        Assert.Contains(expected, output);
        Assert.DoesNotContain("Unhandled exception", output);
        Assert.DoesNotContain("not-a-secret", output);
        Assert.DoesNotContain("Cannot verify SendFrom", output);
    }
}
