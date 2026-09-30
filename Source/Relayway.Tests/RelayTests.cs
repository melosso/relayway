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
        message.To.Add(new MailboxAddress("Ann", "ann@contoso.test"));
        message.To.Add(MailboxAddress.Parse("not-in-envelope@contoso.test"));
        message.Cc.Add(MailboxAddress.Parse("cc@contoso.test"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "body" };
        return message;
    }

    private static string[] Addresses(List<Recipient>? recipients) =>
        [.. recipients!.Select(r => r.EmailAddress!.Address!)];

    [Fact]
    public void Envelope_decides_recipients_and_headers_decide_to_cc_bcc()
    {
        Message graph = MessageHandler.ToGraphMessage(Message(), ["ANN@contoso.test", "cc@contoso.test", "hidden@contoso.test"]);

        Assert.Equal(["ann@contoso.test"], Addresses(graph.ToRecipients));
        Assert.Equal("Ann", graph.ToRecipients![0].EmailAddress!.Name);
        Assert.Equal(["cc@contoso.test"], Addresses(graph.CcRecipients));
        Assert.Equal(["hidden@contoso.test"], Addresses(graph.BccRecipients));
    }

    [Fact]
    public void Html_inline_images_unnamed_attachments_and_eml_are_kept()
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

        Message graph = MessageHandler.ToGraphMessage(message, ["ann@contoso.test"]);

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
    public void Text_attachment_is_kept_and_text_alternative_is_not()
    {
        BodyBuilder body = new() { TextBody = "plain", HtmlBody = "<b>html</b>" };
        body.Attachments.Add("report.csv", [65, 66], ContentType.Parse("text/csv"));
        MimeMessage message = Message();
        message.Body = body.ToMessageBody();

        Message graph = MessageHandler.ToGraphMessage(message, ["ann@contoso.test"]);

        FileAttachment csv = Assert.IsType<FileAttachment>(Assert.Single(graph.Attachments!));
        Assert.Equal("report.csv", csv.Name);
    }

    [Fact]
    public async Task Smtp_message_is_sent_through_graph()
    {
        await using RelayHarness relay = new();

        await relay.SendAsync(Message("over smtp"), "ann@contoso.test", "hidden@contoso.test");

        JsonElement sent = Assert.Single(relay.Graph.Sent);
        Assert.Equal("over smtp", sent.GetProperty("subject").GetString());
        Assert.Equal("hidden@contoso.test", sent.GetProperty("bccRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
    }

    [Fact]
    public async Task Graph_throttling_is_retried_then_accepted()
    {
        await using RelayHarness relay = new();
        relay.Graph.Status = call => call == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Accepted;

        await relay.SendAsync(Message(), "ann@contoso.test");

        Assert.Equal(2, relay.Graph.Calls);
        Assert.Single(relay.Graph.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 451)]
    [InlineData(HttpStatusCode.TooManyRequests, 451)]
    [InlineData(HttpStatusCode.Unauthorized, 554)]
    [InlineData(HttpStatusCode.BadRequest, 554)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, 554)]
    public async Task Graph_failures_map_to_smtp_codes(HttpStatusCode graphStatus, int smtpCode)
    {
        await using RelayHarness relay = new();
        relay.Graph.Status = _ => graphStatus;

        SmtpCommandException ex = await Assert.ThrowsAsync<SmtpCommandException>(() => relay.SendAsync(Message(), "ann@contoso.test"));

        Assert.Equal(smtpCode, (int)ex.StatusCode);
        Assert.Empty(relay.Graph.Sent);
    }

    [Fact]
    public async Task Oversized_message_is_refused_before_graph()
    {
        await using RelayHarness relay = new(maxMessageSizeMb: 1);
        MimeMessage message = WithAttachments(("big.bin", 1024 * 1024));

        SmtpCommandException ex = await Assert.ThrowsAsync<SmtpCommandException>(() => relay.SendAsync(message, "ann@contoso.test"));

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
    public async Task Large_message_is_sent_as_draft_with_upload_session()
    {
        await using RelayHarness relay = new();
        MimeMessage message = WithAttachments(("large.bin", 5 * 1024 * 1024), ("small.txt", 1000));

        await relay.SendAsync(message, "ann@contoso.test", "hidden@contoso.test");

        Assert.Empty(relay.Graph.Sent);
        JsonElement draft = Assert.Single(relay.Graph.Drafts);
        Assert.Equal("large", draft.GetProperty("subject").GetString());
        Assert.Equal(0, draft.GetProperty("attachments").GetArrayLength());
        Assert.Equal("hidden@contoso.test", draft.GetProperty("bccRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal(Content(message, "large.bin"), relay.Graph.DraftAttachments["large.bin"]);
        Assert.Equal(Content(message, "small.txt"), relay.Graph.DraftAttachments["small.txt"]);
        Assert.Equal(["draft-1"], relay.Graph.DraftsSent);
        Assert.Empty(relay.Graph.DraftsDeleted);
    }

    [Fact]
    public async Task Message_under_4_mb_uses_sendmail_without_mail_readwrite()
    {
        await using RelayHarness relay = new();

        await relay.SendAsync(WithAttachments(("mid.bin", 2_500_000)), "ann@contoso.test");

        Assert.Single(relay.Graph.Sent);
        Assert.Empty(relay.Graph.Drafts);
    }

    [Fact]
    public async Task Sendmail_413_falls_back_to_draft()
    {
        await using RelayHarness relay = new();
        relay.Graph.Status = call => call == 1 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.OK;

        await relay.SendAsync(WithAttachments(("mid.bin", 2_500_000)), "ann@contoso.test");

        Assert.Empty(relay.Graph.Sent);
        JsonElement draft = Assert.Single(relay.Graph.Drafts);
        Assert.Equal("large", draft.GetProperty("subject").GetString());
        Assert.Equal("ann@contoso.test", draft.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal(["draft-1"], relay.Graph.DraftsSent);
        Assert.Equal(["mid.bin"], relay.Graph.DraftAttachments.Keys);
    }

    [Fact]
    public async Task Failed_upload_deletes_draft_and_asks_client_to_retry()
    {
        await using RelayHarness relay = new();
        relay.Graph.Status = call => call is >= 2 and <= 5 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;

        SmtpCommandException ex = await Assert.ThrowsAsync<SmtpCommandException>(() => relay.SendAsync(WithAttachments(("large.bin", 4 * 1024 * 1024)), "ann@contoso.test"));

        Assert.Equal(451, (int)ex.StatusCode);
        Assert.Equal(["draft-1"], relay.Graph.DraftsDeleted);
        Assert.Empty(relay.Graph.DraftsSent);
    }

    [Fact]
    public async Task Large_message_without_mail_readwrite_is_refused()
    {
        await using RelayHarness relay = new();
        relay.Graph.Status = _ => HttpStatusCode.Forbidden;

        SmtpCommandException ex = await Assert.ThrowsAsync<SmtpCommandException>(() => relay.SendAsync(WithAttachments(("large.bin", 4 * 1024 * 1024)), "ann@contoso.test"));

        Assert.Equal(554, (int)ex.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.0/8", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.0/8", false)]
    public async Task Allowed_networks_decide_which_clients_may_send(string network, bool accepted)
    {
        await using RelayHarness relay = new(allowedNetworks: [network]);

        Task send = relay.SendAsync(Message(), "ann@contoso.test");

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
    public void Invalid_allowed_network_names_the_entry(string entry)
    {
        FormatException ex = Assert.Throws<FormatException>(() => Relay.ParseNetworks(["10.0.0.0/8", entry]));

        Assert.Contains($"'{entry}'", ex.Message);
    }

    [Fact]
    public void Allowed_networks_accept_addresses_and_ipv6()
    {
        IPNetwork[] networks = Relay.ParseNetworks([" 10.0.0.5 ", "fd00::/8", "::1", "192.168.1.5/24"]);

        Assert.Equal(["10.0.0.5/32", "fd00::/8", "::1/128"], networks[..3].Select(n => n.ToString()));
        Assert.True(networks[3].Contains(IPAddress.Parse("192.168.1.77")));
        Assert.False(networks[3].Contains(IPAddress.Parse("192.168.2.1")));
    }

    [Fact]
    public async Task Unspecified_address_listens()
    {
        await using RelayHarness relay = new("0.0.0.0");

        await relay.SendAsync(Message(), "ann@contoso.test");

        Assert.Single(relay.Graph.Sent);
    }

    [Fact]
    public async Task Localhost_does_not_listen_on_other_interfaces()
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
