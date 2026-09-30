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
    public async Task ImageBadCredentials()
    {
        await RequireDocker();
        (int buildCode, string buildOutput) = await Build.Value;
        Assert.True(buildCode == 0, buildOutput);

        (int code, string output) = await Docker("run", "--rm",
            "-e", "Graph__TenantId=00000000-0000-0000-0000-000000000000",
            "-e", "Graph__ClientId=00000000-0000-0000-0000-000000000000",
            "-e", "Graph__ClientSecret=not-a-secret",
            "-e", "SendFrom=relay@lidlcloud.test",
            Image);

        Assert.Equal(1, code);
        Assert.Contains("Cannot verify SendFrom relay@lidlcloud.test with Microsoft Graph", output);
        Assert.DoesNotContain("not-a-secret", output);
        Assert.DoesNotContain("Unhandled exception", output);
    }

    [Fact]
    public async Task ImageMissingConfig()
    {
        await RequireDocker();
        (int buildCode, string buildOutput) = await Build.Value;
        Assert.True(buildCode == 0, buildOutput);

        (int code, string output) = await Docker("run", "--rm", Image);

        Assert.Equal(1, code);
        Assert.Contains("Missing configuration", output);
    }

    [Fact]
    public async Task ImageLoopbackHost()
    {
        await RequireDocker();
        (int buildCode, string buildOutput) = await Build.Value;
        Assert.True(buildCode == 0, buildOutput);

        (int code, string output) = await Docker("run", "--rm", "--network", "none",
            "-e", "Smtp__Host=localhost",
            "-e", "Graph__TenantId=00000000-0000-0000-0000-000000000000",
            "-e", "Graph__ClientId=00000000-0000-0000-0000-000000000000",
            "-e", "Graph__ClientSecret=not-a-secret",
            "-e", "SendFrom=relay@lidlcloud.test",
            Image);

        Assert.Equal(1, code);
        Assert.Contains("Smtp:Host localhost resolves to", output);
        Assert.Contains("RELAYWAY_SMTP_HOST=0.0.0.0", output);
        Assert.DoesNotContain("Cannot verify SendFrom", output);
    }

    [Fact]
    public async Task SwaksSend()
    {
        await RequireDocker();
        await using RelayHarness relay = new();

        (int code, string output) = await Docker("run", "--rm", "--network", "host", "chko/swaks",
            "--server", "127.0.0.1", "--port", relay.Port.ToString(),
            "--from", "app@legacy.test", "--to", "ann@lidlcloud.test,hidden@lidlcloud.test",
            "--header", "Subject: from swaks", "--header", "To: ann@lidlcloud.test",
            "--attach", "@/etc/hostname");

        Assert.True(code == 0, output);
        JsonElement sent = Assert.Single(relay.Graph.Sent);
        Assert.Equal("from swaks", sent.GetProperty("subject").GetString());
        Assert.Equal("hidden@lidlcloud.test", sent.GetProperty("bccRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal(1, sent.GetProperty("attachments").GetArrayLength());
    }
}
