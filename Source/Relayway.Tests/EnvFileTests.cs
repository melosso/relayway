using System.Collections;
using Microsoft.Extensions.Configuration;

namespace Relayway.Tests;

public sealed class EnvFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("relayway-env-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private (IConfiguration Configuration, List<string> Problems) Build(Hashtable environment)
    {
        List<string> problems = [];
        IConfiguration configuration = new ConfigurationBuilder().AddRelaywaySources(_directory, environment, problems).Build();
        return (configuration, problems);
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("json", "", "", "")]
    [InlineData("dotenv", "RELAYWAY_SEND_FROM=dotenv", "", "")]
    [InlineData("alias", "RELAYWAY_SEND_FROM=dotenv", "alias", "")]
    [InlineData("section", "RELAYWAY_SEND_FROM=dotenv", "alias", "section")]
    public void Precedence(string expected, string dotenv, string alias, string section)
    {
        Write("appsettings.json", """{"SendFrom":"json"}""");
        Write(".env", dotenv);
        Hashtable environment = new();
        if (alias.Length > 0)
        {
            environment["RELAYWAY_SEND_FROM"] = alias;
        }
        if (section.Length > 0)
        {
            environment["SendFrom"] = section;
        }

        Assert.Equal(expected, Build(environment).Configuration["SendFrom"]);
    }

    [Fact]
    public void DotEnvSyntax()
    {
        Write(".env", """
            # comment
            export RELAYWAY_TENANT_ID=tenant
            RELAYWAY_CLIENT_ID="quoted # kept"
            RELAYWAY_SMTP_HOST=0.0.0.0 # trailing comment
            Smtp__Port='2626'
            LogLevel=Debug
            not a setting
            RELAYWAY_TYPO=x
            RELAYWAY_CLOUD=China
            RELAYWAY_CLOUD=Global
            """);

        (IConfiguration configuration, List<string> problems) = Build([]);

        Assert.Equal("tenant", configuration["Graph:TenantId"]);
        Assert.Equal("quoted # kept", configuration["Graph:ClientId"]);
        Assert.Equal("0.0.0.0", configuration["Smtp:Host"]);
        Assert.Equal("2626", configuration["Smtp:Port"]);
        Assert.Equal("Debug", configuration["LogLevel"]);
        Assert.Equal("Global", configuration["Graph:Cloud"]);
        Assert.Equal(
            [".env line 7: not NAME=value", ".env: RELAYWAY_CLOUD is set on lines 9, 10; line 10 wins", ".env line 8: unknown name RELAYWAY_TYPO"],
            problems);
    }

    [Fact]
    public void SecretFromFile()
    {
        string secret = Write("secret", "s3cret\n");

        (IConfiguration configuration, List<string> problems) = Build(new Hashtable { ["RELAYWAY_CLIENT_SECRET_FILE"] = secret });

        Assert.Equal("s3cret", configuration["Graph:ClientSecret"]);
        Assert.Empty(problems);
    }

    [Fact]
    public void ExplicitEnvFile()
    {
        string path = Write("relayway.env", "RELAYWAY_SEND_FROM=explicit");

        Assert.Equal("explicit", Build(new Hashtable { ["RELAYWAY_ENV_FILE"] = path }).Configuration["SendFrom"]);
    }

    [Fact]
    public void AliasConflictWarns()
    {
        (IConfiguration configuration, List<string> problems) = Build(new Hashtable { ["RELAYWAY_SMTP_HOST"] = "0.0.0.0", ["Smtp__Host"] = "localhost" });

        Assert.Equal("localhost", configuration["Smtp:Host"]);
        Assert.Equal(["Smtp__Host overrides RELAYWAY_SMTP_HOST"], problems);
    }

    [Fact]
    public void AllowedNetworksList()
    {
        Write(".env", "RELAYWAY_ALLOWED_NETWORKS=192.168.1.0/24, 10.0.0.0/8, 127.0.0.1");

        string list = Build([]).Configuration["Smtp:AllowedNetworks"]!;

        Assert.Equal(3, Relay.ParseNetworks(list).Length);
    }

    [Fact]
    public void ProblemsNeverContainValues()
    {
        Write(".env", "RELAYWAY_CLIENT_SECRET=first-secret\nRELAYWAY_CLIENT_SECRET=second-secret\n=broken-secret");

        (_, List<string> problems) = Build(new Hashtable { ["RELAYWAY_CLIENT_SECRET"] = "env-secret", ["Graph__ClientSecret"] = "section-secret" });

        Assert.NotEmpty(problems);
        Assert.DoesNotContain(problems, p => p.Contains("secret", StringComparison.Ordinal) && !p.Contains("CLIENT_SECRET", StringComparison.Ordinal) && !p.Contains("ClientSecret", StringComparison.Ordinal));
    }
}
