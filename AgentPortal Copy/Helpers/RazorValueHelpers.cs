using System;
using System.Net;

namespace AgentPortal.Helpers
{
    public static class RazorValueHelpers
    {
        public static string V(string? v)
        {
            return string.IsNullOrWhiteSpace(v)
                ? "<span class=\"det-val-empty\">\u2014</span>"
                : $"<span class=\"det-val\">{WebUtility.HtmlEncode(v)}</span>";
        }

        public static string B(bool? v, string yes = "Yes", string no = "No")
        {
            if (v == null) return "<span class=\"det-na\">\u2014</span>";
            return v == true
                ? $"<span class=\"det-yes\">{yes}</span>"
                : $"<span class=\"det-no\">{no}</span>";
        }
    }
}
