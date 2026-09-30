using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Graph.Models;
using MimeKit;
using MimeKit.Utils;
using ContentType = MimeKit.ContentType;
using MimeContent = MimeKit.MimeContent;

namespace Relayway.Tests;

public class RelayTests
{
    private static MimeMessage Message(string subject = "hello")
    {
        MimeMessage message = new();
        message.From.Add(MailboxAddress.Parse("app@legacy.test"));
        message.To.Add(new MailboxAddress("Ann", "ann@lidlcloud.test"));
        message.To.Add(MailboxAddress.Parse("not-in-envelope@lidlcloud.test"));
        message.Cc.Add(MailboxAddress.Parse("cc@lidlcloud.test"));
        message.ReplyTo.Add(new MailboxAddress("Desk", "desk@lidlcloud.test"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "body" };
        return message;
    }

    private static string[] Addresses(List<Recipient>? recipients) =>
        [.. recipients!.Select(r => r.EmailAddress!.Address!)];

    [Fact]
    public void EnvelopeRecipients()
    {
        Message graph = MessageHandler.ToGraphMessage(Message(), ["ANN@lidlcloud.test", "cc@lidlcloud.test", "hidden@lidlcloud.test"]);

        Assert.Equal(["ann@lidlcloud.test"], Addresses(graph.ToRecipients));
        Assert.Equal("Ann", graph.ToRecipients![0].EmailAddress!.Name);
        Assert.Equal(["cc@lidlcloud.test"], Addresses(graph.CcRecipients));
        Assert.Equal(["hidden@lidlcloud.test"], Addresses(graph.BccRecipients));
        Assert.Equal(["desk@lidlcloud.test"], Addresses(graph.ReplyTo));
    }

    [Fact]
    public void AttachmentsKept()
    {
        BodyBuilder body = new() { TextBody = "plain" };
        MimeEntity image = body.LinkedResources.Add("logo.png", [1, 2, 3], ContentType.Parse("image/png"));
        image.ContentId = MimeUtils.GenerateMessageId();
        body.HtmlBody = $"<img src=\"cid:{image.ContentId}\">";
        body.Attachments.Add(new MimePart("application", "pdf") { Content = new MimeContent(new MemoryStream([9, 9])), ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) });
        MimeMessage inner = Message("forwarded");
        inner.Body = new Multipart("mixed") { new TextPart("plain") { Text = "inner" }, new MimePart("text", "csv") { FileName = "inner.csv", Content = new MimeContent(new MemoryStream([1])) } };
        body.Attachments.Add(new MessagePart { Message = inner });
        MimeMessage message = Message();
        message.Body = body.ToMessageBody();

        Message graph = MessageHandler.ToGraphMessage(message, ["ann@lidlcloud.test"]);

        Assert.Equal(BodyType.Html, graph.Body!.ContentType);
        FileAttachment[] files = [.. graph.Attachments!.Cast<FileAttachment>()];
        Assert.Equal(3, files.Length);
        FileAttachment logo = Assert.Single(files, f => f.Name == "logo.png");
        Assert.True(logo.IsInline);
        Assert.Equal(image.ContentId, logo.ContentId);
        Assert.Equal([1, 2, 3], logo.ContentBytes);
        FileAttachment pdf = Assert.Single(files, f => f.ContentType == "application/pdf");
        Assert.Equal("attachment", pdf.Name);
        Assert.False(pdf.IsInline);
        FileAttachment eml = Assert.Single(files, f => f.ContentType == "message/rfc822");
        Assert.Equal("forwarded.eml", eml.Name);
    }

    [Fact]
    public void TextAttachmentKept()
    {
        BodyBuilder body = new() { TextBody = "plain", HtmlBody = "<b>html</b>" };
        body.Attachments.Add("report.csv", [65, 66], ContentType.Parse("text/csv"));
        MimeMessage message = Message();
        message.Body = body.ToMessageBody();

        Message graph = MessageHandler.ToGraphMessage(message, ["ann@lidlcloud.test"]);

        FileAttachment csv = Assert.IsType<FileAttachment>(Assert.Single(graph.Attachments!));
        Assert.Equal("report.csv", csv.Name);
    }

