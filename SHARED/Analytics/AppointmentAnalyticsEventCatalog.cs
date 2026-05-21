namespace Shared.Analytics;

public static class AppointmentAnalyticsEventCatalog
{
    public const string EmbedViewed = "appointment_embed_viewed";
    public const string SlotSelected = "appointment_slot_selected";
    public const string Booked = "appointment_booked";
    public const string Abandoned = "appointment_abandoned";
    public const string Completed = "appointment_completed";
    public const string NoShow = "appointment_no_show";

    public static IReadOnlyList<string> All =>
    [
        EmbedViewed,
        SlotSelected,
        Booked,
        Abandoned,
        Completed,
        NoShow
    ];
}
