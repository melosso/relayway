using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MailKit.Net.Smtp;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using MimeKit;
using Serilog;

namespace Relayway.Tests;

public sealed class FakeGraph : HttpMessageHandler
{
    public ConcurrentQueue<JsonElement> Sent { get; } = new();
    public Func<int, HttpStatusCode> Status { get; set; } = _ => HttpStatusCode.Accepted;
    public bool KeepBodies { get; set; } = true;
    public ConcurrentQueue<string> Subjects { get; } = new();
    private int _calls;
    public int Calls => _calls;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpStatusCode status = Status(Interlocked.Increment(ref _calls));
        if (status == HttpStatusCode.Accepted)
        {
            using JsonDocument json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            JsonElement message = json.RootElement.GetProperty("Message");
            Subjects.Enqueue(message.GetProperty("subject").GetString()!);
            if (KeepBodies)
            {
                Sent.Enqueue(message.Clone());
            }
        }
        HttpResponseMessage response = new(status)
        {
            RequestMessage = request,
            Content = new StringContent(status == HttpStatusCode.Accepted ? "" : """{"error":{"code":"Fake","message":"fake failure"}}""", System.Text.Encoding.UTF8, "application/json"),
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

    public RelayHarness(string host = "127.0.0.1")
    {
        Port = FreePort();
        GraphServiceClient client = new(GraphClientFactory.Create(finalHandler: Graph), new AnonymousAuthenticationProvider());
        SmtpServer.SmtpServer server = Relay.CreateServer(new SmtpConfiguration { Host = host, Port = Port }, new MessageHandler(client, new LoggerConfiguration().WriteTo.Console().CreateLogger(), SendFrom));
        _run = server.StartAsync(_cts.Token);
    }

    public async Task<SmtpClient> ConnectAsync()
    {
        SmtpClient client = new();
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