    [Fact]
    public async Task SendMail()
    {
        await using RelayHarness relay = new();

        await relay.SendAsync(Message("over smtp"), "ann@lidlcloud.test", "hidden@lidlcloud.test");

        JsonElement sent = Assert.Single(relay.Graph.Sent);
        Assert.Equal("over smtp", sent.GetProperty("subject").GetString());
        Assert.Equal("hidden@lidlcloud.test", sent.GetProperty("bccRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
    }

    [Fact]
    public async Task ThrottlingRetried()
    {
        await using RelayHarness relay = new();
        relay.Graph.Status = call => call == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Accepted;

        await relay.SendAsync(Message(), "ann@lidlcloud.test");

        Assert.Equal(2, relay.Graph.Calls);
        Assert.Single(relay.Graph.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 451)]
    [InlineData(HttpStatusCode.TooManyRequests, 451)]
    [InlineData(HttpStatusCode.Unauthorized, 554)]
    [InlineData(HttpStatusCode.BadRequest, 554)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, 554)]
    public async Task GraphErrorCodes(HttpStatusCode graphStatus, int smtpCode)
    {
        await using RelayHarness relay = new();
        relay.Graph.Status = _ => graphStatus;

        SmtpCommandException ex = await Assert.ThrowsAsync<SmtpCommandException>(() => relay.SendAsync(Message(), "ann@lidlcloud.test"));

        Assert.Equal(smtpCode, (int)ex.StatusCode);
        Assert.Empty(relay.Graph.Sent);
    }

    [Fact]
    public async Task OversizeRefused()
    {
        await using RelayHarness relay = new(maxMessageSizeMb: 1);
        MimeMessage message = WithAttachments(("big.bin", 1024 * 1024));

        SmtpCommandException ex = await Assert.ThrowsAsync<SmtpCommandException>(() => relay.SendAsync(message, "ann@lidlcloud.test"));

        Assert.Equal(552, (int)ex.StatusCode);
        Assert.Equal(0, relay.Graph.Calls);
    }

    private static MimeMessage WithAttachments(params (string Name, int Size)[] files)
    {
        BodyBuilder body = new() { TextBody = "files" };
        foreach ((string name, int size) in files)
        {
            byte[] content = new byte[size];
            Random.Shared.NextBytes(content);
            body.Attachments.Add(name, content);
        }
        MimeMessage message = Message("large");
        message.Body = body.ToMessageBody();
        return message;
    }

    private static byte[] Content(MimeMessage message, string name)
    {
        using MemoryStream stream = new();
        message.Attachments.OfType<MimePart>().Single(p => p.FileName == name).Content!.DecodeTo(stream);
        return stream.ToArray();
    }

    [Fact]
    public async Task LargeMessageDraft()
    {
        await using RelayHarness relay = new();
        MimeMessage message = WithAttachments(("large.bin", 5 * 1024 * 1024), ("small.txt", 1000));

        await relay.SendAsync(message, "ann@lidlcloud.test", "hidden@lidlcloud.test");

        Assert.Empty(relay.Graph.Sent);
        JsonElement draft = Assert.Single(relay.Graph.Drafts);
        Assert.Equal("large", draft.GetProperty("subject").GetString());
        Assert.Equal(0, draft.GetProperty("attachments").GetArrayLength());
        Assert.Equal("hidden@lidlcloud.test", draft.GetProperty("bccRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal(Content(message, "large.bin"), relay.Graph.DraftAttachments["large.bin"]);
        Assert.Equal(Content(message, "small.txt"), relay.Graph.DraftAttachments["small.txt"]);
        Assert.Equal(["draft-1"], relay.Graph.DraftsSent);
        Assert.Empty(relay.Graph.DraftsDeleted);
    }

    [Fact]
    public async Task MidSizeSendMail()
    {
        await using RelayHarness relay = new();

        await relay.SendAsync(WithAttachments(("mid.bin", 2_500_000)), "ann@lidlcloud.test");

        Assert.Single(relay.Graph.Sent);
        Assert.Empty(relay.Graph.Drafts);
    }

    [Fact]
    public async Task SendMail413Draft()
    {
        await using RelayHarness relay = new();
        relay.Graph.Status = call => call == 1 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.OK;

        await relay.SendAsync(WithAttachments(("mid.bin", 2_500_000)), "ann@lidlcloud.test");

        Assert.Empty(relay.Graph.Sent);
        JsonElement draft = Assert.Single(relay.Graph.Drafts);
        Assert.Equal("large", draft.GetProperty("subject").GetString());
        Assert.Equal("ann@lidlcloud.test", draft.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal(["draft-1"], relay.Graph.DraftsSent);
        Assert.Equal(["mid.bin"], relay.Graph.DraftAttachments.Keys);
    }

    [Fact]
    public async Task UploadFailureDeletesDraft()
    {
        await using RelayHarness relay = new();
        relay.Graph.Status = call => call is >= 2 and <= 5 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;

        SmtpCommandException ex = await Assert.ThrowsAsync<SmtpCommandException>(() => relay.SendAsync(WithAttachments(("large.bin", 4 * 1024 * 1024)), "ann@lidlcloud.test"));

        Assert.Equal(451, (int)ex.StatusCode);
        Assert.Equal(["draft-1"], relay.Graph.DraftsDeleted);
        Assert.Empty(relay.Graph.DraftsSent);
    }

    [Fact]
    public async Task UploadIncompleteDeletesDraft()
    {
        await using RelayHarness relay = new();
        relay.Graph.OmitUploadLocation = true;

        SmtpCommandException ex = await Assert.ThrowsAsync<SmtpCommandException>(() => relay.SendAsync(WithAttachments(("large.bin", 4 * 1024 * 1024)), "ann@lidlcloud.test"));

        Assert.Equal(451, (int)ex.StatusCode);
        Assert.Equal(["draft-1"], relay.Graph.DraftsDeleted);
        Assert.Empty(relay.Graph.DraftsSent);
    }

    [Fact]
    public async Task DraftCleanupFailure()
    {
        await using RelayHarness relay = new();
        relay.Graph.Status = call => call switch { 1 => HttpStatusCode.OK, 2 => HttpStatusCode.BadRequest, _ => HttpStatusCode.InternalServerError };

        SmtpCommandException ex = await Assert.ThrowsAsync<SmtpCommandException>(() => relay.SendAsync(WithAttachments(("large.bin", 4 * 1024 * 1024)), "ann@lidlcloud.test"));

        Assert.Equal(554, (int)ex.StatusCode);
        Assert.Empty(relay.Graph.DraftsDeleted);
    }

    [Fact]
    public async Task UnparseableRefused()
    {
        await using RelayHarness relay = new();
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, relay.Port, TestContext.Current.CancellationToken);
        using StreamReader reader = new(tcp.GetStream());
        using StreamWriter writer = new(tcp.GetStream()) { NewLine = "\r\n", AutoFlush = true };

        async Task<string> Command(string line)
        {
            await writer.WriteLineAsync(line);
            string reply;
            do
            {
                reply = (await reader.ReadLineAsync(TestContext.Current.CancellationToken))!;
            }
            while (reply[3] == '-');
            return reply;
        }

        await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        await Command("EHLO test");
        await Command("MAIL FROM:<app@legacy.test>");
        await Command("RCPT TO:<ann@lidlcloud.test>");
        await Command("DATA");
        await writer.WriteLineAsync("x");
        string reply = await Command(".");

        Assert.StartsWith("554", reply);
        Assert.Equal(0, relay.Graph.Calls);
    }

    [Fact]
    public async Task DraftForbidden()
    {
        await using RelayHarness relay = new();
        relay.Graph.Status = _ => HttpStatusCode.Forbidden;

        SmtpCommandException ex = await Assert.ThrowsAsync<SmtpCommandException>(() => relay.SendAsync(WithAttachments(("large.bin", 4 * 1024 * 1024)), "ann@lidlcloud.test"));

        Assert.Equal(554, (int)ex.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.0/8", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.0/8", false)]
    public async Task AllowedNetworks(string network, bool accepted)
    {
        await using RelayHarness relay = new(allowedNetworks: network);

        Task send = relay.SendAsync(Message(), "ann@lidlcloud.test");

        if (accepted)
        {
            await send;
            Assert.Single(relay.Graph.Sent);
        }
        else
        {
            SmtpCommandException ex = await Assert.ThrowsAsync<SmtpCommandException>(() => send);
            Assert.Equal(550, (int)ex.StatusCode);
            Assert.Equal(0, relay.Graph.Calls);
        }
    }

    [Theory]
    [InlineData("300.1.1.1")]
    [InlineData("lan")]
    [InlineData("10.0.0.0/33")]
    public void InvalidNetwork(string entry)
    {
        FormatException ex = Assert.Throws<FormatException>(() => Relay.ParseNetworks($"10.0.0.0/8,{entry}"));

        Assert.Contains($"'{entry}'", ex.Message);
    }

    [Theory]
    [InlineData("Global", "https://login.microsoftonline.com/", "graph.microsoft.com")]
    [InlineData("usgovernment", "https://login.microsoftonline.us/", "graph.microsoft.us")]
    [InlineData("USGovernmentDoD", "https://login.microsoftonline.us/", "dod-graph.microsoft.us")]
    [InlineData("China", "https://login.chinacloudapi.cn/", "microsoftgraph.chinacloudapi.cn")]
    public void CloudEndpoints(string name, string authority, string graphHost)
    {
        GraphCloud cloud = GraphCloud.All[name];

        (Microsoft.Graph.GraphServiceClient graph, _) = cloud.Connect("tenant", "client", "secret");

        Assert.Equal(authority, cloud.AuthorityHost.ToString());
        Assert.Equal($"https://{graphHost}/v1.0", graph.RequestAdapter.BaseUrl);
    }

    [Fact]
    public void NetworkFormats()
    {
        IPNetwork[] networks = Relay.ParseNetworks(" 10.0.0.5 , fd00::/8,::1,192.168.1.5/24");

        Assert.Equal(["10.0.0.5/32", "fd00::/8", "::1/128"], networks[..3].Select(n => n.ToString()));
        Assert.True(networks[3].Contains(IPAddress.Parse("192.168.1.77")));
        Assert.False(networks[3].Contains(IPAddress.Parse("192.168.2.1")));
    }

    [Fact]
    public async Task UnspecifiedAddress()
    {
        await using RelayHarness relay = new("0.0.0.0");

        await relay.SendAsync(Message(), "ann@lidlcloud.test");

        Assert.Single(relay.Graph.Sent);
    }

    [Fact]
    public async Task LocalhostOnly()
    {
        IPAddress? lan = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        Assert.SkipWhen(lan is null, "no non-loopback IPv4 interface");
        await using RelayHarness relay = new("localhost");
        using (SmtpClient ok = await relay.ConnectAsync())
        {
            Assert.True(ok.IsConnected);
        }

        using TcpClient tcp = new();
        await Assert.ThrowsAnyAsync<SocketException>(() => tcp.ConnectAsync(lan!, relay.Port, TestContext.Current.CancellationToken).AsTask());
    }
}
