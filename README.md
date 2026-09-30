# Relayway - SMTP relay to Microsoft Graph

[![License](https://img.shields.io/github/license/melosso/relayway)](LICENSE)
[![Last commit](https://img.shields.io/github/last-commit/melosso/relayway)](https://github.com/melosso/relayway/commits/main)
[![Latest Release](https://img.shields.io/github/v/release/melosso/relayway)](https://github.com/melosso/relayway/releases/latest)

**Relayway** is a headless, lightweight SMTP relay server that bridges legacy applications with Microsoft Graph's modern OAuth authentication. When Microsoft disabled basic authentication for Exchange, many applications were left unable to send emails through O365. Relayway solves this by acting as a local SMTP server that receives emails and forwards them via Microsoft Graph API.

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

* A Microsoft 365 Tenant
* A user with appropriate admin roles (Global Administrator, Privileged Role Administrator, Application Administrator, or Cloud Application Administrator) who can grant Application `Mail.Send`, `User.Read.All` and, for messages of 4 MB or more, `Mail.ReadWrite` API permissions
* The email address used as the SendFrom address must be a valid address within the tenant

## Getting Started

### 1. Azure Setup

1. Navigate to [Azure App Registrations](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
2. Click **New Registration**, enter a name, leave defaults
3. Go to **API Permissions** > **Add a permission** > **Microsoft Graph** > **Application permissions**
4. Add these permissions: `Mail.Send`, `User.Read.All` and `Mail.ReadWrite` (messages of 4 MB or more are sent as a draft). Finally, press 'Add permissions'
5. Click **Grant admin consent** for your tenant
6. Navigate to **Certificates & secrets** > **Client secrets** > **New client secret**
7. Set expiry to 24 months, copy the secret value (shown once) and note the expiry date: Relayway stops sending when the secret expires
8. Note your **Client ID** and **Tenant ID** from the Overview tab

### 2. Installation

> [!CAUTION]
> Relayway has no SMTP authentication or TLS. Every host that can reach the listen address can send mail as `SendFrom`. Keep `Smtp:Host` on `localhost`, or set `RELAYWAY_ALLOWED_NETWORKS` and restrict access by firewall or container network.

<details>
<summary>Binary</summary>

<br>

Requires the [.NET 10 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Extract the [latest release](https://github.com/melosso/relayway/releases/latest) for your platform, e.g. to `C:\Relayway` or `/opt/relayway`.

**Configuration**

Settings are read from `appsettings.json` and `.env` beside the executable. A setting in `.env` overrides the same setting in `appsettings.json`.

`appsettings.json`:

```json
{
  "Graph": {
    "TenantId": "your-tenant-id",
    "ClientId": "your-client-id",
    "ClientSecret": "your-client-secret"
  },
  "SendFrom": "your-sender-address@mycompany.com",
  "Smtp": {
    "Host": "localhost",
    "Port": 2525
  }
}
```

`.env`:

```bash
RELAYWAY_TENANT_ID=your-tenant-id
RELAYWAY_CLIENT_ID=your-client-id
RELAYWAY_CLIENT_SECRET=your-client-secret
RELAYWAY_SEND_FROM=your-sender-address@mycompany.com
```

**Run**

| Platform | Command | Start at boot |
| --- | --- | --- |
| Windows | `C:\Relayway\Relayway.exe` | Import `Relayway.xml` into Task Scheduler |
| Linux, macOS | `/opt/relayway/Relayway` | Service manager of the host, e.g. a systemd unit |

</details>

<details>
<summary>Docker (or Podman)</summary>

<br>

The examples publish on `127.0.0.1:2525`: only the host connects. For LAN clients, publish `2525:2525` and set `RELAYWAY_ALLOWED_NETWORKS`, see [Security Considerations](#security-considerations). For Podman, replace `docker` with `podman`; `podman compose` reads the same file.

**Run**

```bash
docker run -d \
  --name relayway \
  -p 127.0.0.1:2525:2525 \
  -e RELAYWAY_TENANT_ID="your-tenant-id" \
  -e RELAYWAY_CLIENT_ID="your-client-id" \
  -e RELAYWAY_CLIENT_SECRET="your-client-secret" \
  -e RELAYWAY_SEND_FROM="your-sender-address@mycompany.com" \
  ghcr.io/melosso/relayway
```

With the settings in a `.env` file instead of `-e` flags:

```bash
docker run -d --name relayway -p 127.0.0.1:2525:2525 --env-file .env ghcr.io/melosso/relayway
```

**Compose**

The client secret is read from a secret file through `RELAYWAY_CLIENT_SECRET_FILE`:

```yaml
services:
  relayway:
    image: ghcr.io/melosso/relayway
    container_name: relayway
    ports:
      - "127.0.0.1:2525:2525"
    environment:
      - RELAYWAY_TENANT_ID=your-tenant-id
      - RELAYWAY_CLIENT_ID=your-client-id
      - RELAYWAY_CLIENT_SECRET_FILE=/run/secrets/relayway_client_secret
      - RELAYWAY_SEND_FROM=your-sender-address@mycompany.com
    secrets:
      - relayway_client_secret
    restart: unless-stopped

secrets:
  relayway_client_secret:
    file: ./client_secret.txt
```

With a `.env` file, replace `environment:` and `secrets:` by `env_file: .env`.

</details>

## Configuration

### Settings

Set each setting in `appsettings.json`, `.env` or the environment.

| Setting | Variable | Default | Purpose |
| --- | --- | --- | --- |
| `SendFrom` | `RELAYWAY_SEND_FROM` | required | Mailbox that sends every message |
| `Graph:TenantId` | `RELAYWAY_TENANT_ID` | required | App registration tenant |
| `Graph:ClientId` | `RELAYWAY_CLIENT_ID` | required | App registration client |
| `Graph:ClientSecret` | `RELAYWAY_CLIENT_SECRET` | required | App registration secret |
| `Graph:Cloud` | `RELAYWAY_CLOUD` | `Global` | `Global` (including EU tenants), `USGovernment` (GCC High), `USGovernmentDoD` or `China` |
| `Smtp:Host` | `RELAYWAY_SMTP_HOST` | `localhost` (`0.0.0.0` in Docker) | Bind address |
| `Smtp:Port` | `RELAYWAY_SMTP_PORT` | `2525` | Bind port |
| `Smtp:AllowedNetworks` | `RELAYWAY_ALLOWED_NETWORKS` | any | Comma-separated addresses or CIDR networks allowed to send, e.g. `192.168.1.0/24,10.0.0.5` |
| `Smtp:MaxMessageSizeMb` | `RELAYWAY_MAX_MESSAGE_SIZE_MB` | `35` | Largest accepted message, 1 to 150; match the Exchange Online `MaxSendSize` of `SendFrom` |
| `LogLevel` | `RELAYWAY_LOG_LEVEL` | `Information` | Serilog minimum level |

Sources, lowest to highest precedence:

1. `appsettings.json` beside the executable
2. `.env` beside the executable, or the file named by `RELAYWAY_ENV_FILE`
3. `RELAYWAY_*` environment variables
4. `Section__Key` environment variables (`Smtp__Host`, `SendFrom`)

Every `RELAYWAY_*` variable has a `_FILE` form that reads the value from a file, e.g. `RELAYWAY_CLIENT_SECRET_FILE=/run/secrets/relayway_client_secret`.

`.env` syntax: `NAME=value` per line, `#` comments, optional `export ` prefix and quotes. Unknown names, malformed lines, repeated names and a `Section__Key` variable overriding its `RELAYWAY_*` alias are logged as warnings at startup, without values.

Messages under 4 MB go through Graph `sendMail`. Larger messages, and messages Graph rejects with 413, are created as a draft in `SendFrom`, attachments of 3 MB or more are uploaded in chunks, and the draft is sent. This path needs `Mail.ReadWrite`. Both `Mail.Send` and `Mail.ReadWrite` apply to every mailbox in the tenant unless limited with [RBAC for Applications](https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac) to `SendFrom`.

### Client Setup

SMTP settings in the application that sends mail:

| Field | Value |
| --- | --- |
| Server | Relayway host, e.g. `localhost` |
| Port | `2525` |
| Encryption | None |
| Authentication | None |
| From address | Any; mail is sent as `SendFrom` |

### Security Considerations

`Smtp:Host` is the bind address: `localhost` binds loopback, `0.0.0.0` binds all interfaces. The Docker image defaults to `RELAYWAY_SMTP_HOST=0.0.0.0`; the publish address sets exposure:

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

Licensed under the [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0), as is [MustMail](https://github.com/bxdavies/MustMail), from which Relayway is forked.
