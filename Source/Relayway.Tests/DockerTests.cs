using System.Diagnostics;
using System.Text.Json;

namespace Relayway.Tests;

public class DockerTests
{
    internal const string Image = "relayway:test";
    internal static readonly Lazy<Task<(int Code, string Output)>> Build = new(() => Docker("build", "-t", Image, RepoRoot()));

    private static string RepoRoot()
    {
        DirectoryInfo dir = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "Dockerfile")))
        {
            dir = dir.Parent!;
        }
        return dir.FullName;
    }

    internal static async Task<(int Code, string Output)> Docker(params string[] args)
    {
        ProcessStartInfo info = new("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }

    internal static async Task RequireDocker()
    {
        try
        {
            Assert.SkipWhen((await Docker("info")).Code != 0, "docker daemon not available");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("docker not installed");
        }
    }

    [Fact]
    public async Task Image_rejects_bad_credentials_against_real_entra_id_with_one_line()
    {
        await RequireDocker();
        (int buildCode, string buildOutput) = await Build.Value;
        Assert.True(buildCode == 0, buildOutput);

        (int code, string output) = await Docker("run", "--rm",
            "-e", "Graph__TenantId=00000000-0000-0000-0000-000000000000",
            "-e", "Graph__ClientId=00000000-0000-0000-0000-000000000000",
            "-e", "Graph__ClientSecret=not-a-secret",
            "-e", "SendFrom=relay@contoso.test",
            Image);

        Assert.Equal(1, code);
        Assert.Contains("Cannot verify SendFrom relay@contoso.test with Microsoft Graph", output);
        Assert.DoesNotContain("not-a-secret", output);
        Assert.DoesNotContain("Unhandled exception", output);
    }

    [Fact]
    public async Task Image_rejects_missing_configuration()
    {
        await RequireDocker();
        (int buildCode, string buildOutput) = await Build.Value;
        Assert.True(buildCode == 0, buildOutput);

        (int code, string output) = await Docker("run", "--rm", Image);

        Assert.Equal(1, code);
        Assert.Contains("Missing configuration", output);
    }

    [Fact]
    public async Task Image_refuses_loopback_host_before_contacting_graph()
    {
        await RequireDocker();
        (int buildCode, string buildOutput) = await Build.Value;
        Assert.True(buildCode == 0, buildOutput);

        (int code, string output) = await Docker("run", "--rm", "--network", "none",
            "-e", "Smtp__Host=localhost",
            "-e", "Graph__TenantId=00000000-0000-0000-0000-000000000000",
            "-e", "Graph__ClientId=00000000-0000-0000-0000-000000000000",
            "-e", "Graph__ClientSecret=not-a-secret",
            "-e", "SendFrom=relay@contoso.test",
            Image);

        Assert.Equal(1, code);
        Assert.Contains("Smtp:Host localhost resolves to", output);
        Assert.Contains("Smtp__Host=0.0.0.0", output);
        Assert.DoesNotContain("Cannot verify SendFrom", output);
    }

    [Fact]
    public async Task Swaks_client_in_docker_delivers_through_relay()
    {
        await RequireDocker();
        await using RelayHarness relay = new();

        (int code, string output) = await Docker("run", "--rm", "--network", "host", "chko/swaks",
            "--server", "127.0.0.1", "--port", relay.Port.ToString(),
            "--from", "app@legacy.test", "--to", "ann@contoso.test,hidden@contoso.test",
            "--header", "Subject: from swaks", "--header", "To: ann@contoso.test",
            "--attach", "@/etc/hostname");

        Assert.True(code == 0, output);
        JsonElement sent = Assert.Single(relay.Graph.Sent);
        Assert.Equal("from swaks", sent.GetProperty("subject").GetString());
        Assert.Equal("hidden@contoso.test", sent.GetProperty("bccRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal(1, sent.GetProperty("attachments").GetArrayLength());
    }
}
