namespace Relayway;

public class Configuration
{
    public SmtpConfiguration Smtp { get; init; } = new();
    public MicrosoftGraphConfiguration? Graph { get; init; }
    public string? SendFrom { get; init; }
}

public class SmtpConfiguration
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 2525;
}

public class MicrosoftGraphConfiguration
{
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
}
