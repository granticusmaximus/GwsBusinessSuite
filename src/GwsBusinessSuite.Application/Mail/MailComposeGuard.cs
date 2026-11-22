namespace GwsBusinessSuite.Application.Mail;

// Small, deterministic compose safeguards shared by the Blazor UI and its tests. The check is
// intentionally conservative: it catches explicit attachment wording without blocking ordinary
// uses of words such as "attach" in an unrelated sentence.
public static class MailComposeGuard
{
    private static readonly string[] AttachmentPhrases =
    [
        "attached", "attachment", "attachments", "I've attached", "I have attached", "see attached", "find attached"
    ];

    public static bool MentionsAttachment(string? subject, string? body)
    {
        var text = $"{subject}\n{body}";
        return AttachmentPhrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    public static string WithSignature(string body, string? signature)
    {
        var clean = signature?.Trim();
        if (string.IsNullOrEmpty(clean)) return body;
        return string.IsNullOrWhiteSpace(body) ? $"\n\n-- \n{clean}" : $"{body.TrimEnd()}\n\n-- \n{clean}";
    }
}
