using System.Buffers;
using System.Text;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using MimeKit;
using Serilog;
using SmtpServer;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Relayway;

public class MessageHandler(GraphServiceClient graphClient, ILogger logger, string sendFrom) : MessageStore
{
    public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
    {

        // Debug log for when this function is called
        Log.Debug("An email has been recived!");

        // Create memory stream
        await using MemoryStream stream = new();

        // Get position 0 
        SequencePosition position = buffer.GetPosition(0);

        // Read buffer and write to memory stream
        while (buffer.TryGet(ref position, out ReadOnlyMemory<byte> memory))
        {
            await stream.WriteAsync(memory, cancellationToken);
        }

        // Get position 0 
        position = buffer.GetPosition(0);

        // Dbeug log for the raw message
        logger.Debug($"Raw message:\n{Encoding.UTF8.GetString(buffer.ToArray())}");

        // Set stream position back to 0
        stream.Position = 0;


        // Load the memory stream as a Mime Message
        MimeMessage? message = await MimeMessage.LoadAsync(stream, cancellationToken);

        if (message == null)
        {
            Log.Warning("Unable to read message as Mime Message!");
            return SmtpResponse.SyntaxError;
        }

        // Debug log for the Mime Message
        logger.Debug($"Mime Message:\n {message}");

        Console.WriteLine(message.Attachments?.Count() ?? 0);


        // Create list of recipients
        List<Recipient> recipients = message.To?
            .OfType<MimeKit.MailboxAddress>() // only process mailbox addresses
            .Select(addr => new Recipient
            {
                EmailAddress = new EmailAddress
                {
                    Address = addr.Address,      // plain email only
                    Name = addr.Name             // optional, can be null or empty
                }
            }).ToList() ?? new List<Recipient>();

        logger.Debug("Recipients list: {Recipients}", string.Join(", ", recipients.Select(r => r.EmailAddress?.Address ?? string.Empty)));

        // Create message 
        SendMailPostRequestBody requestBody = new()
        {
            Message = new Message
            {
                Subject = message.Subject,
                ToRecipients = recipients
            }
        };
        
        // If message does contain a HTML body then use it
        if (!string.IsNullOrEmpty(message.HtmlBody))
        {
            requestBody.Message.Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = message.HtmlBody
            };
        }
        // Else use the text body instead
        else
        {
            requestBody.Message.Body = new ItemBody
            {
                ContentType = BodyType.Text,
                Content = message.TextBody ?? string.Empty
            };
        }

        // If the message has attachments then add them
        if (message.Attachments != null && message.Attachments.Any())
        {
            requestBody.Message.Attachments ??= new List<Attachment>();

            foreach (MimeEntity mimeEntity in message.Attachments)
            {
                if (mimeEntity is MimePart mimePart)
                {
                    using var memory = new MemoryStream();
                    mimePart.Content?.DecodeTo(memory);

                    requestBody.Message.Attachments.Add(new FileAttachment
                    {
                        OdataType = "#microsoft.graph.fileAttachment",
                        Name = mimePart.FileName,
                        ContentBytes = memory.ToArray()
                    });
                }
            }
        }

        try
        {
            // Send email
            await graphClient.Users[sendFrom].SendMail.PostAsync(requestBody, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.Warning($"Unknown Error:\n {ex.Message}");
            return SmtpResponse.SyntaxError;
        }


        // Log success message
        logger.Information("The email with the subject `{MessageSubject}` was received and sent to `{MessageTo}` as `{From}`!", message?.Subject, message?.To, sendFrom);

        // Return email received successfully
        return SmtpResponse.Ok;

    }
}