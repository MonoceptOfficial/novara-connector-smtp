/// <summary>
/// SmtpConnector — Outbound Email for Novara
///
/// PURPOSE: Self-describing SMTP connector for transactional email (notification digests,
/// password resets, alerts). Outbound-only — no webhook support. The manifest advertises
/// the `smtp.email.send` outbound action so the Notifications / Admin modules can emit
/// email without holding credentials.
///
/// AUTH: SMTP server URL + port + username + password. TLS/STARTTLS toggle.
///
/// TEST: Opens an SMTP connection with the configured credentials, issues EHLO + AUTH,
/// then disconnects. Verifies the server accepts the creds without sending any message.
///
/// SEND: Exposes SendEmailAsync as an instance method. The Gateway outbound action engine
/// (when wired) will resolve this connector and invoke SendEmailAsync with an
/// OutboundActionRequest. Until then, modules can inject the connector directly.
/// </summary>
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Novara.Module.SDK;
using Novara.Connector.Smtp.Constants;

namespace Novara.Connector.Smtp;

public class SmtpConnector : ConnectorBase
{
    public override ConnectorManifest Manifest => new()
    {
        Id = "connector.smtp",
        Name = "SMTP Email",
        Version = BuildProvenance.ReadVersion(typeof(SmtpConnector).Assembly),
        Author = "Monocept",
        Description = "Outbound SMTP email — transactional notifications, digests, alerts. Supports plain SMTP, STARTTLS, and SMTPS.",
        Icon = "mail",
        Source = "Official",
        Category = "Communication",
        AuthType = "basic",
        DocumentationUrl = "https://docs.novara.io/connectors/smtp",
        SupportsImport = false,
        SupportsExport = true,
        SupportsWebhook = false,
        SupportedEventTypes = new() { SmtpEvents.SendEmail },
        TargetModules = new() { "novara.notifications", "novara.admin", "novara.workflows" },
        ConfigFields = new()
        {
            new() { Key = "host", Label = "SMTP Host", Type = "text",
                    Required = true,
                    Placeholder = "smtp.office365.com",
                    Description = "Hostname of the SMTP relay.",
                    Group = "Server", Order = 1 },
            new() { Key = "port", Label = "Port", Type = "number",
                    Required = true, DefaultValue = "587",
                    Description = "25 (plain), 465 (SMTPS), 587 (STARTTLS). Most providers use 587.",
                    Group = "Server", Order = 2 },
            new() { Key = "security", Label = "Security", Type = "select",
                    Required = true, DefaultValue = "STARTTLS",
                    Description = "Transport security. STARTTLS is the modern default.",
                    Options = new()
                    {
                        new() { Value = "None",     Label = "None (plain)" },
                        new() { Value = "STARTTLS", Label = "STARTTLS" },
                        new() { Value = "SSL",      Label = "SMTPS (implicit SSL)" }
                    },
                    Group = "Server", Order = 3 },
            new() { Key = "username", Label = "Username / From Address", Type = "text",
                    Required = true,
                    Placeholder = "novara@example.com",
                    Description = "Account the emails are sent as (also used for AUTH).",
                    Group = "Authentication", Order = 4 },
            new() { Key = "password", Label = "Password / App Password",
                    Type = "password", Required = true, Sensitive = true,
                    Description = "SMTP password. For Office365/Gmail, use an app password.",
                    Group = "Authentication", Order = 5 },
            new() { Key = "fromName", Label = "From Name", Type = "text",
                    Required = false, DefaultValue = "Novara",
                    Description = "Display name shown in recipient inboxes.",
                    Group = "Identity", Order = 6 },
            new() { Key = "replyTo", Label = "Reply-To Address", Type = "text",
                    Required = false,
                    Placeholder = "no-reply@example.com",
                    Description = "Optional Reply-To header. Leave empty to use the From address.",
                    Group = "Identity", Order = 7 }
        }
    };

