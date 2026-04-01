namespace Protect_Website.Helpers
{
    public static class EmailHelper
    {
        private static readonly string HeadingColor = "#cca134f1";
        private static readonly string HeadingFontSize = "1.2em";
        private static readonly string HeadingPadding = "4px 6px";

        public static string ApplyHeadingHighlighting(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return html;

            return System.Text.RegularExpressions.Regex.Replace(html, @"<(h[34])>(.*?)</\1>", m =>
            {
                var tag = m.Groups[1].Value;
                var content = m.Groups[2].Value;
                return $"<{tag} style=\"background-color:{HeadingColor}; font-size:{HeadingFontSize}; padding:{HeadingPadding};\">{content}</{tag}>";
            });
        }
    }
}
