# Relayway - SMTP relay to Microsoft Graph

[![License](https://img.shields.io/github/license/melosso/relayway)](LICENSE)
[![Last commit](https://img.shields.io/github/last-commit/melosso/relayway)](https://github.com/melosso/relayway/commits/main)
[![Latest Release](https://img.shields.io/github/v/release/melosso/relayway)](https://github.com/melosso/relayway/releases/latest)

**Relayway** is a lightweight SMTP relay server that bridges legacy applications with Microsoft Graph's modern OAuth authentication. When Microsoft disabled basic authentication for Exchange, many applications were left unable to send emails through O365. Relayway solves this by acting as a local SMTP server that receives emails and forwards them via Microsoft Graph API.

Common applications that benefit from Relayway include monitoring systems, backup software, legacy ERP systems, and any application that needs to send notifications via email without OAuth support.

> [Releases](https://github.com/melosso/relayway/releases) | [Packages](https://github.com/melosso/relayway/packages)

**Our goal**: Enable any application to send emails through Microsoft 365 without in-app OAuth complexity. 

## Key Features

Relayway is built to solve the Microsoft 365 authentication challenge with minimal complexity. Whether you're running legacy systems or modern applications, Relayway adapts to your infrastructure with secure, efficient email delivery.

* **Zero-config**: Applications connect to localhost:2525 with no authentication required
* **Bridge**: Automatically handles Microsoft Graph authentication and token management
* **Closed by default**: Listens on `localhost` only unless `Smtp:Host` is configured
* **Cross-platform**: Available for Windows, Linux, and Docker environments
* **Lightweight**: Minimal resource usage with efficient email processing

## Requirements

Before deploying Relayway, make sure your environment meets the following requirements. These ensure full functionality across all features, especially Microsoft Graph integration and authentication.

* [.NET 10 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
* A Microsoft 365 Tenant
* A user with appropriate admin roles (Global Administrator, Privileged Role Administrator, Application Administrator, or Cloud Application Administrator) who can grant Application `Mail.Send`, `User.Read.All` and, for messages of 4 MB or more, `Mail.ReadWrite` API permissions
* The email address used as the SendFrom address must be a valid address within the tenant

Ready to go? Then continue:

## Getting Started

Follow these steps to get Relayway up and running in your environment. Setup is fast and straightforward, making it easy to bridge your legacy applications with modern authentication.

### 1. Azure Setup

Create your Azure application registration to enable Microsoft Graph access.

1. Navigate to [Azure App Registrations](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
2. Click **New Registration**, enter a name, leave defaults
3. Go to **API Permissions** > **Add a permission** > **Microsoft Graph** > **Application permissions**
4. Add these permissions: `Mail.Send`, `User.Read.All` and `Mail.ReadWrite` (messages of 4 MB or more are sent as a draft). Finally, press 'Add permissions'.
5. Click **Grant admin consent** for your tenant
6. Navigate to **Certificates & secrets** > **Client secrets** > **New client secret**
7. Set expiry to 24 months, copy the secret value immediately
8. Note your **Client ID** and **Tenant ID** from the Overview tab

### 2. Installation

Grab the [latest release](https://github.com/melosso/relayway/releases/latest) and extract it to your deployment folder.

## Installation
> [!CAUTION]
> Relayway has no SMTP authentication or TLS. Every host that can reach the listen address can send mail as `SendFrom`. Keep `Smtp:Host` on `localhost`, or restrict access by firewall or Docker network when listening on other interfaces.

### 3. Configuration

Define your Microsoft Graph and SMTP settings to enable email relay functionality.

**`appsettings.json`**

```json
{
  "Graph": {
    "ClientId": "your-client-id",
    "TenantId": "your-tenant-id",
    "ClientSecret": "your-client-secret"
  },
  "LogLevel": "Information",
  "SendFrom": "your-sender-address@mycompany.com",
  "Smtp": {
    "Host": "localhost",
    "Port": 2525
  }
}
```

### 4. Deploy

#### Windows Deployment

Extract to `C:\Relayway` and run the executable. For automatic startup, import the included `Relayway.xml` into Task Scheduler.

## Docker Deployment

For containerized environments, Relayway provides ready-to-use Docker images with environment variable configuration.

### Docker (Run)

```bash
docker run -d \
  --name relayway \
  -e LogLevel=Warning \
  -p 127.0.0.1:2525:2525 \
  -e Graph__TenantId="your-tenant-id" \
  -e Graph__ClientId="your-client-id" \
  -e Graph__ClientSecret="your-client-secret" \
  -e SendFrom="your-sender-address@mycompany.com" \
  ghcr.io/melosso/relayway
```

### Docker Compose

```yaml
services:
  relayway:
    image: ghcr.io/melosso/relayway
    container_name: relayway
    ports:
      - "127.0.0.1:2525:2525"
    environment:
      - LogLevel=Warning
      - Graph__TenantId=your-tenant-id
      - Graph__ClientId=your-client-id
      - Graph__ClientSecret=your-client-secret
      - SendFrom=your-sender-address@mycompany.com
    restart: unless-stopped
```

## Configuration

### Azure App Creation
1. Go to the ['App registrations' section in Azure](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade).
2. Click 'New Registration'.
3. Enter a name and leave everything else as default.
4. Navigate to 'API permissions' and click 'Add a permission'.
5. Choose 'Microsoft Graph', then select 'Application permissions', then find `Mail.Send` and tick it. Do the same for `User.Read.All` and `Mail.ReadWrite`. Finally, press 'Add permissions'.
6. Grant admin consent by clicking 'Grant admin consent for Tenant Name' (where Tenant Name is the name of your Microsoft 365 tenant). Hit 'Yes' at confirmation.
7. Navigate to 'Certificates & secrets', choose the 'Client secrets' tab, then click 'New client secret', enter a description and set expiry to 24 months or a custom value.
    > [!TIP]
    > Set a reminder in your calendar now for 24 months' time to renew and update this secret.
8. Copy the secret value and make note of it.
    > [!IMPORTANT]
    > The secret value is only displayed once.
9. The Client ID and Tenant ID can be found in the overview tab.

### Environment Variables

For Docker and containerized deployments, use environment variables for configuration:

```bash
LogLevel=Information
Smtp__Host=localhost
Smtp__Port=2525
Graph__TenantId=your-tenant-id
Graph__ClientId=your-client-id
Graph__ClientSecret=your-client-secret
SendFrom=your-sender-address@mycompany.com
```

| Setting | Default | Purpose |
| --- | --- | --- |
| `SendFrom` | required | Mailbox that sends every message |
| `Graph:TenantId`, `Graph:ClientId`, `Graph:ClientSecret` | required | App registration |
| `Graph:Cloud` | `Global` | `Global`, `USGovernment` (GCC High), `USGovernmentDoD` or `China` |
| `Smtp:Host` | `localhost` | Bind address |
| `Smtp:Port` | `2525` | Bind port |
| `Smtp:AllowedNetworks` | any | Client addresses or CIDR networks allowed to send, e.g. `Smtp__AllowedNetworks__0=192.168.1.0/24` |
| `Smtp:MaxMessageSizeMb` | `35` | Largest accepted message, 1 to 150; match the Exchange Online `MaxSendSize` of `SendFrom` |
| `LogLevel` | `Information` | Serilog minimum level |

Messages under 4 MB go through Graph `sendMail`. Larger messages, and messages Graph rejects with 413, are created as a draft in `SendFrom`, attachments of 3 MB or more are uploaded in chunks, and the draft is sent. This path needs `Mail.ReadWrite`. Both `Mail.Send` and `Mail.ReadWrite` apply to every mailbox in the tenant unless limited with [RBAC for Applications](https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac) to `SendFrom`.

### Application Configuration

Configure your legacy applications to use Relayway as their SMTP server:

```
SMTP_HOST=localhost
SMTP_PORT=2525
SMTP_FROM_EMAIL=your-sender-address@mycompany.com
SMTP_SECURE=false
SMTP_AUTH=false
```

### Security Considerations

`Smtp:Host` is the bind address: `localhost` binds loopback, `0.0.0.0` binds all interfaces. The Docker image defaults to `Smtp__Host=0.0.0.0`; the publish address sets exposure:

| Publish | Reachable from |
| --- | --- |
| `127.0.0.1:2525:2525` | Docker host |
| `2525:2525` | All networks of the Docker host |
| none | Containers on the same Docker network |

Relayway has no SMTP authentication. Any client that reaches the port sends as `SendFrom`, unless `Smtp:AllowedNetworks` is set. In Docker, clients on the Docker host connect from the bridge gateway address (for example `172.17.0.1`).

### SMTP replies

| Reply | Cause | Client action |
| --- | --- | --- |
| `250` | Graph accepted the message | None |
| `451` | Graph 429 or 5xx after SDK retries, network or token failure | Retry |
| `550` | Client address not in `Smtp:AllowedNetworks` | None |
| `552` | Message over `Smtp:MaxMessageSizeMb` | None |
| `554` | Graph 4xx (403 on messages of 4 MB or more: `Mail.ReadWrite` missing), or invalid MIME | None |

### Testing

Use tools like [SMTP Test Tool](https://github.com/georgjf/SMTPtool) to verify functionality:

```bash
# Using swaks for testing
docker run --network docker_default --rm -ti chko/swaks \
  --to recipient@example.com \
  --from your-sender-address@mycompany.com \
  --server relayway \
  --port 2525 \
  --header "Subject: Test Email"
```

## Development

```bash
dotnet build Source/Relayway.slnx
dotnet test --project Source/Relayway.Tests
RELAYWAY_STRESS=1 dotnet test --project Source/Relayway.Tests -- --filter-trait "Category=Stress"
```

Tests run an in-process SMTP server against a fake Graph endpoint. Docker tests (skipped without a Docker daemon) build the image, check startup against Microsoft Entra ID with invalid credentials, and send through the relay with `swaks`.

Live tests send through a real tenant, over both the `sendMail` and the draft path, and through the Docker image on a published port. They run when `RELAYWAY_LIVE_TENANT_ID`, `RELAYWAY_LIVE_CLIENT_ID`, `RELAYWAY_LIVE_CLIENT_SECRET`, `RELAYWAY_LIVE_SEND_FROM` and `RELAYWAY_LIVE_RECIPIENT` are set (optional: `RELAYWAY_LIVE_CLOUD`), and are not part of CI. Use an app registration and mailbox in a tenant you control.

## Logging

Relayway provides comprehensive logging to help you monitor email delivery and troubleshoot issues.

* Configurable log levels: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`
* OAuth token management logging
* SMTP transaction logging
* Microsoft Graph API interaction logs

Log configuration follows [Serilog standards](https://github.com/serilog/serilog/wiki/Configuration-Basics#minimum-level) for flexible output formatting and destinations.

## Credits

> [!NOTE]
> This is a fork of [MustMail](https://github.com/bxdavies/MustMail) by u/bxdavies. 

Thanks to the open source tools that make Relayway possible.

* [MailMust](https://github.com/bxdavies/MustMail) by Ben Davies

This project relies on:

* [ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/)
* [SmtpServer](https://github.com/cosullivan/SmtpServer) by Cain O'Sullivan
* [Microsoft Graph SDK](https://github.com/microsoftgraph/msgraph-sdk-dotnet)
* [Serilog](https://serilog.net/)


## Contribution

Contributions are welcome, please submit a PR if you'd like to help improve Relayway. Found a bug or have a feature idea? [Open an issue](https://github.com/melosso/relayway/issues) with detailed information.

## License

This project is licensed under the GNU 3.0 license (AGPL-3.0). See [LICENSE](LICENSE) for full details.
