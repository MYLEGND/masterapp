using System;
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
            "google", "bing", "facebook", "fb", "meta", "instagram", "tiktok", "youtube", "linkedin", "adwords", "ads", "ad", "paidsearch", "display", "paid_social", "paidsearch", "cpc", "ppc"
        };
        private static readonly HashSet<string> PaidMediums = new(StringComparer.OrdinalIgnoreCase)
        {
            "cpc", "ppc", "paid", "paidsearch", "display", "paid_social", "social_paid", "remarketing", "retargeting"
        };
        private static readonly HashSet<string> OrganicMediums = new(StringComparer.OrdinalIgnoreCase)
        {
            "organic", "seo"
        };
        private static readonly HashSet<string> DirectMediums = new(StringComparer.OrdinalIgnoreCase)
        {
            "(none)", "direct"
        };
        private static readonly HashSet<string> ReferralMediums = new(StringComparer.OrdinalIgnoreCase)
        {
            "referral"
        };

        public static TrafficType Classify(string? utmSource, string? utmMedium, string? utmCampaign, string? fbclid)
        {
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
            }
            // fallback: if any paid-like campaign
            if (!string.IsNullOrWhiteSpace(utmCampaign) && utmCampaign.ToLower().Contains("ad"))
                return TrafficType.PaidAds;
            // fallback: unknown
            return TrafficType.Unknown;
        }

        public static bool MatchesFilter(TrafficType rowType, TrafficType filter)
        {
            if (filter == TrafficType.All) return true;
            if (filter == TrafficType.PaidAds) return rowType == TrafficType.PaidAds;
            if (filter == TrafficType.NonPaid) return rowType != TrafficType.PaidAds;
            return rowType == filter;
        }
    }
}
