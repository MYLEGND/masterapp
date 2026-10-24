using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Reception;

/// <summary>Bounded application-services delegation. No secondary model, model-supplied arguments or alternate CRM writer.</summary>
internal sealed class ReceptionistApplicationServices(
    DbContextOptions<MasterAppDbContext> options,
    ReceptionistAdmissionService admission,
    ReceptionistInquiryAuthority inquiries,
    TimeProvider clock)
{
    internal static void ValidateConfiguration(ReceptionistBusinessConfiguration configuration) =>
        _ = ReceptionistApplicationCatalog.Read(configuration);

    internal ReceptionistApplicationSession CreateSession(ReceptionistCallContext call, Guid sidebandLeaseToken)
    {
        if (sidebandLeaseToken == Guid.Empty) throw new ArgumentException("A fenced dialogue owner is required.");
        _ = ReceptionistInquiryAuthority.ReadIntakeRules(call);
        return new(call, sidebandLeaseToken, ReceptionistApplicationCatalog.Read(call.Configuration),
            new CanonicalActions(options, admission, inquiries), clock);
    }

    private sealed class CanonicalActions(DbContextOptions<MasterAppDbContext> options,
        ReceptionistAdmissionService admission, ReceptionistInquiryAuthority inquiries) : IReceptionistApplicationActions
    {
        public async Task<IReadOnlyList<ReceptionistApplicationPriorAction>> PriorAsync(ReceptionistCallContext call, CancellationToken ct)
        {
            var actual = await admission.GetCallContextAsync(call.CallId, ct);
            if (actual is null || actual.Owner != call.Owner || actual.Provider != call.Provider ||
                actual.ProviderProjectKey != call.ProviderProjectKey || actual.ProviderSessionId != call.ProviderSessionId ||
                actual.ConfigurationId != call.ConfigurationId)
                throw new InvalidOperationException("The application session no longer matches its canonical call owner.");
            await using var read = new MasterAppDbContext(options);
            var prior = await read.Set<ReceptionistActionIntent>().AsNoTracking().Where(x => x.CallId == call.CallId &&
                x.CapabilityKey == ReceptionistCapabilities.CanonicalIntake && x.RouteKey == ReceptionistInquiryAuthority.RouteKey)
                .OrderBy(x => x.CreatedUtc).Take(101)
                .Select(x => new ReceptionistApplicationPriorAction(x.Id, x.State)).ToListAsync(ct);
            if (prior.Count > 100) throw new InvalidOperationException("The call has too many prior intents for bounded automatic reconciliation.");
            return prior;
        }
        public Task<ReceptionistActionClaim> ClaimAsync(ReceptionistCallContext call, string logicalKey,
            string factsHash, string confirmationReceipt, Guid leaseToken, CancellationToken ct) =>
            admission.ClaimActionAsync(call.CallId, logicalKey, ReceptionistCapabilities.CanonicalIntake,
                factsHash, confirmationReceipt, leaseToken, ct);
        public Task<ReceptionistInquiryPersistence> PersistAsync(ReceptionistCallContext call, Guid actionId,
            ConfirmedReceptionistInquiry facts, string confirmationReceipt, CancellationToken ct) =>
            inquiries.PersistConfirmedAsync(call, actionId, facts, confirmationReceipt, ct);
        public Task<ReceptionistInquiryReadback?> ReadBackAsync(ReceptionistCallContext call, Guid actionId, CancellationToken ct) =>
            inquiries.ReadBackAsync(call.Owner, call.CallId, actionId, ct);
        public Task OutcomeAsync(Guid actionId, string state, string? actualReference, string receipt, CancellationToken ct) =>
            admission.RecordActionOutcomeAsync(actionId, state, actualReference, receipt, ct);
    }
}

