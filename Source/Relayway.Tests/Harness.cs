using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using MimeKit;
using Serilog;

namespace Relayway.Tests;

public sealed class FakeGraph : HttpMessageHandler
{
    public ConcurrentQueue<JsonElement> Sent { get; } = new();
    public ConcurrentQueue<string> Subjects { get; } = new();
    public ConcurrentQueue<JsonElement> Drafts { get; } = new();
    public ConcurrentDictionary<string, byte[]> DraftAttachments { get; } = new();
    public ConcurrentQueue<string> DraftsSent { get; } = new();
    public ConcurrentQueue<string> DraftsDeleted { get; } = new();
    public Func<int, HttpStatusCode> Status { get; set; } = _ => HttpStatusCode.OK;
    public bool KeepBodies { get; set; } = true;
    private readonly ConcurrentDictionary<string, (string Name, MemoryStream Content, long Size)> _uploads = new();
    private int _calls;
    private int _drafts;
    public int Calls => _calls;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpStatusCode status = Status(Interlocked.Increment(ref _calls));
        if ((int)status >= 400)
        {
            return Reply(request, status, """{"error":{"code":"Fake","message":"fake failure"}}""");
        }

        string path = request.RequestUri!.AbsolutePath;
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (request.RequestUri.Host == "upload.test")
        {
            return Upload(request, path, await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        }
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        if (path.EndsWith("/sendMail"))
        {
            using JsonDocument json = JsonDocument.Parse(body);
            JsonElement message = json.RootElement.GetProperty("Message");
            Subjects.Enqueue(message.GetProperty("subject").GetString()!);
            if (KeepBodies)
            {
                Sent.Enqueue(message.Clone());
            }
            return Reply(request, HttpStatusCode.Accepted, "");
        }
        if (request.Method == HttpMethod.Post && segments[^1] == "messages")
        {
            string id = $"draft-{Interlocked.Increment(ref _drafts)}";
            Drafts.Enqueue(JsonDocument.Parse(body).RootElement.Clone());
            return Reply(request, HttpStatusCode.Created, $$"""{"id":"{{id}}"}""");
        }
        if (segments[^1] == "createUploadSession")
        {
            JsonElement item = JsonDocument.Parse(body).RootElement.GetProperty("AttachmentItem");
            string url = $"https://upload.test/{segments[^3]}/{Guid.NewGuid():N}";
            _uploads[new Uri(url).AbsolutePath] = (item.GetProperty("name").GetString()!, new MemoryStream(), item.GetProperty("size").GetInt64());
            return Reply(request, HttpStatusCode.Created, $$"""{"uploadUrl":"{{url}}","expirationDateTime":"2099-01-01T00:00:00Z","nextExpectedRanges":["0-"]}""");
        }
        if (segments[^1] == "attachments")
        {
            JsonElement attachment = JsonDocument.Parse(body).RootElement;
            DraftAttachments[attachment.GetProperty("name").GetString()!] = attachment.GetProperty("contentBytes").GetBytesFromBase64();
            return Reply(request, HttpStatusCode.Created, "{}");
        }
        if (segments[^1] == "send")
        {
            DraftsSent.Enqueue(segments[^2]);
            return Reply(request, HttpStatusCode.Accepted, "");
        }
        if (request.Method == HttpMethod.Delete)
        {
            DraftsDeleted.Enqueue(segments[^1]);
            return Reply(request, HttpStatusCode.NoContent, "");
        }
        if (request.Method == HttpMethod.Get)
        {
            return Reply(request, HttpStatusCode.OK, """{"id":"user","mail":"relay@contoso.test"}""");
        }
        return Reply(request, HttpStatusCode.NotFound, """{"error":{"code":"NotFound","message":"fake route missing"}}""");
    }

    private HttpResponseMessage Upload(HttpRequestMessage request, string path, byte[] chunk)
    {
        (string name, MemoryStream content, long size) = _uploads[path];
        content.Write(chunk);
        if (content.Length < size)
        {
            return Reply(request, HttpStatusCode.OK, $$"""{"expirationDateTime":"2099-01-01T00:00:00Z","nextExpectedRanges":["{{content.Length}}-"]}""");
        }
        DraftAttachments[name] = content.ToArray();
        HttpResponseMessage done = Reply(request, HttpStatusCode.Created, "");
        done.Headers.Location = new Uri($"https://graph.test/attachments/{name}");
        return done;
    }

    private static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode status, string body)
    {
        HttpResponseMessage response = new(status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }
}

public sealed class RelayHarness : IAsyncDisposable
{
    public const string SendFrom = "relay@contoso.test";
    public FakeGraph Graph { get; } = new();
    public int Port { get; }
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _run;

    public RelayHarness(string host = "127.0.0.1", string[]? allowedNetworks = null, int maxMessageSizeMb = 35, GraphServiceClient? graph = null, IRequestAdapter? uploads = null, string sendFrom = SendFrom)
    {
        Port = FreePort();
        HttpClient http = GraphClientFactory.Create(finalHandler: Graph);
        graph ??= new GraphServiceClient(http, new AnonymousAuthenticationProvider());
        uploads ??= new BaseGraphRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: http);
        ILogger logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
        SmtpConfiguration smtp = new() { Host = host, Port = Port, AllowedNetworks = allowedNetworks ?? [], MaxMessageSizeMb = maxMessageSizeMb };
        _run = Relay.CreateServer(smtp, new MessageHandler(graph, uploads, logger, sendFrom), logger).StartAsync(_cts.Token);
    }

    public async Task<SmtpClient> ConnectAsync()
    {
        SmtpClient client = new() { Timeout = 300_000 };
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await client.ConnectAsync("127.0.0.1", Port, MailKit.Security.SecureSocketOptions.None);
                return client;
            }
            catch (SocketException) when (attempt < 50)
            {
                await Task.Delay(100);
            }
        }
    }

    public async Task SendAsync(MimeMessage message, params string[] envelope)
    {
        using SmtpClient client = await ConnectAsync();
        await client.SendAsync(message, message.From.Mailboxes.First(), envelope.Select(MailboxAddress.Parse));
        await client.DisconnectAsync(true);
    }

    public static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
