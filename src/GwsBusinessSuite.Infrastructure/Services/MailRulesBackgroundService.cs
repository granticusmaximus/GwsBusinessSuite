using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Mail rules on new inbox mail (Mail phase 6, 21-11-26): every three minutes, each mailbox with an
// enabled rule has its rules run as its owner (IMailService.RunRulesAsync). A mailbox that can't be
// reached is skipped until the next round; its error shows on the account, as for any mail action.
public sealed class MailRulesBackgroundService(IServiceScopeFactory scopeFactory, ILogger<MailRulesBackgroundService> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
                var mailboxes = (await db.MailRules.AsNoTracking().Where(rule => rule.Enabled).Select(rule => new { rule.Username, rule.AccountId }).ToListAsync(stoppingToken))
                    .Distinct().ToList();
                var mail = scope.ServiceProvider.GetRequiredService<IMailService>();
                foreach (var mailbox in mailboxes)
                {
                    try
                    {
                        var acted = await mail.RunRulesAsync(mailbox.Username, mailbox.AccountId, includeExisting: false, stoppingToken);
                        if (acted > 0) logger.LogInformation("Mail rules acted on {Count} new message(s) for {User}", acted, mailbox.Username);
                    }
                    catch (MailException) { }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Mail rules round failed; it runs again in three minutes");
            }
        }
    }
}