internal sealed record ReceptionistApplicationCatalog(
    IReadOnlyList<ReceptionistApplicationServiceDefinition> Services,
    IReadOnlyList<ReceptionistApplicationAnswer> Answers)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static ReceptionistApplicationCatalog Read(ReceptionistBusinessConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        try
        {
            var services = JsonSerializer.Deserialize<ReceptionistApplicationServiceDefinition[]>(configuration.ServiceDefinitionsJson, Json);
            var knowledge = JsonSerializer.Deserialize<ReceptionistApplicationKnowledge>(configuration.ApprovedKnowledgeJson, Json);
            using var intake = JsonDocument.Parse(configuration.IntakeRulesJson);
            var routes = intake.RootElement.GetProperty("canonicalInquiry").Deserialize<ReceptionistInquiryIntakeRules>(Json);
            if (!configuration.RequestedCapabilities.Contains(ReceptionistCapabilities.CanonicalIntake, StringComparer.Ordinal) ||
                services is null || services.Length is < 1 or > 40 || services.Any(x => x is null) ||
                knowledge?.Answers is null || knowledge.Answers.Count > 100 || knowledge.Answers.Any(x => x is null) ||
                routes?.Services is null || routes.Services.Any(x => x is null) || routes.Services.Count != services.Length ||
                services.Select(x => x.ServiceKey).Distinct(StringComparer.Ordinal).Count() != services.Length ||
                routes.Services.Select(x => x.ServiceKey).Distinct(StringComparer.Ordinal).Count() != services.Length)
                throw new ArgumentException("The deterministic backend requires explicit unique service, intake and approved knowledge definitions.");
            var phrases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var service in services)
            {
                ReceptionistConfigurationCodec.Identifier(service.ServiceKey, 80, "service key");
                Text(service.DisplayName, 120); Text(service.DiscoveryQuestion, 320); Text(service.PublicSummary, 1600);
                if (service.Aliases is null || service.Aliases.Count is < 1 or > 20)
                    throw new ArgumentException("Each configured service needs explicit unambiguous public phrases.");
                foreach (var alias in service.Aliases)
                {
                    Text(alias, 100);
                    if (!phrases.Add(Normalize(alias))) throw new ArgumentException("Configured service phrases cannot name multiple routes.");
                }
                var route = routes.Services.SingleOrDefault(x => x.ServiceKey == service.ServiceKey);
                if (route is null || !WorkstationLeadBuckets.ProductBuckets.Contains(route.CrmBucket, StringComparer.Ordinal))
                    throw new ArgumentException("Every supported service requires its explicit canonical CRM product route.");
            }
            var questions = new HashSet<string>(StringComparer.Ordinal);
            var answerKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var answer in knowledge.Answers)
            {
                ReceptionistConfigurationCodec.Identifier(answer.Key, 100, "knowledge key");
                Text(answer.Answer, 2400);
                if (!answerKeys.Add(answer.Key) || answer.Questions is null || answer.Questions.Count is < 1 or > 20 ||
                    (answer.ServiceKey is not null && !services.Any(x => x.ServiceKey == answer.ServiceKey)) ||
                    answer.Status is not ("approved" or "dated") ||
                    (answer.Status == "dated" && !DateOnly.TryParseExact(answer.AsOfDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) ||
                    (answer.Status == "approved" && answer.AsOfDate is not null))
                    throw new ArgumentException("Approved knowledge must have unique questions, a supported service and an explicit current or dated status.");
                foreach (var question in answer.Questions)
                {
                    Text(question, 200);
                    if (!questions.Add((answer.ServiceKey ?? "*") + "|" + NormalizeQuestion(question)))
                        throw new ArgumentException("An approved question cannot select conflicting answers for the same service.");
                }
            }
            return new(services, knowledge.Answers);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ArgumentException("The pinned application-services schema is malformed or unsupported.", nameof(configuration), ex);
        }
    }

    internal static string Normalize(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ")
        .TrimEnd('.', '?', '!');

    internal static string NormalizeQuestion(string text)
    {
        var value = Normalize(text);
        value = Regex.Replace(value, @"^(?:(?:could|can|would) you (?:please )?(?:tell me|explain) |please (?:tell me|explain) )", "", RegexOptions.CultureInvariant);
        return Regex.Replace(value, @",? please$", "", RegexOptions.CultureInvariant);
    }

    private static void Text(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl) ||
            Regex.Split(value, @"\s+").Any(word => Encoding.UTF8.GetByteCount(word) > 480))
            throw new ArgumentException("Application-services text must be present, bounded plain text.");
    }
}

/// <summary>
/// One disposable state per fenced socket. Observe never waits on application I/O.
/// The Live API supplies deltas and estimated append timelines, not completed turns or playback receipts.
/// </summary>
internal sealed class ReceptionistApplicationSession : IDisposable
{
    private const int MaximumContextCharacters = 6000;
    private const int MaximumAppendBytes = 480;
    private const string Genuine = "observed_genuine_inquiry_pending_reconciliation";
    private readonly object gate = new();
    private readonly SemaphoreSlim work = new(1, 1);
    private readonly ReceptionistCallContext call;
    private readonly Guid leaseToken;
    private readonly ReceptionistApplicationCatalog catalog;
    private readonly IReceptionistApplicationActions actions;
    private readonly TimeProvider clock;
    private readonly StringBuilder input = new();
    private readonly StringBuilder output = new();
    private readonly HashSet<string> seenEvents = new(StringComparer.Ordinal);
    private readonly Queue<string> eventOrder = new();
    private readonly HashSet<string> seenDelegations = new(StringComparer.Ordinal);
    private long inputVersion;
    private int firstInputStart = -1;
    private int lastInputEnd = -1;
    private bool unsafeInput;
    private bool initialized;
    private bool disposed;
    private bool unresolvedAction;
    private ReceptionistInquiryReadback? saved;
    private ConfirmedReceptionistInquiry draft = new();
    private string? pendingKnowledgeQuestion;
    private string? pendingNameFragment;
    private string? spellingField;
    private PreparedInquiry? prepared;
    private CancellationTokenSource? activeIntent;

    internal ReceptionistApplicationSession(ReceptionistCallContext call, Guid leaseToken,
        ReceptionistApplicationCatalog catalog, IReceptionistApplicationActions actions, TimeProvider clock)
    {
        this.call = call; this.leaseToken = leaseToken; this.catalog = catalog; this.actions = actions; this.clock = clock;
    }

