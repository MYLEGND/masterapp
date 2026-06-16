using System;
using System.IO;
using System.Linq;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../"));

string[] forbidden =
{
    "IMetaConversionsApiService",
    "_metaConversionsApi",
    "SendLeadAsync",
    "SendEventAsync",
    "fbq(",
    "QualifiedLead",
    "PolicyPaid",
    "ApplicationSubmitted"
};

var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);

foreach (var file in files)
{
    var text = File.ReadAllText(file);

    foreach (var f in forbidden)
    {
        if (text.Contains(f))
        {
            Console.WriteLine("❌ META LOCK VIOLATION:");
            Console.WriteLine(f);
            Console.WriteLine(file);
            Environment.Exit(1);
        }
    }
}

Console.WriteLine("✅ META SYSTEM FULLY LOCKED (CONTROLLER BINDING ENFORCED)");
