using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Mail;

// What the sorter reads of a message: its sender, subject and headers (no body).
public sealed record MailSortInput(string FromAddress, string FromName, string Subject, IReadOnlyDictionary<string, string> Headers);

// The tab GWS puts a message in, whether the rules are sure, and why (shown on hover, and to the AI
// when they aren't sure).
public sealed record MailSortResult(string TabKey, bool Sure, string Reason);

// GWS's own inbox sorting (21-11-26, Grant: "create the same function of moving the appropriate mail
// items to those respective tabs in correct filtering"). Rules first: each signal adds to a tab's
// score; the top tab wins. The rules are sure when the winner is clear (at least SureScore, and
// SureMargin ahead of the next); otherwise the message is "unclear", stays in its best guess, and
// SentinelGPT decides it later (plan: mail-tabs-folders-rules-2026-11-21.md, phase 3).
public static partial class MailSorter
{
    public const int SureScore = 4;
    public const int SureMargin = 2;

    private const string Primary = "inbox:primary";
    private const string Promotions = "inbox:promotions";
    private const string Social = "inbox:social";
    private const string Updates = "inbox:updates";
    private const string Forums = "inbox:forums";

    // Social networks' sending domains (a subdomain counts: mail.instagram.com).
    private static readonly string[] SocialDomains =
    [
        "facebookmail.com", "facebook.com", "linkedin.com", "x.com", "twitter.com", "instagram.com", "pinterest.com",
        "tiktok.com", "redditmail.com", "reddit.com", "nextdoor.com", "threads.net", "snapchat.com", "tumblr.com",
        "quora.com", "meetup.com", "bsky.app", "mastodon.social", "youtube.com", "discord.com", "strava.com"
    ];

    // Discussion lists (Forums), told apart from marketing lists, which also carry List-Id.
    private static readonly string[] ForumHosts = ["googlegroups.com", "groups.io", "discoursemail.com", "discourse", "mailman", "lists.", "listserv", "freelists.org", "topicbox.com"];

    // Bulk senders' own headers: a marketing platform sent it.
    private static readonly string[] BulkHeaders =
    [
        "Feedback-ID", "X-Mailchimp-Campaign", "X-MC-User", "X-SG-EID", "X-Mailgun-Tag", "X-Campaign", "X-Campaign-Id",
        "X-MarketoID", "X-SFMC-Stack", "X-CSA-Complaints", "X-Klaviyo-Message-Id", "X-Braze-Campaign", "X-Iterable-Campaign"
    ];

    private static readonly string[] AutomatedLocalParts =
    [
        "no-reply", "noreply", "donotreply", "do-not-reply", "notifications", "notification", "notify", "alerts", "alert",
        "account", "accounts", "billing", "receipts", "receipt", "orders", "order", "shipping", "auto-confirm", "security",
        "support", "updates", "mailer-daemon", "postmaster", "calendar-notification", "invoice", "payments"
    ];

    private static readonly string[] MarketingLocalParts = ["news", "newsletter", "marketing", "offers", "deals", "promo", "promotions", "hello", "shop", "store", "sales", "email"];

    // The headers Classify reads; backends fetch these and no others.
    public static readonly IReadOnlyList<string> HeaderNames =
        ["List-Id", "List-Post", "List-Unsubscribe", "Precedence", "Auto-Submitted", .. BulkHeaders];