    internal void Observe(JsonElement message)
    {
        if (!String(message, "type", out var type) || !String(message, "event_id", out var id) || !Token(id)) return;
        lock (gate)
        {
            if (disposed || !seenEvents.Add(id)) return;
            eventOrder.Enqueue(id);
            if (eventOrder.Count > 256) seenEvents.Remove(eventOrder.Dequeue());
            if (type is "session.input_transcript.delta" or "session.output_transcript.delta")
            {
                if (!String(message, "delta", out var delta) || !Timeline(message, out var start, out var end) ||
                    delta.Length > MaximumContextCharacters || delta.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\r' or '\t')))
                { inputVersion++; Invalidate(); unsafeInput = true; return; }
                if (type == "session.output_transcript.delta")
                {
                    if (output.Length + delta.Length > MaximumContextCharacters) output.Clear();
                    output.Append(delta);
                    return;
                }
                inputVersion++;
                if (activeIntent is not null) _ = activeIntent.CancelAsync();
                if (input.Length + delta.Length > MaximumContextCharacters || end < lastInputEnd)
                { input.Clear(); unsafeInput = true; Invalidate(); }
                if (input.Length == 0) firstInputStart = start;
                input.Append(delta); lastInputEnd = Math.Max(lastInputEnd, end);
                if (prepared is { } pending && (!pending.Acknowledged || start <= pending.AcknowledgedEndMs || Correction(input.ToString())))
                    Invalidate();
                return;
            }
            if (type == "session.commentary.appended" && prepared is { } summary &&
                String(message, "client_event_id", out var commandId) && summary.Acknowledgements.ContainsKey(commandId))
            {
                if (!Timeline(message, out _, out var end)) { Invalidate(); return; }
                summary.Acknowledgements[commandId] = true;
                summary.AcknowledgedEndMs = Math.Max(summary.AcknowledgedEndMs, end);
                // Estimated context injection only. This does not prove that the assistant spoke the text or that a person heard it.
                summary.Acknowledged = summary.Acknowledgements.Values.All(value => value);
            }
            else if (type == "error" && String(message, "client_event_id", out var failedId) &&
                prepared?.Acknowledgements.ContainsKey(failedId) == true)
                Invalidate();
        }
    }

    internal async Task<IReadOnlyList<ReceptionistApplicationUpdate>> InitializeAsync(CancellationToken ct = default)
    {
        await work.WaitAsync(ct);
        try
        {
            lock (gate) { ThrowIfDisposed(); if (initialized) return []; }
            var prior = await actions.PriorAsync(call, ct);
            foreach (var action in prior.Where(x => x.State != "rejected"))
            {
                var readback = await ReadAndRecordAsync(action.ActionId, ct);
                lock (gate)
                {
                    ThrowIfDisposed();
                    if (readback is null) unresolvedAction = true;
                    else saved = readback;
                }
            }
            lock (gate)
            {
                ThrowIfDisposed(); initialized = true;
                var updates = Updates(null, "session.instructions.append",
                    "Application results are the only authority for saved inquiries. A delegation supplies no typed arguments. Ask one useful clarification when details are uncertain. Never treat a transcript fragment or a lone yes as permission to save; read back the request and wait for an explicit sentence asking to save that request.");
                if (unresolvedAction) updates.AddRange(Say(null, "I can't yet confirm whether the earlier request was saved. I need its status checked before another save."));
                else if (saved is not null) updates.AddRange(Say(null, "Your earlier request is saved for the team to review. I have checked the saved record."));
                return updates;
            }
        }
        finally { work.Release(); }
    }

    internal async Task<IReadOnlyList<ReceptionistApplicationUpdate>> HandleDelegationAsync(string delegationId, CancellationToken ct = default)
    {
        if (!Token(delegationId)) throw new ArgumentException("The original client delegation ID is required.");
        await work.WaitAsync(ct);
        try
        {
            string text; long version; int start; int end; PreparedInquiry? confirmation;
            lock (gate)
            {
                ThrowIfDisposed();
                if (!initialized) throw new InvalidOperationException("Reconcile the durable call actions before handling a delegation.");
                if (!seenDelegations.Add(delegationId)) return [];
                if (seenDelegations.Count > 128) throw new InvalidOperationException("The bounded call delegation limit was reached.");
                if (unresolvedAction) return Say(delegationId, "I can't yet confirm whether the earlier request was saved. I won't create another while its status is uncertain.");
                if (saved is not null) return Say(delegationId, "Your request is saved for the team to review. I can't change that saved record within this call.");
                text = input.ToString().Trim(); version = inputVersion; start = firstInputStart; end = lastInputEnd; confirmation = prepared;
                input.Clear(); firstInputStart = -1;
                if (unsafeInput)
                {
                    unsafeInput = false; Invalidate();
                    return Say(delegationId, "I couldn't safely follow the complete statement. Please say the request or correction again in one brief sentence.");
                }
            }
            if (confirmation is not null)
            {
                if (confirmation.Acknowledged && start > confirmation.AcknowledgedEndMs && CompleteSaveSentence(text))
                    return await SaveAsync(delegationId, confirmation, version, start, end, text, ct);
                lock (gate) { if (prepared == confirmation) Invalidate(); }
                if (string.IsNullOrWhiteSpace(text) || IsBriefConfirmation(text))
                    return Prepare(delegationId, version, "Nothing has been saved. Please review the request, then tell me explicitly whether you want me to save the request I read back.");
            }
            if (string.IsNullOrWhiteSpace(text))
                return Say(delegationId, "What would you like help with?");
            if (Sensitive(text))
            {
                lock (gate) { draft = new(); pendingNameFragment = null; spellingField = null; pendingKnowledgeQuestion = null; Invalidate(); }
                return Say(delegationId, "Please don't read out card numbers, passwords or verification codes. What general service question or request can I help with?");
            }
            if (DeclinesSaving(text))
            {
                lock (gate) { draft = new(); pendingNameFragment = null; spellingField = null; pendingKnowledgeQuestion = null; Invalidate(); }
                return Say(delegationId, "I haven't saved a request. What else would you like to know?");
            }
            if (ExistingOrVendor(text))
            {
                lock (gate) { draft = new(); pendingNameFragment = null; spellingField = null; pendingKnowledgeQuestion = null; Invalidate(); }
                return Say(delegationId, "I can answer approved general questions. I can't access or change an existing customer account, or create a new service inquiry from that request.");
            }
            text = Regex.Replace(text, @"^(?:Actually|Correction)[,:]?\s+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            // Delegation and punctuation are not evidence that a name fragment is complete.
            // Never concatenate separate requests into an invented name. An unfinished clause
            // requires a fresh, explicit name statement before it can enter the final readback.
            lock (gate)
            {
                var repeatedName = pendingNameFragment is not null;
                if (repeatedName)
                {
                    pendingNameFragment = null;
                    if (!Regex.IsMatch(text, @"^(?:my (?:first name|last name|name) is|I'm|I am|This is)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        return Say(delegationId, "Please say your complete name again, or explicitly spell your first or last name. I won't join separate fragments or guess it.");
                }
                if (!repeatedName && Regex.IsMatch(text, @"^my (?:first name|last name|name) is\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                    !Regex.IsMatch(text, @"[.!?]$"))
                {
                    pendingNameFragment = "name clarification required";
                    return Say(delegationId, "Please confirm your complete name, or spell your first or last name. I haven't saved anything.");
                }
            }
            var question = NormalizeQuestion(text);
            var matches = catalog.Services.Where(service => service.Aliases.Any(alias => Phrase(text, alias))).ToArray();
            var explicitChoice = ExplicitServiceChoice(text);
            var isQuestion = text.TrimEnd().EndsWith('?') || Regex.IsMatch(text, @"^(?:what|how|why|when|where|does|can|could)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var negatedChoice = Regex.IsMatch(text, @"\b(?:not|don't|do not|neither)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var choice = explicitChoice ?? (matches.Length == 1 && !isQuestion && !negatedChoice ? matches[0] : null);
            string? serviceKey;
            lock (gate)
            {
                if (disposed || inputVersion != version) return Say(delegationId, "I heard more information. Please finish the correction so I can use the right context.");
                if (choice is not null && draft.ServiceKey != choice.ServiceKey)
                    draft = draft with
                    {
                        ServiceKey = choice.ServiceKey,
                        RequestSummary = draft.ServiceKey is null ? draft.RequestSummary : null
                    };
                serviceKey = isQuestion && matches.Length == 1 ? matches[0].ServiceKey : choice is null && matches.Length > 1 ? null : draft.ServiceKey;
                if (pendingKnowledgeQuestion is not null && choice is not null && IsServiceSelection(text, choice))
                {
                    question = pendingKnowledgeQuestion;
                    pendingKnowledgeQuestion = null;
                }
                else if (isQuestion) pendingKnowledgeQuestion = null;
                if (!Regex.IsMatch(text, @"^(?:My (?:first name|last name|name) is|It is|It's|That is|That's|Spelled)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) spellingField = null;
            }
            var answers = catalog.Answers.Where(item => item.Questions.Any(q => NormalizeQuestion(q) == question)).ToArray();
            var answer = answers.SingleOrDefault(item => item.ServiceKey is not null && item.ServiceKey == serviceKey)
                ?? answers.SingleOrDefault(item => item.ServiceKey is null);
            if (answer is not null)
                return Say(delegationId, answer.Status == "dated" ? $"The approved information dated {answer.AsOfDate} says: {answer.Answer} It needs current confirmation before a commitment." : answer.Answer);
            if (answers.Length > 0)
            {
                lock (gate) { pendingKnowledgeQuestion = question; }
                return Say(delegationId, serviceKey is null
                    ? "Which service is that question about: " + string.Join(", ", catalog.Services.Select(x => x.DisplayName)) + "?"
                    : "I don't have an approved answer to that question for " + catalog.Services.Single(x => x.ServiceKey == serviceKey).DisplayName + ". Are you asking about a different service?");
            }
            if (UnavailableAction(text))
                return Say(delegationId, "I can't book an appointment, transfer a call, take payment or arrange a callback in this call. I can answer general service questions or help prepare a service inquiry for the team to review.");
            if (matches.Length > 1 && choice is null)
            {
                lock (gate)
                {
                    if (disposed || inputVersion != version) return Say(delegationId, "Please finish the correction before choosing the service.");
                    // Mentioning another service does not erase a previously explicit choice or need.
                    if (draft.ServiceKey is null)
                        draft = ExtractExplicitFacts(draft, text).Facts;
                }
                return Say(delegationId, "Which service is this request about: " + string.Join(", ", matches.Select(x => x.DisplayName)) + "?");
            }
            lock (gate)
            {
                if (disposed || inputVersion != version) return Say(delegationId, "I heard more information. Please finish the correction so I can read back the right request.");
                if (Regex.IsMatch(text, @"^My name is\s+[\p{L}'-]+\s+[\p{L} '-]+[.!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return Say(delegationId, "Which part should I record as your first name, and which as your last name? Please state each explicitly so I don't guess the split.");
                var possibleName = Regex.Match(text, @"^(?:I'm|I am|This is)\s+([\p{L}'-]{1,120})[.!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (possibleName.Success)
                    return Say(delegationId, "If you are giving your name, please say 'My name is' followed by the name you want in this request. I won't infer a name from a description.");
                var extracted = ExtractExplicitFacts(draft, text);
                draft = extracted.Facts;
                if (extracted.Unrecognized && !(choice is not null && IsServiceSelection(text, choice)) && (Correction(text) || ContactDetail(text)))
                    return Say(delegationId, ContactDetail(text)
                        ? "What contact detail should I correct or leave out? Please state that detail clearly so I can read it back without guessing."
                        : "I couldn't safely interpret the complete correction. Please state the corrected request in one brief sentence.");
                if (!extracted.Understood && matches.Length == 0)
                    return Say(delegationId, draft.ServiceKey is null
                        ? "I couldn't safely interpret that request. What would you like help with?"
                        : "We are discussing " + catalog.Services.Single(x => x.ServiceKey == draft.ServiceKey).DisplayName +
                          ". What detail would you like clarified? I have kept the request you already gave me.");
                if (draft.ServiceKey is null)
                    return Say(delegationId, "Which service would you like help with: " + string.Join(", ", catalog.Services.Take(4).Select(x => x.DisplayName)) + "?");
                var service = catalog.Services.Single(x => x.ServiceKey == draft.ServiceKey);
                if (draft.RequestSummary is null)
                {
                    if (question.Contains('?') || question.StartsWith("what ", StringComparison.Ordinal) || question.StartsWith("tell me", StringComparison.Ordinal))
                        return Say(delegationId, service.PublicSummary + " " + service.DiscoveryQuestion);
                    return Say(delegationId, service.DiscoveryQuestion);
                }
            }
            return Prepare(delegationId, version, null);
        }
        catch (ArgumentException)
        {
            lock (gate) { Invalidate(); }
            return Say(delegationId, "I need to clarify the request before saving it. Please give the service and what you need in a brief sentence; leave out any sensitive details.");
        }
        finally { work.Release(); }
    }

    private IReadOnlyList<ReceptionistApplicationUpdate> Prepare(string delegationId, long version, string? introduction)
    {
        lock (gate)
        {
            if (disposed || inputVersion != version) return Say(delegationId, "Please finish the correction before I read the request back.");
            if (draft.ServiceKey is null || draft.RequestSummary is null) return Say(delegationId, "What service do you need, and what would you like help with?");
            var facts = draft with { AcquisitionClass = Genuine, RequestedNextAction = "Save the stated service inquiry for team review." };
            var hash = ReceptionistInquiryAuthority.CanonicalArgumentsSha256(facts);
            var service = catalog.Services.Single(x => x.ServiceKey == facts.ServiceKey);
            var parts = new List<string>();
            if (introduction is not null) parts.Add(introduction);
            parts.Add($"Here is the request I can save for the team to review. Service: {service.DisplayName}. Request: {facts.RequestSummary}");
            if (facts.FirstName is not null) parts.Add("First name: " + facts.FirstName + ".");
            if (facts.LastName is not null) parts.Add("Last name: " + facts.LastName + ".");
            if (facts.BusinessName is not null) parts.Add("Business: " + facts.BusinessName + ".");
            if (facts.Email is not null) parts.Add("Email provided for this request: " + facts.Email + ".");
            if (facts.Phone is not null) parts.Add("Phone provided for this request: " + facts.Phone + ".");
            parts.Add("This saves the inquiry for review. No callback, email, booking or other follow-up is arranged by this call. If the details are correct, tell me explicitly that you want me to save the request I just read back, or tell me what to correct.");
            var updates = parts.SelectMany(part => Say(delegationId, part)).ToList();
            prepared = new(facts, hash, "confirmed-inquiry/" + Guid.NewGuid().ToString("N"),
                updates.ToDictionary(x => x.EventId, _ => false, StringComparer.Ordinal));
            return updates;
        }
    }

    private async Task<IReadOnlyList<ReceptionistApplicationUpdate>> SaveAsync(string delegationId, PreparedInquiry intent,
        long version, int confirmationStartMs, int confirmationEndMs, string confirmationText, CancellationToken ct)
    {
        // A short stabilization window is not a completed-turn signal. The complete explicit sentence,
        // acknowledged summary timeline and context-version checks remain mandatory independent gates.
        await Task.Delay(TimeSpan.FromMilliseconds(350), clock, ct);
        CancellationTokenSource effect;
        lock (gate)
        {
            if (disposed || prepared != intent || inputVersion != version)
                return Say(delegationId, "I heard a correction. Nothing has been saved from that confirmation; let me use the latest details.");
            effect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            activeIntent = effect;
        }
        Guid? actionId = null;
        var writeStarted = false;
        var sentenceHash = ReceptionistConfigurationCodec.Hash(ReceptionistApplicationCatalog.Normalize(confirmationText));
        var receipt = "caller-confirmation:v1:" + ReceptionistConfigurationCodec.Hash(
            $"{call.CallId:N}|{intent.FactsHash}|{intent.LogicalKey}|{version}|{intent.AcknowledgedEndMs}|{confirmationStartMs}|{confirmationEndMs}|{sentenceHash}");
        try
        {
            var claim = await actions.ClaimAsync(call, intent.LogicalKey, intent.FactsHash, receipt, leaseToken, effect.Token);
            actionId = claim.ActionId;
            if (actionId is null || claim.RouteKey != ReceptionistInquiryAuthority.RouteKey)
                return Say(delegationId, "I couldn't authorize that save. Your request has not been confirmed as saved.");
            if (!claim.MayExecute)
            {
                var prior = await ReadAndRecordAsync(actionId.Value, ct);
                StoreResolution(prior, prior is null);
                return Say(delegationId, prior is null ? "I can't yet confirm whether the request was saved." : "Your request is saved for the team to review. I have checked the saved record.");
            }
            bool changed;
            lock (gate) { changed = disposed || prepared != intent || inputVersion != version || effect.IsCancellationRequested; }
            if (changed)
            {
                using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await actions.OutcomeAsync(actionId.Value, "rejected", null, "caller-correction-before-write", finish.Token);
                return Say(delegationId, "I heard a correction before saving. Please finish the corrected request.");
            }
            writeStarted = true;
            var result = await actions.PersistAsync(call, actionId.Value, intent.Facts, receipt, effect.Token);
            if (result.State == "not_attempted")
            {
                await actions.OutcomeAsync(actionId.Value, "rejected", null, "inquiry-excluded:" + result.ExclusionReason, ct);
                lock (gate) { Invalidate(); }
                return Say(delegationId, "No customer inquiry was saved because this interaction is excluded from inquiry intake.");
            }
            using var verify = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var readback = await ReadAndRecordAsync(actionId.Value, verify.Token);
            StoreResolution(readback, readback is null);
            if (readback is null) return Say(delegationId, "I can't yet confirm whether your request was saved.");
            lock (gate) changed = inputVersion != version;
            return Say(delegationId, changed
                ? "The request you confirmed was saved and checked before the latest correction could be applied. I haven't changed that saved record or arranged a follow-up."
                : "Your request is saved for the team to review. I have checked the saved record. No follow-up has been arranged by this call.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A lost claim/commit response is not evidence that nothing happened. Reconcile actual records,
            // and block a second write while the effect remains uncertain.
            ReceptionistInquiryReadback? readback = null;
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (actionId is not null)
                {
                    readback = await ReadAndRecordAsync(actionId.Value, finish.Token);
                    if (readback is null) await actions.OutcomeAsync(actionId.Value,
                        writeStarted ? "uncertain" : "rejected", null,
                        writeStarted ? "inquiry-write-response-unresolved" : "claim-canceled-before-write", finish.Token);
                }
                else
                {
                    var prior = await actions.PriorAsync(call, finish.Token);
                    foreach (var item in prior.Where(x => x.State != "rejected"))
                        readback ??= await ReadAndRecordAsync(item.ActionId, finish.Token);
                }
            }
            catch (Exception reconciliation) when (reconciliation is not OutOfMemoryException) { }
            StoreResolution(readback, readback is null && (writeStarted || actionId is null));
            return Say(delegationId, readback is not null
                ? "The request you confirmed is saved and its record has been checked. I haven't applied any later correction or arranged a follow-up."
                : writeStarted || actionId is null ? "I can't yet confirm whether your request was saved. Its status needs to be checked before another save."
                : "No inquiry write was started. Please finish the corrected request before another confirmation.");
        }
        finally
        {
            lock (gate) { if (activeIntent == effect) activeIntent = null; }
            effect.Dispose();
        }
    }

    private async Task<ReceptionistInquiryReadback?> ReadAndRecordAsync(Guid actionId, CancellationToken ct)
    {
        var readback = await actions.ReadBackAsync(call, actionId, ct);
        if (readback is not null)
        {
            if (readback.OwnerKey != call.Owner.Key || readback.CallId != call.CallId || readback.ActionId != actionId ||
                readback.State != "crm_readback_verified" || readback.InquiryId == Guid.Empty || readback.IntakeLinkId == Guid.Empty ||
                string.IsNullOrWhiteSpace(readback.RecordReference) || string.IsNullOrWhiteSpace(readback.CrmRecordReference) ||
                !readback.VerificationReceipt.StartsWith("inquiry-readback:v1:", StringComparison.Ordinal))
                throw new InvalidOperationException("The independent readback does not prove this call's owner-bound canonical inquiry.");
            await actions.OutcomeAsync(actionId, "verified", readback.RecordReference, readback.VerificationReceipt, ct);
        }
        return readback;
    }

    private (ConfirmedReceptionistInquiry Facts, bool Understood, bool Unrecognized) ExtractExplicitFacts(ConfirmedReceptionistInquiry prior, string text)
    {
        var facts = prior;
        var understood = false;
        var unrecognized = false;
        foreach (var raw in Regex.Split(text, @"(?<=[.!?])\s+|,?\s+and\s+(?=I (?:need|want|would like)|my (?:name|first name|last name) is)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var sentence = Regex.Replace(raw.Trim(), @"^(?:Actually|Correction)[,:]?\s+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var recognizedSentence = false;
            var need = Regex.Match(sentence, @"^(?:I need|I want|I would like|I'd like|I am looking for|I'm looking for|Please help me with)\s+.+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (need.Success && !UnavailableAction(sentence) && !SaveDiscussion(sentence))
            {
                if (Encoding.UTF8.GetByteCount(sentence) > 360) throw new ArgumentException("The minimal stated need must be brief enough to read back accurately.");
                var selectionOnly = catalog.Services.Any(service => service.Aliases.Any(alias => Regex.IsMatch(
                    ReceptionistApplicationCatalog.Normalize(sentence),
                    @"^(?:i need|i want|i would like|i'd like)\s+(?:the\s+)?" + Regex.Escape(ReceptionistApplicationCatalog.Normalize(alias)) +
                    @"(?:\s+service)?(?:,?\s+please)?$", RegexOptions.CultureInvariant)));
                if (!selectionOnly) facts = facts with { RequestSummary = sentence.Trim() };
                recognizedSentence = true;
            }
            if (SaveDiscussion(sentence) && facts.RequestSummary is not null) recognizedSentence = true;
            var email = Regex.Match(sentence, @"^(?:My email is|My email address is)\s+([^\s@]+@[^\s@]+\.[^\s@]+)[.!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (email.Success) { facts = facts with { Email = email.Groups[1].Value.TrimEnd('.', '!') }; recognizedSentence = true; }
            var phone = Regex.Match(sentence, @"^(?:My phone number is|My number is)\s+([+()0-9 .-]+)[.!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (phone.Success) { facts = facts with { Phone = phone.Groups[1].Value.TrimEnd('.', '!').Trim() }; recognizedSentence = true; }
            if (!Regex.IsMatch(sentence, @"^My first name is spelled\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) && NamedFact(sentence, "first name", out var first)) { facts = facts with { FirstName = first }; spellingField = "first"; recognizedSentence = true; }
            if (!Regex.IsMatch(sentence, @"^My last name is spelled\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) && NamedFact(sentence, "last name", out var last)) { facts = facts with { LastName = last }; spellingField = "last"; recognizedSentence = true; }
            var introduction = Regex.Match(sentence.TrimEnd(','), @"^(?:My name is)\s+([\p{L}'-]{1,120})[.!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (introduction.Success && !new[] { "interested", "looking", "calling", "not", "unsure", "ready", "done" }.Contains(introduction.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
            { facts = facts with { FirstName = introduction.Groups[1].Value }; spellingField = "first"; recognizedSentence = true; }
            var spelled = Regex.Match(sentence, @"^(?:(?:My )?(first name|last name) is spelled |(?:It is|It's|That is|That's|Spelled)\s+)([\p{L}](?:[ -]*[\p{L}]){1,119})[.!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (spelled.Success && (spelled.Groups[1].Success || Regex.IsMatch(spelled.Groups[2].Value, @"[ -]")))
            {
                var field = spelled.Groups[1].Value.StartsWith("first", StringComparison.OrdinalIgnoreCase) ? "first" :
                    spelled.Groups[1].Value.StartsWith("last", StringComparison.OrdinalIgnoreCase) ? "last" : spellingField;
                if (field is not null)
                {
                    var name = Regex.Replace(spelled.Groups[2].Value, @"[ -]+", "");
                    facts = field == "first" ? facts with { FirstName = name } : facts with { LastName = name };
                    spellingField = field; recognizedSentence = true;
                }
            }
            if (NamedFact(sentence, "business", out var business)) { facts = facts with { BusinessName = business }; recognizedSentence = true; }
            var remove = Regex.Match(sentence, @"^(?:please )?(?:leave out|remove|do not include|don't include) my (email(?: address)?|phone(?: number)?|name)[.!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (remove.Success)
            {
                var field = remove.Groups[1].Value.ToLowerInvariant();
                if (field == "name") { spellingField = null; pendingNameFragment = null; }
                facts = field.StartsWith("email", StringComparison.Ordinal) ? facts with { Email = null } :
                    field.StartsWith("phone", StringComparison.Ordinal) ? facts with { Phone = null } : facts with { FirstName = null, LastName = null };
                recognizedSentence = true;
            }
            understood |= recognizedSentence;
            unrecognized |= !recognizedSentence;
        }
        // Caller-provided contact can be part of the final confirmed record, but is never permission to call, email or text.
        return (facts with
        {
            ContactStatus = facts.Email is not null || facts.Phone is not null ? "caller_confirmed_for_requested_purpose" : "not_requested",
            RequestedContactMethod = "inbound_voice_only", ContactPermissionState = "not_requested",
            ContactPurpose = facts.Email is not null || facts.Phone is not null ? "Record the stated inquiry; no follow-up permission requested." : null
        }, understood, unrecognized);
    }

    private ReceptionistApplicationServiceDefinition? ExplicitServiceChoice(string text)
    {
        // Resolve an explicit final choice, not arbitrary last-mentioned words. Configured aliases
        // remain the only service identities; ambiguity never grants a route.
        var clauses = Regex.Split(text, @"[.!?;]|\b(?:but|instead|actually)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var clause in clauses.Reverse())
        {
            var candidate = Regex.Replace(clause, @"\bnot\s+[^,]+,", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            if (!Regex.IsMatch(candidate, @"^(?:(?:I (?:want|need|choose|prefer|would like)|I'd like|let's (?:choose|focus on|go with)|(?:please )?(?:focus on|switch to|go with))\s+|(?:the )?.+\s+please[.!]?$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            var options = catalog.Services.Where(service => service.Aliases.Any(alias => Phrase(candidate, alias))).ToArray();
            return options.Length == 1 && !Regex.IsMatch(candidate, @"\b(?:not|don't|do not|neither)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? options[0] : null;
        }
        return null;
    }

    private static bool IsServiceSelection(string text, ReceptionistApplicationServiceDefinition service) =>
        !text.TrimEnd().EndsWith('?') && service.Aliases.Any(alias => Phrase(text, alias)) &&
        !Regex.IsMatch(text, @"\b(?:what|how|why|when|where|does|can you)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string NormalizeQuestion(string text) => ReceptionistApplicationCatalog.NormalizeQuestion(text);

    private static bool NamedFact(string sentence, string label, out string name)
    {
        var match = Regex.Match(sentence, "^My " + label + @" is\s+([\p{L}\p{N} '&-]{1,120})[.!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        name = match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        return match.Success;
    }

    private static bool CompleteSaveSentence(string text)
    {
        if (text.TrimEnd().EndsWith('?')) return false;
        var value = ReceptionistApplicationCatalog.Normalize(text);
        return Regex.IsMatch(value, @"^(?:yes,?\s+)?(?:please\s+)?(?:save|record) (?:this|the|my) (?:request|inquiry) (?:you just read back|you just summarized|as we just reviewed it)$", RegexOptions.CultureInvariant) ||
            value == "i want you to save the request you just read back" ||
            value == "i confirm the details you just read back and want you to save this request";
    }
    private static bool IsBriefConfirmation(string text) =>
        Regex.IsMatch(ReceptionistApplicationCatalog.Normalize(text), @"^(?:yes|yeah|okay|ok|sure|please save|please save it|save it)$", RegexOptions.CultureInvariant);
    private static bool SaveDiscussion(string text) => CompleteSaveSentence(text) || Regex.IsMatch(text,
        @"^(?:please |I want (?:you )?to |I would like (?:you )?to )?(?:save|record|read back|summarize) (?:my|the|this|that) (?:request|inquiry)[.!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool Phrase(string text, string phrase) => Regex.IsMatch(text,
        @"(?<![\p{L}\p{N}])" + Regex.Escape(phrase.Trim()) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool Correction(string text) => Regex.IsMatch(text, @"\b(?:actually|correction|instead|wrong|don't|do not|cancel|wait)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool ContactDetail(string text) => Regex.IsMatch(text, @"\b(?:email|e-mail|phone|number|name|contact)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool DeclinesSaving(string text) => Regex.IsMatch(text, @"\b(?:don't save|do not save|cancel (?:the |this )?request|I don't want (?:it|this|that) saved)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool Sensitive(string text) => Regex.IsMatch(text, @"\b(?:password|verification code|security code|card number|credit card|social security|SSN)\b|(?:\d[ -]?){16,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool ExistingOrVendor(string text) => Regex.IsMatch(text, @"\b(?:I am an existing customer|I'm an existing customer|my existing (?:request|account)|following up on (?:my |an )?existing|sell you|offer you my services|wrong number)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool UnavailableAction(string text) => Regex.IsMatch(text, @"\b(?:book (?:me |an? )?(?:appointment|meeting)|transfer (?:me|this call)|take (?:my )?payment|charge (?:my )?card|call me back|arrange a callback)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static List<ReceptionistApplicationUpdate> Say(string? delegationId, string content) =>
        Updates(delegationId, "session.commentary.append", content);
    private static List<ReceptionistApplicationUpdate> Updates(string? delegationId, string type, string content)
    {
        var result = new List<ReceptionistApplicationUpdate>();
        var chunk = new StringBuilder();
        foreach (var word in Regex.Split(content.Trim(), @"\s+"))
        {
            if (Encoding.UTF8.GetByteCount(word) > MaximumAppendBytes)
                throw new ArgumentException("An unbroken application value exceeds the bounded update size.");
            if (chunk.Length > 0 && Encoding.UTF8.GetByteCount(chunk.ToString()) + 1 + Encoding.UTF8.GetByteCount(word) > MaximumAppendBytes)
            {
                result.Add(new("app_" + Guid.NewGuid().ToString("N"), delegationId, type, chunk.ToString())); chunk.Clear();
            }
            if (chunk.Length > 0) chunk.Append(' ');
            chunk.Append(word);
        }
        if (chunk.Length > 0) result.Add(new("app_" + Guid.NewGuid().ToString("N"), delegationId, type, chunk.ToString()));
        return result;
    }

    private static bool String(JsonElement message, string key, out string value)
    {
        value = string.Empty;
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString() ?? string.Empty; return true;
    }
    private static bool Timeline(JsonElement message, out int start, out int end)
    {
        start = 0; end = 0;
        return message.TryGetProperty("start_ms", out var first) && first.ValueKind == JsonValueKind.Number && first.TryGetInt32(out start) && start >= 0 &&
            message.TryGetProperty("end_ms", out var last) && last.ValueKind == JsonValueKind.Number && last.TryGetInt32(out end) && end >= start;
    }
    private static bool Token(string value) => value.Length is > 0 and <= 200 &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.');
    private void Invalidate() { prepared = null; if (activeIntent is not null) _ = activeIntent.CancelAsync(); }
    private void StoreResolution(ReceptionistInquiryReadback? readback, bool unresolved)
    {
        lock (gate)
        {
            if (!disposed) { saved = readback; unresolvedAction = unresolved; }
            Invalidate();
        }
    }
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(disposed, this); }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; Invalidate(); input.Clear(); output.Clear(); draft = new(); saved = null; pendingKnowledgeQuestion = null; pendingNameFragment = null; spellingField = null;
            seenEvents.Clear(); eventOrder.Clear(); seenDelegations.Clear();
        }
    }

    private sealed class PreparedInquiry(ConfirmedReceptionistInquiry facts, string factsHash, string logicalKey,
        Dictionary<string, bool> acknowledgements)
    {
        internal ConfirmedReceptionistInquiry Facts { get; } = facts;
        internal string FactsHash { get; } = factsHash;
        internal string LogicalKey { get; } = logicalKey;
        internal Dictionary<string, bool> Acknowledgements { get; } = acknowledgements;
        internal bool Acknowledged;
        internal int AcknowledgedEndMs = -1;
    }
}
