using System.Buffers;
using System.Net;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Graph.Users.Item.Messages.Item.Attachments.CreateUploadSession;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.Kiota.Abstractions;
using MimeKit;
using Serilog;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Relayway;

public static class Relay
{
    public static SmtpServer.SmtpServer CreateServer(SmtpConfiguration smtp, MessageStore store, ILogger logger)
    {
        IPNetwork[] allowed = ParseNetworks(smtp.AllowedNetworks);
        SmtpServerOptionsBuilder builder = new SmtpServerOptionsBuilder()
            .ServerName(smtp.Host)
            .MaxMessageSize(smtp.MaxMessageSizeMb * 1024 * 1024, MaxMessageSizeHandling.Strict);
        foreach (IPAddress address in Resolve(smtp.Host))
        {
            builder.Endpoint(e => e.Endpoint(new IPEndPoint(address, smtp.Port)));
        }

        SmtpServer.ComponentModel.ServiceProvider services = new();
        services.Add(store);
        if (allowed.Length > 0)
        {
            services.Add(new ClientFilter(allowed, logger));
        }
        return new SmtpServer.SmtpServer(builder.Build(), services);
    }

    public static IPAddress[] Resolve(string host) =>
        IPAddress.TryParse(host, out IPAddress? address) ? [address] : Dns.GetHostAddresses(host);

    public static IPNetwork[] ParseNetworks(string list) =>
    [
        .. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value =>
        {
            if (IPAddress.TryParse(value, out IPAddress? single))
            {
                return new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            }
            if (IPNetwork.TryParse(value, out IPNetwork network))
            {
                return network;
            }
            throw new FormatException($"Smtp:AllowedNetworks entry '{value}' is not an IP address or CIDR network, e.g. 192.168.1.0/24 or 10.0.0.5");
        }),
    ];
}

public class ClientFilter(IPNetwork[] allowed, ILogger logger) : MailboxFilter
{
    public override Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
    {
        IPAddress remote = ((IPEndPoint)context.Properties[EndpointListener.RemoteEndPointKey]).Address;
        if (allowed.Any(n => n.Contains(remote)))
        {
            return Task.FromResult(true);
        }
        logger.Warning("Refused client {Remote}: not in Smtp:AllowedNetworks {Allowed}", remote, string.Join(", ", allowed));
        return Task.FromResult(false);
    }
}

public class MessageHandler(GraphServiceClient graphClient, IRequestAdapter uploadAdapter, ILogger logger, string sendFrom) : MessageStore
{
    public const int SendMailLimit = 4 * 1024 * 1024;
    public const int AttachmentPostLimit = 3 * 1024 * 1024;
    private const int UploadSliceSize = 10 * 320 * 1024;

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

        string[] envelope = [.. transaction.To.Select(m => $"{m.User}@{m.Host}")];
        bool viaDraft = buffer.Length >= SendMailLimit;

        try
        {
            if (!viaDraft)
            {
                try
                {
                    await graphClient.Users[sendFrom].SendMail.PostAsync(new SendMailPostRequestBody { Message = ToGraphMessage(message, envelope) }, cancellationToken: cancellationToken);
                }
                catch (ApiException ex) when (ex.ResponseStatusCode == 413)
                {
                    logger.Information("Graph sendMail returned 413 for {Subject} ({Size} bytes), sending as draft", message.Subject, buffer.Length);
                    viaDraft = true;
                }
            }
            if (viaDraft)
            {
                await SendThroughDraftAsync(ToGraphMessage(message, envelope), cancellationToken);
            }
        }
        catch (ODataError ex) when (ex.ResponseStatusCode is >= 400 and < 500 and not 429)
        {
            if (viaDraft && ex.ResponseStatusCode == 403)
            {
                logger.Error("Graph returned 403 on the draft path for {Subject} ({Size} bytes). Messages too large for sendMail (4 MB) are sent as a draft, which needs the Mail.ReadWrite application permission", message.Subject, buffer.Length);
            }
            else
            {
                logger.Error("Graph rejected message {Subject}: {Status} {Error}", message.Subject, ex.ResponseStatusCode, ex.Error?.Message ?? ex.Message);
            }
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

    private async Task SendThroughDraftAsync(Message message, CancellationToken cancellationToken)
    {
        List<FileAttachment> attachments = [.. message.Attachments!.Cast<FileAttachment>()];
        message.Attachments = [];
        Message draft = await graphClient.Users[sendFrom].Messages.PostAsync(message, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Graph returned no draft");
        var draftRequest = graphClient.Users[sendFrom].Messages[draft.Id];
        try
        {
            foreach (FileAttachment file in attachments)
            {
                if (file.ContentBytes!.Length < AttachmentPostLimit)
                {
                    await draftRequest.Attachments.PostAsync(file, cancellationToken: cancellationToken);
                    continue;
                }

                UploadSession session = await draftRequest.Attachments.CreateUploadSession.PostAsync(new CreateUploadSessionPostRequestBody
                {
                    AttachmentItem = new AttachmentItem
                    {
                        AttachmentType = AttachmentType.File,
                        Name = file.Name,
                        Size = file.ContentBytes.Length,
                        ContentType = file.ContentType,
                        IsInline = file.IsInline,
                        ContentId = file.ContentId,
                    },
                }, cancellationToken: cancellationToken) ?? throw new InvalidOperationException($"Graph returned no upload session for {file.Name}");

                using MemoryStream content = new(file.ContentBytes);
                UploadResult<FileAttachment> result = await new LargeFileUploadTask<FileAttachment>(session, content, UploadSliceSize, uploadAdapter)
                    .UploadAsync(cancellationToken: cancellationToken);
                if (!result.UploadSucceeded)
                {
                    throw new InvalidOperationException($"Upload of attachment {file.Name} did not complete");
                }
            }

            await draftRequest.Send.PostAsync(cancellationToken: cancellationToken);
        }
        catch
        {
            try
            {
                await draftRequest.DeleteAsync(cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.Warning("Could not delete unsent draft {DraftId} from {From}: {Error}", draft.Id, sendFrom, ex.Message);
            }
            throw;
        }
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