    public static MailSortResult Classify(MailSortInput input)
    {
        var scores = new Dictionary<string, int> { [Primary] = 0, [Promotions] = 0, [Social] = 0, [Updates] = 0, [Forums] = 0 };
        var reasons = new Dictionary<string, List<string>>();
        void Add(string tab, int points, string why)
        {
            scores[tab] += points;
            (reasons.TryGetValue(tab, out var list) ? list : reasons[tab] = []).Add(why);
        }

        var address = input.FromAddress.Trim().ToLowerInvariant();
        var at = address.LastIndexOf('@');
        var local = at > 0 ? address[..at] : address;
        var domain = at > 0 ? address[(at + 1)..] : string.Empty;
        var subject = input.Subject ?? string.Empty;
        string? Header(string name) => input.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        var listId = Header("List-Id")?.ToLowerInvariant();
        var precedence = Header("Precedence")?.ToLowerInvariant();
        var autoSubmitted = Header("Auto-Submitted")?.ToLowerInvariant();
        var bulk = BulkHeaders.FirstOrDefault(name => Header(name) is not null);

        if (SocialDomains.Any(social => domain == social || domain.EndsWith("." + social, StringComparison.Ordinal)))
            Add(Social, 5, $"from {domain}, a social network");

        if (Header("List-Post") is not null) Add(Forums, 5, "a discussion list (List-Post)");
        if (listId is not null && ForumHosts.Any(host => listId.Contains(host, StringComparison.Ordinal))) Add(Forums, 4, "a discussion group's list");
        if (precedence == "list") Add(Forums, 1, "Precedence: list");

        if (autoSubmitted is not null && autoSubmitted != "no") Add(Updates, 3, "sent automatically (Auto-Submitted)");
        if (AutomatedLocalParts.Any(part => local == part || local.StartsWith(part + "+", StringComparison.Ordinal) || local.StartsWith(part + ".", StringComparison.Ordinal)))
            Add(Updates, 2, $"an automated sender ({local}@)");
        if (TransactionalWords().IsMatch(subject)) Add(Updates, 3, "a receipt, order, account or security notice");

        if (bulk is not null) Add(Promotions, 2, $"sent by a mailing platform ({bulk})");
        if (precedence == "bulk") Add(Promotions, 1, "Precedence: bulk");
        if (Header("List-Unsubscribe") is not null) Add(Promotions, 2, "has an unsubscribe link");
        if (MarketingLocalParts.Contains(local)) Add(Promotions, 2, $"a marketing sender ({local}@)");
        if (PromoWords().IsMatch(subject)) Add(Promotions, 3, "sale or offer wording");

        // A person writing: no list, bulk or automation signals at all.
        var automated = bulk is not null || listId is not null || Header("List-Unsubscribe") is not null || precedence is "bulk" or "list"
                        || autoSubmitted is not null && autoSubmitted != "no" || scores[Updates] > 0 || scores[Social] > 0;
        if (!automated) Add(Primary, 4, "a person writing, no list or automated headers");
        if (ReplyPrefix().IsMatch(subject)) Add(Primary, 2, "a reply or forward");

        var ranked = scores.OrderByDescending(pair => pair.Value).ThenBy(pair => TabOrder(pair.Key)).ToList();
        var (best, top) = (ranked[0].Key, ranked[0].Value);
        if (top == 0) return new MailSortResult(Primary, false, "no clear signals");
        // A subject that reads as both a receipt and an offer ("Order confirmation - save 10% next time")
        // is the AI's to decide, however the points fall.
        var conflicting = TransactionalWords().IsMatch(subject) && PromoWords().IsMatch(subject);
        var sure = !conflicting && top >= SureScore && top - ranked[1].Value >= SureMargin;
        return new MailSortResult(best, sure, string.Join("; ", reasons[best]));
    }

    private static int TabOrder(string key) => key switch { Primary => 0, Updates => 1, Promotions => 2, Social => 3, _ => 4 };

    [GeneratedRegex(@"\b(receipt|order\s*(#|no\.?|number|confirm\w*|has shipped|shipped)|your order|shipped|shipping (update|confirmation)|out for delivery|delivered|tracking|invoice|statement|payment (received|due|confirmation|failed)|bill(ing)? (is ready|due)|password|verification code|verify your|one-time|security alert|sign-?in (attempt|alert)|new sign-?in|login (alert|attempt)|appointment|reservation|booking|itinerary|confirmation|reminder|account (update|alert|notice))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransactionalWords();

    [GeneratedRegex(@"(\d+\s?%\s?off|\bsale\b|\bdeals?\b|\bsave (up to|\$|\d)|\bcoupon|\bpromo( code)?\b|\bfree shipping\b|\blimited[- ]time\b|\bexclusive\b|\bnew arrivals\b|\bdiscount|\bshop now\b|\bbogo\b|\bclearance\b|\bflash sale\b|\bblack friday\b|\bcyber monday\b|\bwebinar\b|\blast chance\b|\bends (today|tonight|soon)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PromoWords();

    [GeneratedRegex(@"^\s*(re|fw|fwd|aw|sv|vs)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplyPrefix();
}
