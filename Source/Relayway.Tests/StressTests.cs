using System.Diagnostics;
using System.Net;
using MailKit.Net.Smtp;
using MimeKit;

namespace Relayway.Tests;

[Trait("Category", "Stress")]
public class StressTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ConcurrentThrottled()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("RELAYWAY_STRESS") == "1", "set RELAYWAY_STRESS=1");
        const int clients = 50, perClient = 40;
        byte[] attachment = new byte[256 * 1024];
        Random.Shared.NextBytes(attachment);
        await using RelayHarness relay = new();
        relay.Graph.KeepBodies = false;
        relay.Graph.Status = call => call % 7 == 0 ? HttpStatusCode.TooManyRequests : call % 11 == 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted;
        int retried = 0;
        long before = GC.GetTotalMemory(true);
        Stopwatch clock = Stopwatch.StartNew();

        await Parallel.ForEachAsync(Enumerable.Range(0, clients), async (client, ct) =>
        {
            using SmtpClient smtp = await relay.ConnectAsync();
            for (int i = 0; i < perClient; i++)
            {
                BodyBuilder body = new() { TextBody = "stress" };
                body.Attachments.Add("blob.bin", attachment);
                MimeMessage message = new() { Subject = $"{client}-{i}", Body = body.ToMessageBody() };
                message.From.Add(MailboxAddress.Parse("app@legacy.test"));
                message.To.Add(MailboxAddress.Parse($"user{client}@lidlcloud.test"));
                while (true)
                {
                    try
                    {
                        await smtp.SendAsync(message, ct);
                        break;
                    }
                    catch (SmtpCommandException ex) when ((int)ex.StatusCode == 451)
                    {
                        Interlocked.Increment(ref retried);
                    }
                }
            }
            await smtp.DisconnectAsync(true, ct);
        });

        clock.Stop();
        long after = GC.GetTotalMemory(true);
        string[] subjects = [.. relay.Graph.Subjects];
        output.WriteLine($"{subjects.Length} messages in {clock.Elapsed.TotalSeconds:F1}s ({subjects.Length / clock.Elapsed.TotalSeconds:F0}/s), {relay.Graph.Calls} Graph calls, {retried} client retries after 451, heap delta {(after - before) / 1024 / 1024} MB");
        Assert.Equal(clients * perClient, subjects.Length);
        Assert.Equal(clients * perClient, subjects.Distinct().Count());
    }
}
