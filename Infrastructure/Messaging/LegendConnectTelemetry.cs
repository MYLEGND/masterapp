using System.Diagnostics.Metrics;

namespace Infrastructure.Messaging;

/// <summary>
/// Aggregate-only instrumentation for Legend Connect. Tags contain language
/// identifiers and provider/state metadata only—never message or corpus text.
/// </summary>
internal static class LegendConnectTelemetry
{
    private static readonly Meter Meter = new("Legend.Connect", "1.0");
    private static readonly Counter<long> TranslationsRequested = Meter.CreateCounter<long>("legend.connect.translations.requested");
    private static readonly Counter<long> SameLanguageBypasses = Meter.CreateCounter<long>("legend.connect.translations.same_language_bypass");
    private static readonly Counter<long> TranslationMemoryHits = Meter.CreateCounter<long>("legend.connect.translations.memory_hit");
    private static readonly Counter<long> ProviderCharacters = Meter.CreateCounter<long>("legend.connect.provider.characters");
    private static readonly Counter<long> LearningEvents = Meter.CreateCounter<long>("legend.connect.learning.events");
    private static readonly Counter<long> CorpusEvents = Meter.CreateCounter<long>("legend.connect.corpus.events");
    private static readonly Counter<long> CapacityReservations = Meter.CreateCounter<long>("legend.connect.capacity.reservations");

    internal static void TranslationRequested(string? source, string target) =>
        TranslationsRequested.Add(1, new KeyValuePair<string, object?>("source_language", source), new("target_language", target));

    internal static void SameLanguageBypass(string language) =>
        SameLanguageBypasses.Add(1, new KeyValuePair<string, object?>("language", language));

    internal static void TranslationMemoryHit(string pairKey) =>
        TranslationMemoryHits.Add(1, new KeyValuePair<string, object?>("pair", pairKey));

    internal static void ProviderCharactersServed(string provider, long characters, string source, string target) =>
        ProviderCharacters.Add(Math.Max(0, characters), new KeyValuePair<string, object?>("provider", provider), new("source_language", source), new("target_language", target));

    internal static void LearningEvent(string eligibilityState) =>
        LearningEvents.Add(1, new KeyValuePair<string, object?>("eligibility", eligibilityState));

    internal static void CorpusEvent(string state, string pairKey) =>
        CorpusEvents.Add(1, new KeyValuePair<string, object?>("state", state), new("pair", pairKey));

    internal static void CapacityReservation(string provider, string purpose, long characters) =>
        CapacityReservations.Add(Math.Max(0, characters), new KeyValuePair<string, object?>("provider", provider), new("purpose", purpose));
}
