using System;
using System.IO;
using System.Linq;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../"));

string[] forbidden =
{
    ".lq-booking-shell",
    ".lq-booking-embed-shell",
    "#lifePostSubmitSlot .lq-booking-iframe",
    "#disabilityPostSubmitSlot .lq-booking-iframe",
    "#dvhPostSubmitSlot .lq-booking-iframe",
    ".lq-booking-iframe {"
};

var files = Directory.GetFiles(root, "*.cshtml", SearchOption.AllDirectories)
    .Where(f => !f.Contains("_QuoteBookingSystemStyles.cshtml"))
    .ToList();

foreach (var file in files)
{
    var text = File.ReadAllText(file);

    foreach (var f in forbidden)
    {
        if (text.Contains(f))
        {
            Console.WriteLine("❌ BOOKING SYSTEM VIOLATION:");
            Console.WriteLine(f);
            Console.WriteLine(file);
            Environment.Exit(1);
        }
    }
}

Console.WriteLine("✅ BOOKING SYSTEM CLEAN");
