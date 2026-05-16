using System.Collections.Generic;

namespace AgentPortal.Services.Analytics
{
    public enum TrafficType
    {
        All,
        PaidAds,
        NonPaid,
        Organic,
        Direct,
        Referral,
        Unknown
    }

    public static class TrafficAttribution
    {
        private static readonly HashSet<string> PaidSources = new(StringComparer.OrdinalIgnoreCase)
        {
            "adwords", "googleads", "google_ads", "gads", "bingads", "meta_ads",
            "facebook_ads", "instagram_ads", "paidsearch", "display", "paid_social",
            "cpc", "ppc", "remarketing", "retargeting"
        };
        private static readonly HashSet<string> PaidMediums = new(StringComparer.OrdinalIgnoreCase)
        {
            "cpc", "ppc", "paid", "paidsearch", "display", "paid_social", "social_paid",
            "remarketing", "retargeting", "paid_search", "paid-social"
        };
        private static readonly HashSet<string> OrganicMediums = new(StringComparer.OrdinalIgnoreCase)
        {
            "organic", "seo", "organic_search"
        };
        private static readonly HashSet<string> DirectMediums = new(StringComparer.OrdinalIgnoreCase)
        {
            "(none)", "direct"
        };
        private static readonly HashSet<string> ReferralMediums = new(StringComparer.OrdinalIgnoreCase)
        {
            "referral", "partner"
        };
        private static readonly HashSet<string> SearchSources = new(StringComparer.OrdinalIgnoreCase)
        {
            "google", "bing", "yahoo", "duckduckgo", "brave", "ecosia", "search"
        };
        private static readonly HashSet<string> ReferralSources = new(StringComparer.OrdinalIgnoreCase)
        {
            "facebook", "fb", "meta", "instagram", "tiktok", "youtube", "linkedin",
            "reddit", "x", "twitter", "pinterest", "nextdoor", "partner", "newsletter"
        };

        private static string? Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string? NormalizeHost(string? value)
        {
            var host = Normalize(value);
            if (string.IsNullOrWhiteSpace(host)) return null;
            host = host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                host = host[4..];
            return host;
        }

        public static TrafficType Classify(
            string? utmSource,
            string? utmMedium,
            string? utmCampaign,
            string? fbclid,
            string? referrerHost = null)
        {
            utmSource = Normalize(utmSource);
            utmMedium = Normalize(utmMedium);
            utmCampaign = Normalize(utmCampaign);
            fbclid = Normalize(fbclid);
            referrerHost = NormalizeHost(referrerHost);

            if (!string.IsNullOrWhiteSpace(fbclid))
                return TrafficType.PaidAds;

            if (!string.IsNullOrWhiteSpace(utmMedium))
            {
                if (PaidMediums.Contains(utmMedium)) return TrafficType.PaidAds;
                if (OrganicMediums.Contains(utmMedium)) return TrafficType.Organic;
                if (DirectMediums.Contains(utmMedium)) return TrafficType.Direct;
                if (ReferralMediums.Contains(utmMedium)) return TrafficType.Referral;
            }

            if (!string.IsNullOrWhiteSpace(utmSource))
            {
                if (PaidSources.Contains(utmSource)) return TrafficType.PaidAds;
                if (SearchSources.Contains(utmSource)) return TrafficType.Organic;
                if (ReferralSources.Contains(utmSource)) return TrafficType.Referral;
                if (string.Equals(utmSource, "direct", StringComparison.OrdinalIgnoreCase)) return TrafficType.Direct;
            }

            if (!string.IsNullOrWhiteSpace(referrerHost))
            {
                if (SearchSources.Any(s => referrerHost.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    return TrafficType.Organic;
                return TrafficType.Referral;
            }

            if (string.IsNullOrWhiteSpace(utmSource) &&
                string.IsNullOrWhiteSpace(utmMedium) &&
                string.IsNullOrWhiteSpace(utmCampaign))
            {
                return TrafficType.Direct;
            }

            return TrafficType.Unknown;
        }

        public static bool MatchesFilter(TrafficType rowType, TrafficType filter)
        {
            if (filter == TrafficType.All) return true;
            if (filter == TrafficType.PaidAds) return rowType == TrafficType.PaidAds;
            if (filter == TrafficType.NonPaid)
            {
                // NonPaid = Organic + Direct + Referral + Unknown/Unattributed.
                return rowType == TrafficType.Organic
                    || rowType == TrafficType.Direct
                    || rowType == TrafficType.Referral
                    || rowType == TrafficType.Unknown;
            }
            return rowType == filter;
        }

        /// <summary>Human-readable label for a traffic filter, used in snapshot headers and diagnostics.</summary>
        public static string BucketLabel(TrafficType t) => t switch
        {
            TrafficType.All      => "All Traffic (Paid + Non-Paid + Unknown)",
            TrafficType.PaidAds  => "Paid Ads Only",
            TrafficType.NonPaid  => "Non-Ads Only (Organic + Referral + Direct + Unknown)",
            TrafficType.Organic  => "Organic Only",
            TrafficType.Direct   => "Direct Only",
            TrafficType.Referral => "Referral Only",
            TrafficType.Unknown  => "Unknown/Unattributed Only",
            _                    => t.ToString()
        };
    }
}
