using GwsBusinessSuite.Application.Email;

namespace GwsBusinessSuite.Application.Abstractions;

public interface IEmailAccountService
{
    Task<EmailAccountValidationResult> ValidateAsync(EmailAccountSettings settings, CancellationToken ct = default);
    Task<EmailAccountSendResult> SendAsync(EmailAccountSettings settings, string toAddress, string subject, string body, CancellationToken ct = default);
}
