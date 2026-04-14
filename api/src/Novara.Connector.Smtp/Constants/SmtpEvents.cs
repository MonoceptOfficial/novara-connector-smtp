/// <summary>
/// SmtpEvents — Outbound action types the SMTP connector can execute.
/// (SMTP has no inbound webhook — it is outbound-only.)
/// </summary>
namespace Novara.Connector.Smtp.Constants;

public static class SmtpEvents
{
    public const string SendEmail = "smtp.email.send";
}
