using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Infrastructure.Bookings;
using Infrastructure.Leads;
using Shared.Analytics;

namespace Infrastructure.Reception;

internal sealed partial class ReceptionistApplicationSession
{
    private readonly IReceptionistApplicationBooking? booking;
    private bool bookingRequested;
    private bool bookingIntakeReady;
    private string? bookingServiceKey;
    private string? requestedBookingDetail;
    private IReadOnlyList<ReceptionistBookingOption> bookingOptions = [];
    private PreparedBooking? preparedBooking;
    private ReceptionistBookingResult? bookingResult;
    private bool BookingAvailable => booking is not null &&
        ReceptionistConfigurationCodec.BookingEnabled(call.Configuration, call.Binding, call.ControlledTest);
    private bool BookingForService(string key) => BookingAvailable &&
        ReceptionistConfigurationCodec.BookingEnabled(call.Configuration, call.Binding, call.ControlledTest, key);

    private static bool BookingIntent(string text) => !Regex.IsMatch(text, @"\b(?:don't|do not|cancel)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        Regex.IsMatch(text, @"\b(?:book|schedule|arrange)\b.*\b(?:appointment|meeting|consultation)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private string ExpandRequestedBookingDetail(string text)
    {
        if (requestedBookingDetail == "name" && Regex.IsMatch(text, @"^[\p{L}'-]+(?:\s+[\p{L}'-]+){0,5}[.!]?$", RegexOptions.CultureInvariant) &&
            !Regex.IsMatch(text, @"\b(?:yes|no|not|but|how|what|why|when|where|please|want|need|don't|do|I|my|is)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "My name is " + text.TrimEnd('.', '!') + ".";
        if (requestedBookingDetail == "email" && Regex.IsMatch(text, @"^[^\s@]+@[^\s@]+\.[^\s@]+[.!]?$", RegexOptions.CultureInvariant))
            return "My email is " + text.TrimEnd('.', '!') + ".";
        return text;
    }

    private void RememberBookingScope(ConfirmedReceptionistInquiry facts)
    {
        lock (gate)
        {
            bookingServiceKey = facts.ServiceKey;
            bookingIntakeReady = (facts.CallerName is not null || facts.FirstName is not null) && facts.Email is not null;
        }
    }
    private string BookingAfterSavePrompt() => bookingRequested && bookingIntakeReady && bookingServiceKey is not null && BookingForService(bookingServiceKey)
        ? " What day would you like for the " + (call.ControlledTest ? "synthetic booking check" : "appointment") + "? You can say today, tomorrow, a weekday or a date."
        : string.Empty;

    private void ObserveBookingInput(int start)
    {
        if (preparedBooking is { } pending && (!pending.Acknowledged || start <= pending.AcknowledgedEndMs || Correction(input.ToString())))
            preparedBooking = null;
    }
    private void ObserveBookingAcknowledgement(JsonElement message, string type)
    {
        if (preparedBooking is not { } pending || !String(message, "client_event_id", out var id) || !pending.Acknowledgements.ContainsKey(id)) return;
        if (type == "error") { preparedBooking = null; return; }
        if (type != "session.commentary.appended") return;
        if (!Timeline(message, out _, out var end)) { preparedBooking = null; return; }
        pending.Acknowledgements[id] = true; pending.AcknowledgedEndMs = Math.Max(pending.AcknowledgedEndMs, end);
        pending.Acknowledged = pending.Acknowledgements.Values.All(x => x);
    }

    private async Task<IReadOnlyList<ReceptionistApplicationUpdate>> HandleSavedBookingAsync(string delegationId,
        string text, long version, int start, int end, CancellationToken ct)
    {
        if (bookingResult is { } result)
            return Say(delegationId, BookingResultMessage(result));
        if (!BookingAvailable)
            return Say(delegationId, SavedMessage(saved!) + " I can't change that completed result or book an appointment in this call.");
        if (bookingServiceKey is not null && !BookingForService(bookingServiceKey))
            return Say(delegationId, SavedMessage(saved!) + " Booking is not available for this service in this call.");
        if (Sensitive(text)) return Say(delegationId, "Please don't provide passwords, payment information or verification codes. I can only help with this confirmed service request.");
        if (Correction(text) && (ContactDetail(text) || text.Contains('@') || Regex.IsMatch(text, @"\b\d[\d\s().+-]{5,}\d\b")) ||
            ContactDetail(text) && Regex.IsMatch(text,
            @"\b(?:email|e-mail|name|phone|number) (?:is|should be)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            lock (gate) { Invalidate(); bookingOptions = []; bookingIntakeReady = false; bookingRequested = false; }
            return Say(delegationId, "I won't use the saved contact information to create an appointment. I can't change that completed intake within this call; the correction needs the team's review.");
        }
        if (Regex.IsMatch(ReceptionistApplicationCatalog.Normalize(text), @"^(?:no|no thanks|cancel|stop)$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(text, @"\b(?:don't|do not|no|without|cancel|stop)\b.*\b(?:email|invitation|book|appointment|meeting)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            lock (gate) { Invalidate(); bookingOptions = []; bookingRequested = false; }
            return Say(delegationId, "I won't create an appointment or send an invitation. The intake result already confirmed remains unchanged.");
        }
        if (!bookingIntakeReady || bookingServiceKey is null)
            return Say(delegationId, SavedMessage(saved!) + " Booking needs a name and invitation email confirmed in this call's intake. I can't add those to an already completed intake or retrieve them into this conversation.");
        bookingRequested |= BookingIntent(text);
        if (!bookingRequested) return Say(delegationId, SavedMessage(saved!) + " If you want an appointment, please ask me to check one for this request.");
        PreparedBooking? confirmation;
        lock (gate) confirmation = preparedBooking;
        if (confirmation is not null)
        {
            var explicitConsent = !text.TrimEnd().EndsWith("?", StringComparison.Ordinal) && Regex.IsMatch(ReceptionistApplicationCatalog.Normalize(text),
                @"^(?:yes,?\s+)?(?:please\s+)?book (?:(?:that|this|the) appointment|it) and (?:send (?:the|my|an) email invitation|send me (?:the|an) (?:email )?invitation|email me (?:the|an) invitation)$", RegexOptions.CultureInvariant);
            if (explicitConsent && confirmation.Acknowledged && start > confirmation.AcknowledgedEndMs)
                return await CommitBookingAsync(delegationId, confirmation, text, version, start, end, ct);
            lock (gate) preparedBooking = null;
            if (IsBriefConfirmation(text) || explicitConsent)
                return PrepareBookingConfirmation(delegationId, confirmation.Option,
                    "Nothing has been booked. Please review the appointment and explicitly confirm both booking and the email invitation.");
        }
        if (bookingOptions.Count > 0 && TrySlotChoice(text, out var choice) && choice >= 1 && choice <= bookingOptions.Count)
            return PrepareBookingConfirmation(delegationId, bookingOptions[choice - 1], null);
        var service = call.Configuration.BookingPolicy!.Services.SingleOrDefault(x => x.ServiceKey == bookingServiceKey);
        if (service is null) return Say(delegationId, "Booking is not available for this service in this call.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(service.TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        if (!TryBookingDate(text, today, out var date))
            return Say(delegationId, bookingOptions.Count > 0 ? "Please choose one of the numbered options, or give a different day. Nothing has been booked."
                : "What day would you like? I can check today, tomorrow, a weekday or a specific date in " + service.TimeZoneId + ".");
        try
        {
            var options = await booking!.PrepareAsync(call, saved!.ActionId, bookingServiceKey, date, leaseToken, ct);
            lock (gate)
            {
                if (disposed || inputVersion != version) return Say(delegationId, "I heard an update. Please finish it before choosing an appointment.");
                bookingOptions = options; preparedBooking = null;
            }
            if (options.Count == 0) return Say(delegationId, "I couldn't verify an option for that day. Would you like me to check another day?");
            var prefix = call.ControlledTest ? "These are synthetic test options, not live calendar availability. " : "The currently available options are: ";
            return Say(delegationId, prefix + string.Join(" ", options.Select((x, i) => $"Option {i + 1}: {SlotLabel(x)}.")) + " Which option works for you?");
        }
        catch (Exception error) when (error is not OutOfMemoryException && !(error is OperationCanceledException && ct.IsCancellationRequested))
        { return Say(delegationId, "I can't verify booking readiness for this request right now. Nothing new is confirmed; the existing intake result remains unchanged."); }
    }

    private IReadOnlyList<ReceptionistApplicationUpdate> PrepareBookingConfirmation(string delegationId, ReceptionistBookingOption option, string? introduction)
    {
        var message = (introduction is null ? "" : introduction + " ") +
            (option.Synthetic ? "Synthetic booking check: " : "Please confirm: ") + option.PublicServiceLabel + ", " + SlotLabel(option) +
            $", for {(option.EndUtc - option.StartUtc).TotalMinutes:0} minutes. " +
            (option.Synthetic ? "This checks your appointment and email-invitation consent without creating or sending either. " : "This creates the appointment and sends its invitation to the email you confirmed in your intake. ") +
            "If that is correct, say 'Book that appointment and send the email invitation', or tell me what to change.";
        var updates = Say(delegationId, message);
        lock (gate) preparedBooking = new(option, updates.ToDictionary(x => x.EventId, _ => false, StringComparer.Ordinal));
        return updates;
    }

    private async Task<IReadOnlyList<ReceptionistApplicationUpdate>> CommitBookingAsync(string delegationId,
        PreparedBooking confirmation, string text, long version, int start, int end, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(350), clock, ct);
        CancellationTokenSource effect;
        lock (gate)
        {
            if (disposed || preparedBooking != confirmation || inputVersion != version)
                return Say(delegationId, "I heard a correction before booking. Please finish the corrected appointment request.");
            effect = CancellationTokenSource.CreateLinkedTokenSource(ct); activeIntent = effect;
        }
        try
        {
            var receipt = "booking-consent:v1:" + ReceptionistConfigurationCodec.Hash(
                $"{call.CallId:N}|{ReceptionistConfigurationCodec.Hash(confirmation.Option.Handle)}|{version}|{confirmation.AcknowledgedEndMs}|{start}|{end}|{ReceptionistApplicationCatalog.Normalize(text)}");
            var result = await booking!.CommitAsync(call, confirmation.Option.Handle, true, true, receipt, leaseToken, effect.Token);
            lock (gate) { bookingResult = result.State == "booking_rejected_before_attempt" ? null : result; bookingOptions = []; preparedBooking = null; input.Clear(); output.Clear(); }
            return Say(delegationId, BookingResultMessage(result));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lock (gate) { bookingResult = new("booking_uncertain", false, call.ControlledTest); bookingOptions = []; preparedBooking = null; }
            return Say(delegationId, BookingResultMessage(bookingResult));
        }
        finally { lock (gate) { if (activeIntent == effect) activeIntent = null; } effect.Dispose(); }
    }

    private static string BookingResultMessage(ReceptionistBookingResult result) => result.Verified
        ? result.Synthetic ? "Your synthetic booking outcome was recorded and independently checked. No calendar appointment or invitation was created."
            : "Your appointment is booked and both the calendar event and saved appointment have been checked. The email invitation was authorized; I can't confirm its delivery to your inbox."
        : result.State == "booking_rejected_before_attempt" ? "That option could not be booked. No appointment or invitation was attempted. Please give another day so I can check fresh options."
        : result.State == "booking_uncertain" ? "I can't yet confirm the appointment outcome. It needs reconciliation before another booking attempt; I won't create a duplicate."
            : "The appointment was not authorized as a verified booking. The intake result remains unchanged.";
    private static string SlotLabel(ReceptionistBookingOption option) =>
        TimeZoneInfo.ConvertTimeFromUtc(option.StartUtc, TimeZoneInfo.FindSystemTimeZoneById(option.TimeZoneId))
            .ToString("dddd, MMMM d, yyyy 'at' h:mm tt", CultureInfo.InvariantCulture) + " " + option.TimeZoneId;
    private static bool TrySlotChoice(string text, out int choice)
    {
        var value = ReceptionistApplicationCatalog.Normalize(text);
        var match = Regex.Match(value, @"^(?:(?:i(?:'ll| will) (?:take|choose)|please choose|option|the)\s+)?(one|two|three|first|second|third|[123])(?:\s+(?:one|option))?$", RegexOptions.CultureInvariant);
        choice = match.Success ? match.Groups[1].Value switch { "one" or "first" or "1" => 1, "two" or "second" or "2" => 2, _ => 3 } : 0;
        return choice != 0;
    }
    private static bool TryBookingDate(string text, DateOnly today, out DateOnly date)
    {
        var value = ReceptionistApplicationCatalog.Normalize(text);
        // Several dates or a negated date are not one confirmed choice. Ask again
        // rather than silently favoring the first matching word.
        if (Regex.IsMatch(value, @"\b(?:not|don't|do not|except|either|or)\b", RegexOptions.CultureInvariant))
        { date = default; return false; }
        var dateMentions = Regex.Matches(value, @"\b(?:today|tomorrow|monday|tuesday|wednesday|thursday|friday|saturday|sunday|\d{4}-\d{2}-\d{2})\b", RegexOptions.CultureInvariant);
        if (dateMentions.Count != 1) { date = default; return false; }
        if (Regex.IsMatch(value, @"\btomorrow\b", RegexOptions.CultureInvariant)) { date = today.AddDays(1); return true; }
        if (Regex.IsMatch(value, @"\btoday\b", RegexOptions.CultureInvariant)) { date = today; return true; }
        var iso = Regex.Match(value, @"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant);
        if (iso.Success && DateOnly.TryParseExact(iso.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        var weekdays = Enum.GetValues<DayOfWeek>().Where(x => Phrase(value, x.ToString())).ToArray();
        if (weekdays.Length == 1)
        {
            var offset = ((int)weekdays[0] - (int)today.DayOfWeek + 7) % 7;
            if (value.Contains("next", StringComparison.Ordinal) && offset == 0) offset = 7;
            date = today.AddDays(offset); return true;
        }
        date = default; return false;
    }
    private sealed class PreparedBooking(ReceptionistBookingOption option, Dictionary<string, bool> acknowledgements)
    {
        internal ReceptionistBookingOption Option { get; } = option;
        internal Dictionary<string, bool> Acknowledgements { get; } = acknowledgements;
        internal bool Acknowledged;
        internal int AcknowledgedEndMs = -1;
    }
}
