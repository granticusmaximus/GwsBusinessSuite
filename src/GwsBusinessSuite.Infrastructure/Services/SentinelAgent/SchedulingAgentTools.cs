using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Scheduling;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

public sealed class SchedulingAgentTools(IBookingService bookings) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin];
    public SentinelAgentArea Area { get; } = new("scheduling", "booking types, appointments and availability", ["booking", "appointment", "schedule", "availability", "meeting"]);
    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool("list_booking_types", "List booking types, including inactive ones.", SentinelAgentJson.Schema(), SentinelAgentRisk.Read, Roles,
            _ => "Listing booking types", ListTypesAsync),
        new SentinelAgentTool("list_bookings", "List appointments, optionally for one booking type.", SentinelAgentJson.Schema(("bookingTypeId", "string", "Optional booking type id", false)), SentinelAgentRisk.Read, Roles,
            _ => "Listing bookings", ListBookingsAsync),
        new SentinelAgentTool("create_booking_type", "Create a booking type. Availability can be configured later in the scheduling editor.", TypeSchema(includeId: false), SentinelAgentRisk.Change, Roles,
            args => $"Creating booking type '{args.OptionalText("title")}'", CreateTypeAsync, UndoCreateTypeAsync),
        new SentinelAgentTool("update_booking_type", "Update a booking type while preserving its existing weekly availability.", TypeSchema(includeId: true), SentinelAgentRisk.Change, Roles,
            args => $"Updating booking type '{args.OptionalText("title")}'", UpdateTypeAsync, UndoUpdateTypeAsync),
        new SentinelAgentTool("delete_booking_type", "Permanently delete a booking type and its bookings. Nothing happens until Confirm.", SentinelAgentJson.Schema(("bookingTypeId", "string", "The booking type id", true)), SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to delete a booking type", DeleteTypeAsync,
            preview: async (args, _, ct) => $"Permanently delete booking type '{(await TypeAsync(args.Id("bookingTypeId"), ct)).Title}' and its bookings"),
        new SentinelAgentTool("cancel_booking", "Cancel an appointment and send its cancellation email. Nothing happens until Confirm.", SentinelAgentJson.Schema(("bookingId", "string", "The booking id", true)), SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to cancel a booking", CancelAsync,
            preview: async (args, _, ct) => { var row = await BookingAsync(args.Id("bookingId"), ct); return $"Cancel {row.BookingTypeTitle} with {row.AttendeeName} at {row.StartsAt:u}"; })
    ];

    private static string TypeSchema(bool includeId)
    {
        var fields = new List<(string, string, string, bool)>();
        if (includeId) fields.Add(("bookingTypeId", "string", "The booking type id", true));
        fields.AddRange([("title", "string", "Title", true), ("slug", "string", "Public address slug", false), ("description", "string", "Description", false),
            ("durationMinutes", "number", "Duration in minutes", false), ("bufferMinutes", "number", "Buffer between bookings", false), ("ownerUsername", "string", "Owner username", false),
            ("timeZoneId", "string", "IANA time zone", false), ("daysAhead", "number", "How far ahead people can book", false), ("minNoticeMinutes", "number", "Minimum notice", false),
            ("maxPerDay", "number", "Maximum bookings per day; zero is unlimited", false), ("active", "boolean", "Whether it is publicly bookable", false)]);
        return SentinelAgentJson.Schema([.. fields]);
    }
    private async Task<SentinelAgentToolResult> ListTypesAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct) => SentinelAgentToolResult.Of(new { bookingTypes = await bookings.ListBookingTypesAsync(true, ct) });
    private async Task<SentinelAgentToolResult> ListBookingsAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct) => SentinelAgentToolResult.Of(new { bookings = await bookings.ListBookingsAsync(args.OptionalId("bookingTypeId"), ct) });
    private async Task<SentinelAgentToolResult> CreateTypeAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var saved = await bookings.SaveBookingTypeAsync(Editor(args, null), context.User.Username, ct);
        return SentinelAgentToolResult.Of(saved, Change($"Created booking type '{saved.Title}'", saved, SentinelAgentJson.Serialize(new { saved.Id })));
    }
    private async Task UndoCreateTypeAsync(string json, SentinelAgentToolContext context, CancellationToken ct) => await bookings.DeleteBookingTypeAsync(SentinelAgentJson.Deserialize<IdUndo>(json).Id, ct);
    private async Task<SentinelAgentToolResult> UpdateTypeAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var before = await TypeAsync(args.Id("bookingTypeId"), ct); var saved = await bookings.SaveBookingTypeAsync(Editor(args, before), context.User.Username, ct);
        return SentinelAgentToolResult.Of(saved, Change($"Updated booking type '{saved.Title}'", saved, SentinelAgentJson.Serialize(ToEditor(before))));
    }
    private async Task UndoUpdateTypeAsync(string json, SentinelAgentToolContext context, CancellationToken ct) => _ = await bookings.SaveBookingTypeAsync(SentinelAgentJson.Deserialize<BookingTypeEditorModel>(json), context.User.Username, ct);
    private async Task<SentinelAgentToolResult> DeleteTypeAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var row = await TypeAsync(args.Id("bookingTypeId"), ct); await bookings.DeleteBookingTypeAsync(row.Id, ct);
        return SentinelAgentToolResult.Of(new { deleted = row.Title }, new SentinelAgentChange($"Deleted booking type '{row.Title}'", "booking-type", row.Id.ToString(), row.Title, "/admin/scheduling", null));
    }
    private async Task<SentinelAgentToolResult> CancelAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var row = await BookingAsync(args.Id("bookingId"), ct);
        if (!await bookings.CancelBookingAsync(row.Id, context.User.Username, ct)) throw new SentinelAgentToolException("That booking could not be cancelled.");
        return SentinelAgentToolResult.Of(new { cancelled = row.Id }, new SentinelAgentChange($"Cancelled {row.BookingTypeTitle} with {row.AttendeeName}", "booking", row.Id.ToString(), row.AttendeeName, "/admin/scheduling", null));
    }
    private async Task<BookingTypeView> TypeAsync(Guid id, CancellationToken ct) => (await bookings.ListBookingTypesAsync(true, ct)).FirstOrDefault(row => row.Id == id) ?? throw new SentinelAgentToolException($"There's no booking type with id {id}.");
    private async Task<BookingView> BookingAsync(Guid id, CancellationToken ct) => (await bookings.ListBookingsAsync(cancellationToken: ct)).FirstOrDefault(row => row.Id == id) ?? throw new SentinelAgentToolException($"There's no booking with id {id}.");
    private static BookingTypeEditorModel Editor(JsonObject args, BookingTypeView? before) => new()
    {
        Id = before?.Id, Title = args.Text("title"), Slug = args.OptionalText("slug") ?? before?.Slug ?? string.Empty, Description = args.OptionalText("description") ?? before?.Description ?? string.Empty,
        DurationMinutes = (int)(args.OptionalNumber("durationMinutes") ?? before?.DurationMinutes ?? 30), BufferMinutes = (int)(args.OptionalNumber("bufferMinutes") ?? before?.BufferMinutes ?? 0),
        OwnerUsername = args.OptionalText("ownerUsername") ?? before?.OwnerUsername, IsActive = args.Flag("active", before?.IsActive ?? true), Availability = before?.Availability.ToList() ?? [],
        TimeZoneId = args.OptionalText("timeZoneId") ?? before?.TimeZoneId ?? string.Empty, DaysAhead = (int)(args.OptionalNumber("daysAhead") ?? before?.DaysAhead ?? 14),
        MinNoticeMinutes = (int)(args.OptionalNumber("minNoticeMinutes") ?? before?.MinNoticeMinutes ?? 60), MaxPerDay = (int)(args.OptionalNumber("maxPerDay") ?? before?.MaxPerDay ?? 0)
    };
    private static BookingTypeEditorModel ToEditor(BookingTypeView row) => new() { Id = row.Id, Title = row.Title, Slug = row.Slug, Description = row.Description, DurationMinutes = row.DurationMinutes, BufferMinutes = row.BufferMinutes, OwnerUsername = row.OwnerUsername, IsActive = row.IsActive, Availability = row.Availability.ToList(), TimeZoneId = row.TimeZoneId, DaysAhead = row.DaysAhead, MinNoticeMinutes = row.MinNoticeMinutes, MaxPerDay = row.MaxPerDay };
    private static SentinelAgentChange Change(string summary, BookingTypeView row, string undo) => new(summary, "booking-type", row.Id.ToString(), row.Title, "/admin/scheduling", undo);
    private sealed record IdUndo(Guid Id);
}
