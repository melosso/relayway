using System.Diagnostics;
using System.Text;
using MailKit.Net.Smtp;
using MimeKit;

namespace Relayway.Tests;

[Trait("Category", "Live")]
public class LiveTests
{
    private static readonly string[] Keys = ["TENANT_ID", "CLIENT_ID", "CLIENT_SECRET", "SEND_FROM", "RECIPIENT"];
    private static string Env(string key) => Environment.GetEnvironmentVariable($"RELAYWAY_LIVE_{key}") ?? "";
    private static string Cloud => Env("CLOUD") is { Length: > 0 } cloud ? cloud : "Global";

    private static void RequireTenant() =>
        Assert.SkipWhen(Keys.Any(k => Env(k).Length == 0), $"set RELAYWAY_LIVE_{string.Join(", RELAYWAY_LIVE_", Keys)}");

    private static MimeMessage Message(string subject, int attachmentSize)
    {
        BodyBuilder body = new() { HtmlBody = $"<p>{subject}</p>" };
        byte[] content = new byte[attachmentSize];
        Random.Shared.NextBytes(content);
        body.Attachments.Add("relayway-test.bin", content);
        MimeMessage message = new() { Subject = $"Relayway live test: {subject} {DateTime.UtcNow:O}", Body = body.ToMessageBody() };
        message.From.Add(MailboxAddress.Parse(Env("SEND_FROM")));
        message.To.Add(MailboxAddress.Parse(Env("RECIPIENT")));
        return message;
    }

    [Theory]
    [InlineData("sendMail", 100 * 1024)]
    [InlineData("draft and upload session", 5 * 1024 * 1024)]
    public async Task LiveSend(string path, int attachmentSize)
    {
        RequireTenant();
        (Microsoft.Graph.GraphServiceClient graph, Microsoft.Kiota.Abstractions.IRequestAdapter uploads) =
            GraphCloud.All[Cloud].Connect(Env("TENANT_ID"), Env("CLIENT_ID"), Env("CLIENT_SECRET"));
        await using RelayHarness relay = new(graph: graph, uploads: uploads, sendFrom: Env("SEND_FROM"));

        await relay.SendAsync(Message(path, attachmentSize), Env("RECIPIENT"));
    }

    private static async Task<string> FollowLogsUntil(string container, string text)
    {
        ProcessStartInfo info = new("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("logs");
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add(container);
        using Process process = Process.Start(info)!;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        StringBuilder seen = new();
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            seen.AppendLine(line);
            if (line.Contains(text))
            {
                process.Kill();
                return seen.ToString();
            }
        }
        return seen.Append(await process.StandardError.ReadToEndAsync(timeout.Token)).ToString();
    }

    [Fact]
    public async Task LiveContainerSend()
    {
        RequireTenant();
        await DockerTests.RequireDocker();
        (int buildCode, string buildOutput) = await DockerTests.Build.Value;
        Assert.True(buildCode == 0, buildOutput);
        int port = RelayHarness.FreePort();
        string name = $"relayway-live-{Guid.NewGuid():N}";

        (int runCode, string runOutput) = await DockerTests.Docker("run", "-d", "--name", name, "-p", $"127.0.0.1:{port}:2525",
            "-e", $"Graph__TenantId={Env("TENANT_ID")}", "-e", $"Graph__ClientId={Env("CLIENT_ID")}",
            "-e", $"Graph__ClientSecret={Env("CLIENT_SECRET")}", "-e", $"Graph__Cloud={Cloud}",
            "-e", $"SendFrom={Env("SEND_FROM")}", DockerTests.Image);
        Assert.True(runCode == 0, runOutput);
        try
        {
            string logs = await FollowLogsUntil(name, "SMTP server listening");
            Assert.Contains("SMTP server listening", logs);
            Assert.DoesNotContain(Env("CLIENT_SECRET"), logs);

            using SmtpClient client = new();
            await client.ConnectAsync("127.0.0.1", port, MailKit.Security.SecureSocketOptions.None, TestContext.Current.CancellationToken);
            await client.SendAsync(Message("docker", 1024), TestContext.Current.CancellationToken);
            await client.DisconnectAsync(true, TestContext.Current.CancellationToken);
        }
        finally
        {
            await DockerTests.Docker("rm", "-f", name);
        }
    }
}
