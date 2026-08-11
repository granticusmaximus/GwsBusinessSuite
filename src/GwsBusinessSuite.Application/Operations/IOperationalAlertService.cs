namespace GwsBusinessSuite.Application.Operations;

// The only way a background job/backup/automation failure reaches a human today is the
// in-app notification bell (DockerHealthNotifier) or the log file - neither pages anyone who
// doesn't already have the app open. This is the "someone gets an email" half of that gap,
// deliberately narrow in scope: fire-and-forget best-effort notification, never something a
// caller should let block or fail the actual operation it's reporting on.
public interface IOperationalAlertService
{
    // source identifies the failing subsystem (e.g. "automation-resume-sweep",
    // "database-backup") - repeated calls with the same source are throttled so a
    // persistently failing job pages once per cooldown window, not once per tick.
    Task NotifyFailureAsync(string source, string summary, Exception? exception = null, CancellationToken cancellationToken = default);
}
