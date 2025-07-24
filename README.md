# Relayway - SMTP relay to Microsoft Graph

[![.NET](https://img.shields.io/badge/.NET-5C2D91)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/hawkinslabdev/mijnmotorparkeren)](LICENSE)
[![Last commit](https://img.shields.io/github/last-commit/hawkinslabdev/mijnmotorparkeren)](https://github.com/hawkinslabdev/mijnmotorparkeren/commits/main)
[![Latest Release](https://img.shields.io/github/v/release/melosso/relayway)](https://github.com/melosso/relayway/releases/latest)

**Relayway** is a lightweight SMTP relay server that bridges legacy applications with Microsoft Graph's modern OAuth authentication. When Microsoft disabled basic authentication for Exchange, many applications were left unable to send emails through O365. Relayway solves this by acting as a local SMTP server that receives emails and forwards them via Microsoft Graph API.

Common applications that benefit from Relayway include monitoring systems, backup software, legacy ERP systems, and any application that needs to send notifications via email without OAuth support.

> 📍 [Documentation](https://relayway-docs.melosso.com) | 🧪 [Packages](https://github.com/melosso/relayway/packages)

**Our goal**: Enable any application to send emails through Microsoft 365 without in-app OAuth complexity. 

## 🧩 Key Features

Relayway is built to solve the Microsoft 365 authentication challenge with minimal complexity. Whether you're running legacy systems or modern applications, Relayway adapts to your infrastructure with secure, efficient email delivery.

* **Zero-config**: Applications connect to localhost:2525 with no authentication required
* **Bridge**: Automatically handles Microsoft Graph authentication and token management
* **Secure**: Only accepts connections from localhost, eliminating network security concerns
* **Cross-platform**: Available for Windows, Linux, and Docker environments
* **Lightweight**: Minimal resource usage with efficient email processing

## ⚙️ Requirements

Before deploying Relayway, make sure your environment meets the following requirements. These ensure full functionality across all features, especially Microsoft Graph integration and authentication.

* [.NET 9+ Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
* Microsoft 365 Tenant with appropriate licensing
* Admin access to create Azure app registrations
* Valid sender address within your M365 tenant
* Local filesystem access for configuration and token storage

Ready to go? Then continue:

## 🚀 Getting Started

Follow these steps to get Relayway up and running in your environment. Setup is fast and straightforward, making it easy to bridge your legacy applications with modern authentication.

### 1. Azure Setup

Create your Azure application registration to enable Microsoft Graph access.

1. Navigate to [Azure App Registrations](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
2. Click **New Registration**, enter a name, leave defaults
3. Go to **API Permissions** → **Add a permission** → **Microsoft Graph** → **Application permissions**
4. Add these permissions: `Mail.Send` and `User.Read.All`
5. Click **Grant admin consent** for your tenant
6. Navigate to **Certificates & secrets** → **Client secrets** → **New client secret**
7. Set expiry to 24 months, copy the secret value immediately
8. Note your **Client ID** and **Tenant ID** from the Overview tab

### 2. Installation

Grab the [latest release](https://github.com/melosso/relayway/releases/latest) and extract it to your deployment folder.

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

## 🐳 Docker Deployment

For containerized environments, Relayway provides ready-to-use Docker images with environment variable configuration.

### Docker (Run)

```bash
docker run -d \
  --name relayway \
  -e LogLevel=Warning \
  -e Smtp__Host=localhost \
  -e Smtp__Port=2525 \
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
    environment:
      - LogLevel=Warning
      - Smtp__Host=localhost
      - Smtp__Port=2525
      - Graph__TenantId=your-tenant-id
      - Graph__ClientId=your-client-id
      - Graph__ClientSecret=your-client-secret
      - SendFrom=your-sender-address@mycompany.com
    restart: unless-stopped
```

## 🔐 Configuration

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

Relayway is designed with security in mind by only accepting local connections. Always use `localhost` as your SMTP host, as Relayway has no built-in authentication or encryption by design — this is intentional for local-only usage.

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

## 📊 Logging

Relayway provides comprehensive logging to help you monitor email delivery and troubleshoot issues.

* Configurable log levels: `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`
* OAuth token management logging
* SMTP transaction logging
* Microsoft Graph API interaction logs

Log configuration follows [Serilog standards](https://github.com/serilog/serilog/wiki/Configuration-Basics#minimum-level) for flexible output formatting and destinations.

## 🤝 Credits

Thanks to the open source tools that make Relayway possible:

* [ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/)
* [SmtpServer](https://github.com/cosullivan/SmtpServer) by Cain O'Sullivan
* [Microsoft Graph SDK](https://github.com/microsoftgraph/msgraph-sdk-dotnet)
* [Serilog](https://serilog.net/)


## Contribution

Contributions are welcome, please submit a PR if you'd like to help improve Relayway. Found a bug or have a feature idea? [Open an issue](https://github.com/melosso/relayway/issues) with detailed information.

## License

This project is licensed under the GNU 3.0 license (AGPL-3.0). See [LICENSE](LICENSE) for full details.