    public override async Task<ConnectorTestResult> TestConnectionAsync(ConnectorConfig config, CancellationToken ct = default)
    {
        var host = config.Values.GetValueOrDefault("host", "");
        var portStr = config.Values.GetValueOrDefault("port", "587");
        var security = config.Values.GetValueOrDefault("security", "STARTTLS");
        var username = config.Values.GetValueOrDefault("username", "");
        var password = config.Values.GetValueOrDefault("password", "");

        if (string.IsNullOrEmpty(host)) return new ConnectorTestResult { Success = false, Message = "SMTP host is required." };
        if (!int.TryParse(portStr, out var port)) return new ConnectorTestResult { Success = false, Message = $"Invalid port '{portStr}'." };
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return new ConnectorTestResult { Success = false, Message = "Username and password are required." };

        try
        {
            using var client = new SmtpClient();
            client.Timeout = 10_000;
            await client.ConnectAsync(host, port, MapSecurity(security), ct);
            await client.AuthenticateAsync(username, password, ct);
            var server = $"{host}:{port}";
            await client.DisconnectAsync(true, ct);
            return new ConnectorTestResult
            {
                Success = true,
                Message = $"SMTP handshake succeeded at {server} (security={security}).",
                ServerVersion = server
            };
        }
        catch (AuthenticationException ex)
        {
            return new ConnectorTestResult { Success = false, Message = $"Authentication failed: {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { Success = false, Message = $"SMTP connection failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Send an email via the configured SMTP server. Called by the outbound action
    /// engine when a module emits a `smtp.email.send` action.
    /// </summary>
    public async Task<ConnectorResult> SendEmailAsync(
        ConnectorConfig config, SmtpSendRequest request, CancellationToken ct = default)
    {
        Guard.NotNull(request, nameof(request));
        Guard.NotEmpty(request.To, nameof(request.To));
        Guard.NotEmpty(request.Subject, nameof(request.Subject));

        var host = config.Values.GetValueOrDefault("host", "");
        var portStr = config.Values.GetValueOrDefault("port", "587");
        var security = config.Values.GetValueOrDefault("security", "STARTTLS");
        var username = config.Values.GetValueOrDefault("username", "");
        var password = config.Values.GetValueOrDefault("password", "");
        var fromName = config.Values.GetValueOrDefault("fromName", "Novara");
        var replyTo = config.Values.GetValueOrDefault("replyTo", "");

        if (!int.TryParse(portStr, out var port))
            return new ConnectorResult { Success = false, Message = $"Invalid port '{portStr}'." };

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, username));
            foreach (var addr in request.To.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                message.To.Add(MailboxAddress.Parse(addr));
            if (!string.IsNullOrEmpty(replyTo))
                message.ReplyTo.Add(MailboxAddress.Parse(replyTo));
            message.Subject = request.Subject;

            var body = new BodyBuilder
            {
                HtmlBody = request.HtmlBody,
                TextBody = request.TextBody
            };
            message.Body = body.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(host, port, MapSecurity(security), ct);
            await client.AuthenticateAsync(username, password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            return new ConnectorResult { Success = true, Message = "Email sent.", RecordsProcessed = 1 };
        }
        catch (Exception ex)
        {
            return new ConnectorResult
            {
                Success = false,
                Message = $"SMTP send failed: {ex.Message}",
                Errors = new() { ex.Message }
            };
        }
    }

    private static SecureSocketOptions MapSecurity(string s) => s.ToUpperInvariant() switch
    {
        "SSL"       => SecureSocketOptions.SslOnConnect,
        "STARTTLS"  => SecureSocketOptions.StartTls,
        _           => SecureSocketOptions.None
    };
}

/// <summary>Input shape for SmtpConnector.SendEmailAsync.</summary>
public class SmtpSendRequest
{
    public string To { get; set; } = "";                // comma-separated
    public string Subject { get; set; } = "";
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
}
