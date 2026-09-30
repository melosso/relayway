using System.Buffers;
using System.Net;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Graph.Users.Item.SendMail;
using MimeKit;
using Serilog;
using SmtpServer;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Relayway;

public static class Relay
{
    // ponytail: graph sendMail caps the request at 4 MB; larger mail needs a draft plus upload session
    public const int MaxMessageSize = 4 * 1024 * 1024;

    public static SmtpServer.SmtpServer CreateServer(SmtpConfiguration smtp, MessageStore store)
    {
        SmtpServerOptionsBuilder builder = new SmtpServerOptionsBuilder()
            .ServerName(smtp.Host)
            .MaxMessageSize(MaxMessageSize, MaxMessageSizeHandling.Strict);
        foreach (IPAddress address in Dns.GetHostAddresses(smtp.Host))
        {
            builder.Endpoint(e => e.Endpoint(new IPEndPoint(address, smtp.Port)));
        }

        SmtpServer.ComponentModel.ServiceProvider services = new();
        services.Add(store);
        return new SmtpServer.SmtpServer(builder.Build(), services);
    }
}

public class MessageHandler(GraphServiceClient graphClient, ILogger logger, string sendFrom) : MessageStore
{
    public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
    {
        MimeMessage message;
        try
        {
            using MemoryStream stream = new(buffer.ToArray());
            message = await MimeMessage.LoadAsync(stream, cancellationToken);
        }
        catch (FormatException ex)
        {
            logger.Warning("Rejected unparseable message ({Size} bytes): {Error}", buffer.Length, ex.Message);
            return new SmtpResponse(SmtpReplyCode.TransactionFailed, "Message could not be parsed");
        }

        Message graphMessage = ToGraphMessage(message, transaction.To.Select(m => $"{m.User}@{m.Host}"));

        try
        {
            await graphClient.Users[sendFrom].SendMail.PostAsync(new SendMailPostRequestBody { Message = graphMessage }, cancellationToken: cancellationToken);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode is >= 400 and < 500 and not 429)
        {
            logger.Error("Graph rejected message {Subject}: {Status} {Error}", message.Subject, ex.ResponseStatusCode, ex.Error?.Message ?? ex.Message);
            return new SmtpResponse(SmtpReplyCode.TransactionFailed, $"Rejected by Microsoft Graph: {ex.ResponseStatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.Warning("Graph send failed for {Subject}, client should retry: {Error}", message.Subject, ex.Message);
            return new SmtpResponse(SmtpReplyCode.Aborted, "Temporary failure sending via Microsoft Graph, try again later");
        }

        logger.Information("Sent {Subject} as {From} to {Count} recipient(s)", message.Subject, sendFrom, transaction.To.Count);
        return SmtpResponse.Ok;
    }

    public static Message ToGraphMessage(MimeMessage message, IEnumerable<string> envelope)
    {
        HashSet<string> pending = new(envelope, StringComparer.OrdinalIgnoreCase);

        List<Recipient> Take(InternetAddressList header) =>
            [.. header.Mailboxes.Where(m => pending.Remove(m.Address)).Select(m => ToRecipient(m.Address, m.Name))];

        List<Recipient> to = Take(message.To);
        List<Recipient> cc = Take(message.Cc);

        return new Message
        {
            Subject = message.Subject,
            ToRecipients = to,
            CcRecipients = cc,
            BccRecipients = [.. pending.Select(a => ToRecipient(a, null))],
            ReplyTo = [.. message.ReplyTo.Mailboxes.Select(m => ToRecipient(m.Address, m.Name))],
            Body = message.HtmlBody is { } html
                ? new ItemBody { ContentType = BodyType.Html, Content = html }
                : new ItemBody { ContentType = BodyType.Text, Content = message.TextBody ?? string.Empty },
            Attachments = [.. ToAttachments(message)],
        };
    }

    private static Recipient ToRecipient(string address, string? name) =>
        new() { EmailAddress = new EmailAddress { Address = address, Name = string.IsNullOrEmpty(name) ? null : name } };

    private static IEnumerable<Attachment> ToAttachments(MimeMessage message)
    {
        foreach (MimeEntity entity in message.BodyParts)
        {
            if (entity is MessagePart { Message: { } inner })
            {
                using MemoryStream eml = new();
                inner.WriteTo(eml);
                yield return new FileAttachment
                {
                    Name = entity.ContentDisposition?.FileName ?? $"{inner.Subject ?? "message"}.eml",
                    ContentType = "message/rfc822",
                    ContentBytes = eml.ToArray(),
                };
            }
            else if (entity is MimePart part && (part.IsAttachment || part is not TextPart))
            {
                using MemoryStream content = new();
                part.Content?.DecodeTo(content);
                bool inline = !part.IsAttachment && part.ContentId is not null;
                yield return new FileAttachment
                {
                    Name = part.FileName ?? part.ContentId ?? "attachment",
                    ContentType = part.ContentType.MimeType,
                    ContentBytes = content.ToArray(),
                    IsInline = inline,
                    ContentId = inline ? part.ContentId : null,
                };
            }
        }
    }
}
