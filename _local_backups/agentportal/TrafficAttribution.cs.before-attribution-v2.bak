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
        Internal,
        Test,
        BotSuspicious,
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
        private static readonly HashSet<string> MetaSources = new(StringComparer.OrdinalIgnoreCase)
        {
            "facebook", "fb", "meta", "instagram", "facebook_ads", "instagram_ads", "meta_ads"
        };
        private static readonly string[] InternalDomains =
        {
            "mylegnd.com",
            "localhost",
            "127.0.0.1"
        };
        private static readonly string[] NonProductionHostHints =
        {
            "localhost",
            "127.0.0.1",
            ".local",
            "dev",
            "staging",
            "preview",
            "sandbox",
            "ngrok",
            "azurewebsites.net"
        };
        private static readonly string[] TestTokens =
        {
            "test",
            "qa",
            "preview",
            "staging",
            "sandbox",
            "debug",
            "internal"
        };
        private static readonly string[] BotTokens =
        {
            "bot",
            "crawler",
            "spider",
            "headless",
            "lighthouse",
            "monitor",
            "uptime",
            "synthetic",
            "healthcheck"
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

        private static bool ContainsAnyToken(IEnumerable<string?> values, IEnumerable<string> tokens)
        {
            foreach (var raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var normalized = raw.Trim().ToLowerInvariant();
                if (tokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            return false;
        }

        private static bool IsInternalReferrer(string? referrerHost)
        {
            var normalized = NormalizeHost(referrerHost);
            if (string.IsNullOrWhiteSpace(normalized)) return false;

            return InternalDomains.Any(domain =>
                normalized.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsNonProductionEnvironment(string? environment, string? host)
        {
            var normalizedEnvironment = Normalize(environment);
            if (!string.IsNullOrWhiteSpace(normalizedEnvironment) &&
                !string.Equals(normalizedEnvironment, "production", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalizedEnvironment, "prod", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var normalizedHost = NormalizeHost(host);
            if (string.IsNullOrWhiteSpace(normalizedHost)) return false;

            return NonProductionHostHints.Any(hint =>
                normalizedHost.Contains(hint, StringComparison.OrdinalIgnoreCase));
        }

        private static TrafficType? ClassifySpecialTraffic(
            bool isInternal,
            string? environment,
            string? host,
            string? referrerHost,
            params string?[] values)
        {
            if (isInternal)
                return TrafficType.Internal;

            if (IsNonProductionEnvironment(environment, host) || ContainsAnyToken(values, TestTokens))
                return TrafficType.Test;

            if (ContainsAnyToken(values, BotTokens))
                return TrafficType.BotSuspicious;

            if (IsInternalReferrer(referrerHost))
                return TrafficType.Internal;

            return null;
        }

        public static TrafficType Classify(
            string? utmSource,
            string? utmMedium,
            string? utmCampaign,
            string? fbclid,
            string? referrerHost = null,
            string? metaCampaignId = null,
            string? metaAdSetId = null,
            string? metaAdId = null,
            bool isInternal = false,
            string? environment = null,
            string? host = null)
        {
            utmSource = Normalize(utmSource);
            utmMedium = Normalize(utmMedium);
            utmCampaign = Normalize(utmCampaign);
            fbclid = Normalize(fbclid);
            referrerHost = NormalizeHost(referrerHost);
            metaCampaignId = Normalize(metaCampaignId);
            metaAdSetId = Normalize(metaAdSetId);
            metaAdId = Normalize(metaAdId);
            environment = Normalize(environment);
            host = NormalizeHost(host);

            var specialTraffic = ClassifySpecialTraffic(
                isInternal,
                environment,
                host,
                referrerHost,
                utmSource,
                utmMedium,
                utmCampaign,
                fbclid,
                metaCampaignId,
                metaAdSetId,
                metaAdId);

            if (specialTraffic.HasValue)
                return specialTraffic.Value;

            if (!string.IsNullOrWhiteSpace(fbclid) ||
                !string.IsNullOrWhiteSpace(metaCampaignId) ||
                !string.IsNullOrWhiteSpace(metaAdSetId) ||
                !string.IsNullOrWhiteSpace(metaAdId))
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

            return TrafficType.Unknown;
        }

        public static bool IsMetaAttributedPaid(
            string? utmSource,
            string? utmMedium,
            string? utmCampaign,
            string? fbclid,
            string? metaCampaignId = null,
            string? metaAdSetId = null,
            string? metaAdId = null,
            bool isInternal = false,
            string? environment = null,
            string? host = null,
            string? referrerHost = null)
        {
            var source = Normalize(utmSource);
            var medium = Normalize(utmMedium);
            fbclid = Normalize(fbclid);
            metaCampaignId = Normalize(metaCampaignId);
            metaAdSetId = Normalize(metaAdSetId);
            metaAdId = Normalize(metaAdId);

            if (ClassifySpecialTraffic(
                    isInternal,
                    environment,
                    host,
                    referrerHost,
                    source,
                    medium,
                    utmCampaign,
                    fbclid,
                    metaCampaignId,
                    metaAdSetId,
                    metaAdId).HasValue)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(fbclid) ||
                !string.IsNullOrWhiteSpace(metaCampaignId) ||
                !string.IsNullOrWhiteSpace(metaAdSetId) ||
                !string.IsNullOrWhiteSpace(metaAdId))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(source) &&
                MetaSources.Contains(source) &&
                (string.IsNullOrWhiteSpace(medium) || PaidMediums.Contains(medium)))
            {
                return true;
            }

            return false;
        }

        public static bool MatchesFilter(TrafficType rowType, TrafficType filter)
        {
            var reportingBucket = ToReportingBucket(rowType);

            if (filter == TrafficType.All) return true;
            if (filter == TrafficType.PaidAds) return reportingBucket == TrafficType.PaidAds;
            if (filter == TrafficType.NonPaid)
            {
                return reportingBucket == TrafficType.NonPaid;
            }
            if (filter == TrafficType.Unknown) return reportingBucket == TrafficType.Unknown;
            return rowType == filter;
        }

        /// <summary>
        /// Public dashboard reporting must be mutually exhaustive: every session resolves to
        /// PaidAds, NonPaid, or Unknown. Internal/test/bot classes remain available as raw
        /// diagnostics, but they roll into Unknown for operator-facing growth reporting so the
        /// All Traffic bucket always equals PaidAds + NonPaid + Unknown.
        /// </summary>
        public static TrafficType ToReportingBucket(TrafficType rawType) => rawType switch
        {
            TrafficType.PaidAds => TrafficType.PaidAds,
            TrafficType.NonPaid => TrafficType.NonPaid,
            TrafficType.Organic => TrafficType.NonPaid,
            TrafficType.Direct => TrafficType.NonPaid,
            TrafficType.Referral => TrafficType.NonPaid,
            _ => TrafficType.Unknown
        };

        /// <summary>Human-readable label for a traffic filter, used in snapshot headers and diagnostics.</summary>
        public static string BucketLabel(TrafficType t) => t switch
        {
            TrafficType.All      => "All Traffic (Paid Ads + Non-Ads + Unknown)",
            TrafficType.PaidAds  => "Paid Ads Only",
            TrafficType.NonPaid  => "Non-Ads Only",
            TrafficType.Organic  => "Organic Only",
            TrafficType.Direct   => "Direct Only",
            TrafficType.Referral => "Referral Only",
            TrafficType.Internal => "Internal Navigation / Preview",
            TrafficType.Test => "Test / QA Traffic",
            TrafficType.BotSuspicious => "Bot / Suspicious Traffic",
            TrafficType.Unknown  => "Unknown / Unclassified Only",
            _                    => t.ToString()
        };
    }
}
