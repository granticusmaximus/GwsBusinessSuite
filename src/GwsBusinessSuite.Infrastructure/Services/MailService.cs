using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Mail;
using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Domain.Entities;
using MailKit;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// IMailService: each person's own mailboxes (see MailModels). An account row says which provider
// and how to sign in; IMailboxBackend does the talking. Every call names the account, and only the
// person's own accounts are found. Provider and network failures come back as MailException with
// the server's reason, and the last one is kept on the account for the page.
public sealed class MailService(
    IAppDbContext db,
    ISecretProtector secretProtector,
    IEnumerable<IMailboxBackend> backends,
    TimeProvider timeProvider,
    ILogger<MailService>? logger = null,
    MailInboxCache? inboxCache = null) : GwsBusinessSuite.Application.Mail.IMailService
{
    private const int MaxSearchLength = 200;
    private const int MaxBodyLength = 1_000_000;
    private const int MaxRecipients = 100;

    public async Task<IReadOnlyList<MailAccountView>> ListAccountsAsync(string username, CancellationToken cancellationToken = default) =>
        (await db.MailAccounts.AsNoTracking().Where(account => account.Username == username).ToListAsync(cancellationToken))
            .OrderBy(account => account.CreatedAt).Select(ToView).ToList();

    public async Task<MailAccountView> SaveImapAccountAsync(string username, Guid? accountId, MailImapAccountInput input, CancellationToken cancellationToken = default)
    {
        var address = (input.Address ?? string.Empty).Trim();
        if (!MailboxAddress.TryParse(address, out var parsed) || !parsed.Address.Contains('@'))
            throw new MailException("Enter the mailbox's email address.");
        var imapHost = Host(input.ImapHost, "IMAP server (e.g. imap.mail.me.com)");
        var smtpHost = Host(input.SmtpHost, "SMTP server (e.g. smtp.mail.me.com)");
        if (input.ImapPort is < 1 or > 65535 || input.SmtpPort is < 1 or > 65535) throw new MailException("Ports are between 1 and 65535.");
        if (!MailServerSecurity.All.Contains(input.SmtpSecurity)) throw new MailException("Choose how to secure the SMTP connection.");
        var login = string.IsNullOrWhiteSpace(input.LoginUsername) ? parsed.Address : input.LoginUsername.Trim();

        var account = accountId is { } id ? await FindAsync(username, id, cancellationToken) : null;
        if (account is null) await RequireRoomAsync(username, cancellationToken);
        var password = !string.IsNullOrEmpty(input.Password) ? input.Password.Replace(" ", string.Empty)
            : account is { Provider: MailProviders.Imap, PasswordProtected.Length: > 0 } ? secretProtector.Unprotect(account.PasswordProtected)
            : throw new MailException("Enter the mailbox's app password.");

        var candidate = new MailAccount
        {
            Username = username, Provider = MailProviders.Imap, Address = parsed.Address, DisplayName = (input.DisplayName ?? string.Empty).Trim(),
            ImapHost = imapHost, ImapPort = input.ImapPort, SmtpHost = smtpHost, SmtpPort = input.SmtpPort, SmtpSecurity = input.SmtpSecurity,
            LoginUsername = login
        };
        var warning = await Backend(MailProviders.Imap).VerifyAsync(candidate, password, cancellationToken);

        if (account is null)
        {
            account = new MailAccount { Username = username, CreatedBy = username };
            db.MailAccounts.Add(account);
        }
        else if (account.Provider == MailProviders.Google)
        {
            await RemoveCredentialAsync(account.CredentialId, cancellationToken);
        }
        account.Provider = MailProviders.Imap;
        account.Address = candidate.Address;
        account.DisplayName = candidate.DisplayName;
        account.CredentialId = null;
        account.TokenProtected = string.Empty;
        account.ImapHost = imapHost;
        account.ImapPort = input.ImapPort;
        account.SmtpHost = smtpHost;
        account.SmtpPort = input.SmtpPort;
        account.SmtpSecurity = input.SmtpSecurity;
        account.LoginUsername = login;
        account.PasswordProtected = secretProtector.Protect(password);
        account.LastError = null;
        account.UpdatedAt = timeProvider.GetUtcNow();
        account.UpdatedBy = username;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(account) with { Warning = warning };
    }

    public async Task<MailAccountView> SaveGoogleAccountAsync(string username, string tokenJson, string address, CancellationToken cancellationToken = default)
    {
        var trimmed = address.Trim();
        var account = (await db.MailAccounts.Where(item => item.Username == username).ToListAsync(cancellationToken))
            .FirstOrDefault(item => item.Provider == MailProviders.Google && string.Equals(item.Address, trimmed, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            await RequireRoomAsync(username, cancellationToken);
            account = new MailAccount { Username = username, CreatedBy = username };
            db.MailAccounts.Add(account);
        }
        await RemoveCredentialAsync(account.CredentialId, cancellationToken);
        account.Provider = MailProviders.Google;
        account.Address = trimmed;
        account.CredentialId = null;
        account.TokenProtected = secretProtector.Protect(tokenJson);
        account.ImapHost = account.SmtpHost = account.LoginUsername = account.PasswordProtected = string.Empty;
        account.LastError = null;
        account.UpdatedAt = timeProvider.GetUtcNow();
        account.UpdatedBy = username;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(account);
    }

    public async Task RemoveAccountAsync(string username, Guid accountId, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(username, accountId, cancellationToken) is not { } account) return;
        await RemoveCredentialAsync(account.CredentialId, cancellationToken);
        db.MailAccounts.Remove(account);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MailAccountView> SaveSignatureAsync(string username, Guid accountId, string signature, CancellationToken cancellationToken = default)
    {
        var account = await FindAsync(username, accountId, cancellationToken)
            ?? throw new MailException("That mailbox is no longer connected.");
        var clean = (signature ?? string.Empty).Trim();
        if (clean.Length > 2_000) throw new MailException("A signature can be up to 2,000 characters.");
        account.Signature = clean;
        account.UpdatedAt = timeProvider.GetUtcNow();
        account.UpdatedBy = username;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(account);
    }

    public Task<IReadOnlyList<MailFolderView>> ListFoldersAsync(string username, Guid accountId, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, (backend, account) => backend.ListFoldersAsync(account, cancellationToken), cancellationToken);

    // ---- Mail rules (phase 6, 21-11-26) ----
    public const int RulesWindow = 200;
    public const int RuleSeenKeepDays = 60;

    private static MailRuleView ToView(MailRule rule) => new(rule.Id, rule.Name, rule.Enabled, rule.MatchAll,
        MailRuleEngine.FromJson<MailRuleCondition>(rule.ConditionsJson), MailRuleEngine.FromJson<MailRuleAction>(rule.ActionsJson), rule.Order);

    public async Task<IReadOnlyList<MailRuleView>> ListRulesAsync(string username, Guid accountId, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(username, accountId, cancellationToken) is null) return [];
        return (await db.MailRules.AsNoTracking().Where(rule => rule.AccountId == accountId).ToListAsync(cancellationToken))
            .OrderBy(rule => rule.Order).ThenBy(rule => rule.CreatedAt).Select(ToView).ToList();
    }

    public async Task<MailRuleView> SaveRuleAsync(string username, Guid accountId, MailRuleView rule, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(username, accountId, cancellationToken) is null) throw new MailException("Connect a mailbox first.");
        var clean = MailRuleEngine.Validate(rule);
        var now = timeProvider.GetUtcNow();
        var row = clean.Id == Guid.Empty ? null : await db.MailRules.FirstOrDefaultAsync(r => r.Id == clean.Id && r.AccountId == accountId, cancellationToken);
        if (row is null)
        {
            if (await db.MailRules.CountAsync(r => r.AccountId == accountId, cancellationToken) >= MailRuleEngine.MaxRules)
                throw new MailException($"A mailbox can have up to {MailRuleEngine.MaxRules} rules.");
            var order = (await db.MailRules.Where(r => r.AccountId == accountId).Select(r => (int?)r.Order).MaxAsync(cancellationToken) ?? -1) + 1;
            row = new MailRule { AccountId = accountId, Username = username, Name = clean.Name, Order = order, CreatedAt = now, CreatedBy = username };
            db.MailRules.Add(row);
        }
        row.Name = clean.Name;
        row.Enabled = clean.Enabled;
        row.MatchAll = clean.MatchAll;
        row.ConditionsJson = MailRuleEngine.ToJson(clean.Conditions);
        row.ActionsJson = MailRuleEngine.ToJson(clean.Actions);
        row.UpdatedAt = now;
        row.UpdatedBy = username;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(row);
    }

    public async Task DeleteRuleAsync(string username, Guid accountId, Guid ruleId, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(username, accountId, cancellationToken) is null) return;
        if (await db.MailRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.AccountId == accountId, cancellationToken) is { } row)
        {
            db.MailRules.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // The newest RulesWindow inbox messages the rules haven't seen (or all of them, includeExisting): the
    // first rule that matches each one acts on it. The very first pass on a mailbox only records what's
    // there, so rules act on mail that arrives after they're made. Returns how many messages a rule acted on.
    public async Task<int> RunRulesAsync(string username, Guid accountId, bool includeExisting, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(username, accountId, cancellationToken) is null) throw new MailException("Connect a mailbox first.");
        var rules = (await ListRulesAsync(username, accountId, cancellationToken)).Where(rule => rule.Enabled).ToList();
        if (rules.Count == 0) return 0;
        var now = timeProvider.GetUtcNow();
        var acted = 0;
        await RunAsync(username, accountId, async (backend, account) =>
        {
            var items = await backend.InboxForSortingAsync(account, RulesWindow, cancellationToken);
            var keys = items.Select(item => item.MessageKey).Distinct().ToList();
            var seenRows = await db.MailRuleSeen.Where(row => row.AccountId == accountId).ToListAsync(cancellationToken);
            var seen = seenRows.Select(row => row.MessageKey).ToHashSet(StringComparer.Ordinal);
            var firstPass = seenRows.Count == 0 && !includeExisting;
            foreach (var item in items)
            {
                if (seen.Contains(item.MessageKey) && !includeExisting) continue;
                if (!firstPass && rules.FirstOrDefault(rule => MailRuleEngine.Matches(rule, item)) is { } rule)
                {
                    try
                    {
                        await ApplyRuleAsync(backend, account, item, rule, now, cancellationToken);
                        acted++;
                    }
                    catch (MailException ex)
                    {
                        logger?.LogWarning("Mail rule {Rule} couldn't act on a message: {Message}", rule.Name, ex.Message);
                    }
                }
                if (seen.Add(item.MessageKey))
                    db.MailRuleSeen.Add(new MailRuleSeen { AccountId = accountId, MessageKey = item.MessageKey, CreatedAt = now, CreatedBy = "mail-rules" });
            }
            var cutoff = now.AddDays(-RuleSeenKeepDays);
            db.MailRuleSeen.RemoveRange(seenRows.Where(row => row.CreatedAt < cutoff && !keys.Contains(row.MessageKey)));
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);
        if (acted > 0) inboxCache?.Invalidate(accountId);
        return acted;
    }

    private async Task ApplyRuleAsync(IMailboxBackend backend, MailAccount account, MailInboxItem item, MailRuleView rule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var id = item.Summary.Id;
        foreach (var action in MailRuleEngine.InRunOrder(rule.Actions))
        {
            switch (action.Kind)
            {
                case MailRuleActionKinds.Read: await backend.ApplyAsync(account, [id], MailActions.MarkRead, cancellationToken); break;
                case MailRuleActionKinds.Star: await backend.ApplyAsync(account, [id], MailActions.Star, cancellationToken); break;
                case MailRuleActionKinds.Archive: await backend.ApplyAsync(account, [id], MailActions.Archive, cancellationToken); break;
                case MailRuleActionKinds.Folder: await backend.MoveToFolderAsync(account, [id], action.Target, cancellationToken); break;
                case MailRuleActionKinds.Tab:
                    var row = await db.MailMessageTabs.FirstOrDefaultAsync(r => r.AccountId == account.Id && r.MessageKey == item.MessageKey, cancellationToken);
                    if (row is null)
                    {
                        row = new MailMessageTab { AccountId = account.Id, MessageKey = item.MessageKey, TabKey = action.Target, CreatedAt = now, CreatedBy = "mail-rules" };
                        db.MailMessageTabs.Add(row);
                    }
                    row.TabKey = action.Target;
                    row.Source = MailTabSources.Rule;
                    row.Reason = $"the rule \"{rule.Name}\"";
                    row.Subject = string.Empty;
                    row.UpdatedAt = now;
                    break;
            }
        }
    }

    // ---- Folders (phase 5, 21-11-26) ----
    public const int MaxFolderNameLength = 100;

    // A folder name the mailbox will take: trimmed, not empty, no control characters, and not one of
    // the standard folders' names.
    public static string FolderName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) throw new MailException("Give the folder a name.");
        if (trimmed.Length > MaxFolderNameLength) throw new MailException($"Folder names can be up to {MaxFolderNameLength} characters.");
        if (trimmed.Any(char.IsControl)) throw new MailException("That name has characters a mailbox can't use.");
        if (MailFolders.All.Any(folder => string.Equals(folder.Label, trimmed, StringComparison.OrdinalIgnoreCase)) || trimmed.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            throw new MailException($"\"{trimmed}\" is one of the standard folders.");
        return trimmed;
    }

    private static string CustomFolderKey(string? folderKey) =>
        folderKey is { } key && (key.StartsWith(MailFolders.FolderPrefix, StringComparison.Ordinal) || key.StartsWith(MailFolders.LabelPrefix, StringComparison.Ordinal))
            && key.Length > MailFolders.FolderPrefix.Length
            ? key
            : throw new MailException("Pick one of your own folders.");

    public Task<MailFolderView> CreateFolderAsync(string username, Guid accountId, string name, CancellationToken cancellationToken = default)
    {
        var clean = FolderName(name);
        return RunAsync(username, accountId, (backend, account) => backend.CreateFolderAsync(account, clean, cancellationToken), cancellationToken);
    }

    public Task RenameFolderAsync(string username, Guid accountId, string folderKey, string name, CancellationToken cancellationToken = default)
    {
        var key = CustomFolderKey(folderKey);
        var clean = FolderName(name);
        return RunAsync(username, accountId, (backend, account) => backend.RenameFolderAsync(account, key, clean, cancellationToken), cancellationToken);
    }

    public Task DeleteFolderAsync(string username, Guid accountId, string folderKey, CancellationToken cancellationToken = default)
    {
        var key = CustomFolderKey(folderKey);
        return RunAsync(username, accountId, (backend, account) => backend.DeleteFolderAsync(account, key, cancellationToken), cancellationToken);
    }

    public Task<MailActionResult> MoveToFolderAsync(string username, Guid accountId, IReadOnlyList<string> messageIds, string folderKey, CancellationToken cancellationToken = default)
    {
        var key = CustomFolderKey(folderKey);
        var ids = messageIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return Task.FromResult(new MailActionResult(0, null));
        if (ids.Count > MailLimits.MaxBulk) throw new MailException($"Mail can act on up to {MailLimits.MaxBulk} messages at a time.");
        inboxCache?.Invalidate(accountId);
        return RunAsync(username, accountId, async (backend, account) =>
            new MailActionResult(ids.Count, await backend.MoveToFolderAsync(account, ids, key, cancellationToken)), cancellationToken);
    }

    private Task<T> ChangingResult<T>(Guid accountId, Task<T> work)
    {
        inboxCache?.Invalidate(accountId);
        return work;
    }

    private Task Changing(Guid accountId, Task work)
    {
        inboxCache?.Invalidate(accountId);
        return work;
    }

    // ---- GWS's inbox tabs (21-11-26; plan: mail-tabs-folders-rules-2026-11-21.md) ----
    // Each message's tab: the person's move ("you") or SentinelGPT's decision ("ai") first, then a
    // sender the person moved, then MailSorter's rules. A message the rules aren't sure of is kept
    // (MailTabSources.Unclear) for SentinelGPT and sits in the rules' best guess meanwhile.
    private sealed record Sorted(MailInboxItem Item, string TabKey, string Source, string Reason);

    private async Task<IReadOnlyList<Sorted>> SortInboxAsync(string username, Guid accountId, CancellationToken cancellationToken)
    {
        // The owner first, before the cache (21-11-26): a cached inbox must never answer for someone
        // else's account id. Someone else's mailbox finds nothing, as everywhere in Mail.
        if (await FindAsync(username, accountId, cancellationToken) is null) throw new MailException("Connect a mailbox first.");
        if (inboxCache is null || !inboxCache.TryGet(accountId, out var items))
        {
            items = await RunAsync(username, accountId, (backend, account) => backend.InboxForSortingAsync(account, MailTabs.SortWindow, cancellationToken), cancellationToken);
            inboxCache?.Set(accountId, items);
        }
        var keys = items.Select(item => item.MessageKey).Distinct().ToList();
        var stored = (await db.MailMessageTabs.Where(row => row.AccountId == accountId && keys.Contains(row.MessageKey)).ToListAsync(cancellationToken))
            .ToDictionary(row => row.MessageKey, StringComparer.Ordinal);
        var senders = (await db.MailSenderTabs.AsNoTracking().Where(row => row.AccountId == accountId).ToListAsync(cancellationToken))
            .ToDictionary(row => row.Sender, row => row.TabKey, StringComparer.OrdinalIgnoreCase);
        var now = timeProvider.GetUtcNow();
        var sorted = new List<Sorted>(items.Count);
        var added = false;
        foreach (var item in items)
        {
            var sender = item.Summary.From.Address.Trim().ToLowerInvariant();
            stored.TryGetValue(item.MessageKey, out var row);
            if (row is { Source: MailTabSources.You or MailTabSources.Ai or MailTabSources.Rule } && MailTabs.Find(row.TabKey) is not null)
            {
                sorted.Add(new Sorted(item, row.TabKey, row.Source, row.Reason));
                continue;
            }
            if (senders.TryGetValue(sender, out var senderTab) && MailTabs.Find(senderTab) is not null)
            {
                sorted.Add(new Sorted(item, senderTab, "sender", $"you moved mail from {sender} here"));
                continue;
            }
            var result = MailSorter.Classify(new MailSortInput(item.Summary.From.Address, item.Summary.From.Name, item.Summary.Subject, item.Headers));
            if (!result.Sure && row is null)
            {
                var unclear = new MailMessageTab
                {
                    AccountId = accountId, MessageKey = item.MessageKey, TabKey = result.TabKey, Source = MailTabSources.Unclear, Reason = result.Reason,
                    Sender = sender, Subject = item.Summary.Subject.Length > 300 ? item.Summary.Subject[..300] : item.Summary.Subject,
                    CreatedAt = now, CreatedBy = username
                };
                db.MailMessageTabs.Add(unclear);
                stored[item.MessageKey] = unclear;
                added = true;
            }
            sorted.Add(new Sorted(item, result.TabKey, result.Sure ? "rules" : MailTabSources.Unclear, result.Reason));
        }
        if (added) await db.SaveChangesAsync(cancellationToken);
        return sorted;
    }

    private async Task<MailPage> ListTabAsync(string username, Guid accountId, MailTabs.Tab tab, string? pageToken, CancellationToken cancellationToken)
    {
        var inTab = (await SortInboxAsync(username, accountId, cancellationToken)).Where(entry => entry.TabKey == tab.Key).Select(entry => entry.Item.Summary).ToList();
        var offset = int.TryParse(pageToken, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var start) ? Math.Max(0, start) : 0;
        var page = inTab.Skip(offset).Take(TabPageSize).ToList();
        return new MailPage(page, offset + TabPageSize < inTab.Count ? (offset + TabPageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private const int TabPageSize = 50;

    public async Task<IReadOnlyList<MailTabView>> ListTabsAsync(string username, Guid accountId, CancellationToken cancellationToken = default)
    {
        var sorted = await SortInboxAsync(username, accountId, cancellationToken);
        return MailTabs.All.Select(tab =>
        {
            var unread = sorted.Where(entry => entry.TabKey == tab.Key && entry.Item.Summary.Unread).ToList();
            var names = unread.Select(entry => string.IsNullOrWhiteSpace(entry.Item.Summary.From.Name) ? entry.Item.Summary.From.Address : entry.Item.Summary.From.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            return new MailTabView(tab.Key, unread.Count, names);
        }).ToList();
    }

    // Moves the message to the tab ("you"), and its sender too when asked: their mail goes there from now on.
    public async Task MoveToTabAsync(string username, Guid accountId, string messageId, string tabKey, bool rememberSender, CancellationToken cancellationToken = default)
    {
        if (MailTabs.Find(tabKey) is null) throw new MailException("Unknown tab.");
        var item = (await SortInboxAsync(username, accountId, cancellationToken)).FirstOrDefault(entry => entry.Item.Summary.Id == messageId)?.Item
            ?? throw new MailException("That message isn't in the inbox any more.");
        var now = timeProvider.GetUtcNow();
        var row = await db.MailMessageTabs.FirstOrDefaultAsync(r => r.AccountId == accountId && r.MessageKey == item.MessageKey, cancellationToken);
        if (row is null)
        {
            row = new MailMessageTab { AccountId = accountId, MessageKey = item.MessageKey, TabKey = tabKey, CreatedAt = now, CreatedBy = username };
            db.MailMessageTabs.Add(row);
        }
        row.TabKey = tabKey;
        row.Source = MailTabSources.You;
        row.Reason = "you moved it here";
        row.UpdatedAt = now;
        row.UpdatedBy = username;
        var sender = item.Summary.From.Address.Trim().ToLowerInvariant();
        if (rememberSender && sender.Length > 0)
        {
            var senderRow = await db.MailSenderTabs.FirstOrDefaultAsync(r => r.AccountId == accountId && r.Sender == sender, cancellationToken);
            if (senderRow is null) db.MailSenderTabs.Add(new MailSenderTab { AccountId = accountId, Sender = sender, TabKey = tabKey, CreatedAt = now, CreatedBy = username });
            else { senderRow.TabKey = tabKey; senderRow.UpdatedAt = now; senderRow.UpdatedBy = username; }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailSenderTabView>> ListSenderTabsAsync(string username, Guid accountId, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(username, accountId, cancellationToken) is null) return [];
        return (await db.MailSenderTabs.AsNoTracking().Where(row => row.AccountId == accountId).ToListAsync(cancellationToken))
            .OrderBy(row => row.Sender, StringComparer.OrdinalIgnoreCase).Select(row => new MailSenderTabView(row.Sender, row.TabKey)).ToList();
    }

    public async Task ForgetSenderTabAsync(string username, Guid accountId, string sender, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(username, accountId, cancellationToken) is null) return;
        var key = sender.Trim().ToLowerInvariant();
        if (await db.MailSenderTabs.FirstOrDefaultAsync(row => row.AccountId == accountId && row.Sender == key, cancellationToken) is { } row)
        {
            db.MailSenderTabs.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task<MailPage> ListAsync(string username, Guid accountId, string folder, string? search, string? pageToken, CancellationToken cancellationToken = default)
    {
        if (!MailFolders.IsKnown(folder)) throw new MailException("Unknown folder.");
        var query = search?.Trim();
        if (query is { Length: > MaxSearchLength }) query = query[..MaxSearchLength];
        // A tab is GWS's sorting of the inbox; a search inside a tab searches the whole inbox, as Gmail does.
        if (MailTabs.Find(folder) is { } tab)
        {
            if (string.IsNullOrWhiteSpace(query)) return ListTabAsync(username, accountId, tab, pageToken, cancellationToken);
            folder = MailFolders.Inbox;
        }
        return RunAsync(username, accountId, (backend, account) => backend.ListAsync(account, folder, query, pageToken, cancellationToken), cancellationToken);
    }

    public Task<MailMessageDetail> GetAsync(string username, Guid accountId, string messageId, bool showRemoteImages, CancellationToken cancellationToken = default) =>
        ChangingResult(accountId, RunAsync(username, accountId, async (backend, account) =>
        {
            var fetched = await backend.GetAsync(account, messageId, markRead: true, cancellationToken);
            return MailMessageRenderer.Render(messageId, fetched.ThreadId, fetched.Message, fetched.Unread, fetched.Starred, showRemoteImages);
        }, cancellationToken));

    public Task<MailAttachmentContent> GetAttachmentAsync(string username, Guid accountId, string messageId, int index, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, async (backend, account) =>
        {
            var message = (await backend.GetAsync(account, messageId, markRead: false, cancellationToken)).Message;
            var attachments = MailMessageRenderer.Attachments(message);
            if (index < 0 || index >= attachments.Count) throw new MailException("That attachment isn't there.");
            var part = attachments[index];
            using var stream = new MemoryStream();
            if (part.Content is not null) await part.Content.DecodeToAsync(stream, cancellationToken);
            return new MailAttachmentContent(MailMessageRenderer.FileName(part, index), part.ContentType.MimeType, stream.ToArray());
        }, cancellationToken);

    public Task SendAsync(string username, Guid accountId, MailDraft draft, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, async (backend, account) =>
        {
            var (message, threadId) = await BuildAsync(backend, account, draft, sending: true, cancellationToken);
            await backend.SendAsync(account, message, draft.Forward ? null : threadId, cancellationToken);
            if (draft.DraftId is { } draftId)
            {
                try
                {
                    await backend.DeleteDraftAsync(account, draftId, cancellationToken);
                }
                catch (MailException)
                {
                    throw new MailException("Sent - but its draft is still in Drafts; delete it there.");
                }
            }
            return true;
        }, cancellationToken);

    public Task<string> SaveDraftAsync(string username, Guid accountId, MailDraft draft, CancellationToken cancellationToken = default) =>
        RunAsync(username, accountId, async (backend, account) =>
        {
            var (message, threadId) = await BuildAsync(backend, account, draft, sending: false, cancellationToken);
            return await backend.SaveDraftAsync(account, message, draft.DraftId, draft.Forward ? null : threadId, cancellationToken);
        }, cancellationToken);

    public Task SetReadAsync(string username, Guid accountId, string messageId, bool read, CancellationToken cancellationToken = default) =>
        Changing(accountId, RunAsync(username, accountId, (backend, account) => backend.SetReadAsync(account, messageId, read, cancellationToken), cancellationToken));

    public Task SetStarredAsync(string username, Guid accountId, string messageId, bool starred, CancellationToken cancellationToken = default) =>
        Changing(accountId, RunAsync(username, accountId, (backend, account) => backend.SetStarredAsync(account, messageId, starred, cancellationToken), cancellationToken));

    public Task ArchiveAsync(string username, Guid accountId, string messageId, CancellationToken cancellationToken = default) =>
        Changing(accountId, RunAsync(username, accountId, (backend, account) => backend.ArchiveAsync(account, messageId, cancellationToken), cancellationToken));

    public Task TrashAsync(string username, Guid accountId, string messageId, CancellationToken cancellationToken = default) =>
        Changing(accountId, RunAsync(username, accountId, (backend, account) => backend.TrashAsync(account, messageId, cancellationToken), cancellationToken));

    // Each inbox counted on its own; one that can't be reached is left out rather than failing the rest.
    public Task<MailActionResult> ApplyAsync(string username, Guid accountId, IReadOnlyList<string> messageIds, string action,
        CancellationToken cancellationToken = default)
    {
        if (!MailActions.All.Contains(action)) throw new MailException("That isn't something Mail can do.");
        var ids = messageIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return Task.FromResult(new MailActionResult(0, null));
        if (ids.Count > MailLimits.MaxBulk) throw new MailException($"Mail can act on up to {MailLimits.MaxBulk} messages at a time.");
        inboxCache?.Invalidate(accountId);
        return RunAsync(username, accountId, async (backend, account) =>
            new MailActionResult(ids.Count, await backend.ApplyAsync(account, ids, action, cancellationToken)), cancellationToken);
    }

    public Task UndoAsync(string username, Guid accountId, MailUndo undo, CancellationToken cancellationToken = default) =>
        Changing(accountId, RunAsync(username, accountId, async (backend, account) =>
        {
            await backend.UndoAsync(account, undo, cancellationToken);
            return true;
        }, cancellationToken));

    public async Task<IReadOnlyDictionary<Guid, int>> GetUnreadByAccountAsync(string username, CancellationToken cancellationToken = default)
    {
        var ids = await db.MailAccounts.AsNoTracking().Where(account => account.Username == username).Select(account => account.Id).ToListAsync(cancellationToken);
        var counts = new Dictionary<Guid, int>();
        foreach (var id in ids)
        {
            try
            {
                counts[id] = await RunAsync(username, id, (backend, account) => backend.UnreadCountAsync(account, cancellationToken), cancellationToken);
            }
            catch (MailException)
            {
                // That mailbox's own page says what's wrong.
            }
        }
        return counts;
    }

    public async Task<int?> GetUnreadCountAsync(string username, CancellationToken cancellationToken = default)
    {
        var ids = await db.MailAccounts.AsNoTracking().Where(account => account.Username == username).Select(account => account.Id).ToListAsync(cancellationToken);
        if (ids.Count == 0) return null;
        var total = 0;
        foreach (var id in ids)
        {
            try
            {
                total += await RunAsync(username, id, (backend, account) => backend.UnreadCountAsync(account, cancellationToken), cancellationToken);
            }
            catch (MailException)
            {
                // That mailbox's own page says what's wrong.
            }
        }
        return total;
    }

    // The message to send or keep as a draft. A reply threads on the original; editing a draft keeps
    // the threading the draft already had; a forward carries the original's attachments.
    private async Task<(MimeMessage Message, string? ThreadId)> BuildAsync(IMailboxBackend backend, MailAccount account, MailDraft draft, bool sending, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.DisplayName, account.Address));
        AddRecipients(message.To, draft.To);
        AddRecipients(message.Cc, draft.Cc);
        AddRecipients(message.Bcc, draft.Bcc);
        var recipients = message.To.Count + message.Cc.Count + message.Bcc.Count;
        if (sending && recipients == 0) throw new MailException("Add who it's to.");
        if (recipients > MaxRecipients) throw new MailException($"At most {MaxRecipients} recipients.");
        if (draft.Body.Length > MaxBodyLength) throw new MailException("That message is too long.");
        var uploads = draft.Attachments ?? [];
        if (uploads.Count > MailLimits.MaxAttachments) throw new MailException($"At most {MailLimits.MaxAttachments} attachments.");
        if (uploads.Sum(file => (long)file.Content.Length) > MailLimits.MaxAttachmentBytes)
            throw new MailException($"Attachments can add up to {MailLimits.MaxAttachmentBytes / (1024 * 1024)} MB.");
        message.Subject = draft.Subject.Trim();
        MimeEntity body = new MultipartAlternative
        {
            new TextPart("plain") { Text = draft.Body },
            new TextPart("html") { Text = MailFormatting.ToHtml(draft.Body) }
        };
        var attachments = uploads.Select(Attachment).ToList();

        string? threadId = null;
        var threadFrom = draft.ReplyToMessageId ?? draft.DraftId;
        if (!string.IsNullOrEmpty(threadFrom))
        {
            var original = await backend.GetAsync(account, threadFrom, markRead: false, cancellationToken);
            threadId = original.ThreadId;
            if (draft.ReplyToMessageId is null)
            {
                // Editing a draft: the same threading headers, and the files it already had.
                message.InReplyTo = original.Message.InReplyTo;
                foreach (var reference in original.Message.References) message.References.Add(reference);
                attachments.InsertRange(0, MailMessageRenderer.Attachments(original.Message));
            }
            else if (!draft.Forward && original.Message.MessageId is { } replyingTo)
            {
                message.InReplyTo = replyingTo;
                foreach (var reference in original.Message.References) message.References.Add(reference);
                message.References.Add(replyingTo);
            }
            if (draft.Forward && draft.ReplyToMessageId is not null) attachments.InsertRange(0, MailMessageRenderer.Attachments(original.Message));
        }
        if (attachments.Count > 0)
        {
            var mixed = new Multipart("mixed") { body };
            foreach (var attachment in attachments) mixed.Add(attachment);
            body = mixed;
        }
        message.Body = body;
        return (message, threadId);
    }

    private Task RunAsync(string username, Guid accountId, Func<IMailboxBackend, MailAccount, Task> work, CancellationToken cancellationToken) =>
        RunAsync(username, accountId, async (backend, account) =>
        {
            await work(backend, account);
            return true;
        }, cancellationToken);

    // Runs one mailbox operation; the account's LastError follows the outcome (written only when it changes).
    private async Task<T> RunAsync<T>(string username, Guid accountId, Func<IMailboxBackend, MailAccount, Task<T>> work, CancellationToken cancellationToken)
    {
        var account = await FindAsync(username, accountId, cancellationToken) ?? throw new MailException("Connect a mailbox first.");
        await MoveFirstVersionSignInAsync(account, cancellationToken);
        var signInBefore = account.TokenProtected;
        string? failure = null;
        try
        {
            return await work(Backend(account.Provider), account);
        }
        catch (MailException ex)
        {
            failure = ex.Message;
            throw;
        }
        catch (Exception ex) when (ex is ServiceNotConnectedException or ServiceNotAuthenticatedException or ProtocolException or CommandException
                                       or AuthenticationException or SslHandshakeException or FolderNotFoundException or MessageNotFoundException
                                       or IOException or HttpRequestException or System.Net.Sockets.SocketException or TimeoutException
                                       or System.Text.Json.JsonException)
        {
            failure = $"The mail server said: {ex.Message}";
            // Logged (21-11-26): a Gmail MOVE failure reached the screen and left no trace, because
            // the next good action clears the account's LastError.
            logger?.LogWarning(ex, "Mail action failed on {Host} ({Provider}): {Message}", account.ImapHost, account.Provider, ex.Message);
            throw new MailException(failure);
        }
        finally
        {
            var changed = account.TokenProtected != signInBefore; // a renewed Google sign-in
            if (account.LastError != failure && failure is not "That message is gone.")
            {
                account.LastError = failure;
                if (failure is null) account.LastUsedAt = timeProvider.GetUtcNow();
                changed = true;
            }
            if (changed) await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    // Only the person's own: someone else's account id finds nothing.
    private Task<MailAccount?> FindAsync(string username, Guid accountId, CancellationToken cancellationToken) =>
        db.MailAccounts.FirstOrDefaultAsync(item => item.Id == accountId && item.Username == username, cancellationToken);

    private async Task RequireRoomAsync(string username, CancellationToken cancellationToken)
    {
        if (await db.MailAccounts.CountAsync(account => account.Username == username, cancellationToken) >= MailLimits.MaxAccounts)
            throw new MailException($"You can connect up to {MailLimits.MaxAccounts} mailboxes - remove one first.");
    }

    private IMailboxBackend Backend(string provider) =>
        backends.FirstOrDefault(backend => backend.Provider == provider) ?? throw new MailException($"Mail can't use '{provider}' accounts.");

    private static void AddRecipients(InternetAddressList list, IReadOnlyList<string> entries)
    {
        foreach (var entry in entries.SelectMany(entry => entry.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!MailboxAddress.TryParse(entry, out var address) || !address.Address.Contains('@'))
                throw new MailException($"'{entry}' isn't an email address.");
            list.Add(address);
        }
    }

    private static MimePart Attachment(MailAttachmentUpload file)
    {
        var name = Path.GetFileName(file.FileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) name = "attachment";
        if (!ContentType.TryParse(string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, out var type))
            type = new ContentType("application", "octet-stream");
        return new MimePart(type)
        {
            Content = new MimeContent(new MemoryStream(file.Content)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = name
        };
    }

    private static string Host(string? value, string what)
    {
        var host = (value ?? string.Empty).Trim();
        if (host.Length is 0 or > 200 || host.Contains(' ') || host.Contains('/')) throw new MailException($"Enter the {what}.");
        return host;
    }

    // The first version (31-10-26) kept a Gmail sign-in as the Automation credential "Mail -
    // <username>", where every admin could use it. It moves onto the account, and the credential goes.
    private async Task MoveFirstVersionSignInAsync(MailAccount account, CancellationToken cancellationToken)
    {
        if (account is not { Provider: MailProviders.Google, TokenProtected.Length: 0, CredentialId: { } credentialId }) return;
        if (await db.AutomationCredentials.FirstOrDefaultAsync(item => item.Id == credentialId, cancellationToken) is { } credential)
        {
            account.TokenProtected = secretProtector.Protect(secretProtector.Unprotect(credential.ProtectedData));
            db.AutomationCredentials.Remove(credential);
        }
        account.CredentialId = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RemoveCredentialAsync(Guid? credentialId, CancellationToken cancellationToken)
    {
        if (credentialId is not { } id) return;
        if (await db.AutomationCredentials.FirstOrDefaultAsync(item => item.Id == id, cancellationToken) is { } credential)
            db.AutomationCredentials.Remove(credential);
    }

    private static MailAccountView ToView(MailAccount account) =>
        new(account.Id, account.Provider, account.Address, account.DisplayName, account.LastError, account.LastUsedAt,
            HasInboxTabs: true, Signature: account.Signature);
}
