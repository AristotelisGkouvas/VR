using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestMarkerApp.API
{
    /// <summary>
    /// Shared secret + simple token-bucket style rate limiting for LocalAPIServer.
    /// </summary>
    [Serializable]
    public class ApiSecurityConfig
    {
        [Tooltip("If true, require X-Api-Key or Authorization: Bearer header")]
        public bool requireApiKey = false;

        [Tooltip("Shared secret. Leave empty to disable even if requireApiKey is true.")]
        public string apiKey = "";

        [Tooltip("Max requests per IP per window")]
        public int maxRequestsPerWindow = 60;

        [Tooltip("Window length in seconds")]
        public float windowSeconds = 60f;

        [Tooltip("Paths that skip auth (health is useful for discovery)")]
        public string[] authExemptPaths = { "/api/health", "/health" };
    }

    public class ApiSecurity
    {
        private readonly ApiSecurityConfig config;
        private readonly Dictionary<string, RateBucket> buckets = new Dictionary<string, RateBucket>();
        private readonly object lockObj = new object();

        public ApiSecurity(ApiSecurityConfig config)
        {
            this.config = config ?? new ApiSecurityConfig();
        }

        public bool IsAuthRequired(string path)
        {
            if (config == null || !config.requireApiKey || string.IsNullOrEmpty(config.apiKey))
                return false;

            if (config.authExemptPaths != null)
            {
                foreach (var p in config.authExemptPaths)
                {
                    if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
            return true;
        }

        public bool ValidateApiKey(string headerApiKey, string authorizationHeader)
        {
            if (string.IsNullOrEmpty(config.apiKey)) return true;
            if (!string.IsNullOrEmpty(headerApiKey) &&
                string.Equals(headerApiKey, config.apiKey, StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(authorizationHeader) &&
                authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string token = authorizationHeader.Substring(7).Trim();
                return string.Equals(token, config.apiKey, StringComparison.Ordinal);
            }
            return false;
        }

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private double lastPruneSeconds;

        /// <returns>true if allowed</returns>
        public bool CheckRateLimit(string clientKey)
        {
            if (config.maxRequestsPerWindow <= 0) return true;
            if (string.IsNullOrEmpty(clientKey)) clientKey = "unknown";

            lock (lockObj)
            {
                // Worker-thread safe clock. Double, not float — float only holds
                // ~2-minute precision at unix-epoch magnitudes, breaking the window math.
                double now = (DateTime.UtcNow - UnixEpoch).TotalSeconds;

                PruneStaleBuckets(now);

                if (!buckets.TryGetValue(clientKey, out RateBucket b))
                {
                    b = new RateBucket { windowStart = now, count = 0 };
                    buckets[clientKey] = b;
                }

                if (now - b.windowStart > config.windowSeconds)
                {
                    b.windowStart = now;
                    b.count = 0;
                }

                b.count++;
                return b.count <= config.maxRequestsPerWindow;
            }
        }

        /// <summary>Drop buckets idle for over two windows so per-IP entries don't accumulate forever.</summary>
        private void PruneStaleBuckets(double now)
        {
            if (now - lastPruneSeconds < 300) return;
            lastPruneSeconds = now;

            List<string> stale = null;
            foreach (var kvp in buckets)
            {
                if (now - kvp.Value.windowStart > config.windowSeconds * 2)
                    (stale ??= new List<string>()).Add(kvp.Key);
            }
            if (stale != null)
                foreach (var key in stale)
                    buckets.Remove(key);
        }

        private class RateBucket
        {
            public double windowStart;
            public int count;
        }
    }
}
