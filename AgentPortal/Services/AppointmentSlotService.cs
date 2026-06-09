namespace AgentPortal.Services;

public class AppointmentSlotService
{
    public List<DateTime> GenerateSlots(DateTime start, DateTime end, List<(DateTime start, DateTime end)> busy)
    {
        var slots = new List<DateTime>();
        var cursor = start;

        while (cursor < end)
        {
            var conflict = busy.Any(b =>
                cursor < b.end && cursor.AddMinutes(30) > b.start);

            if (!conflict)
                slots.Add(cursor);

            cursor = cursor.AddMinutes(30);
        }

        return slots;
    }
}
