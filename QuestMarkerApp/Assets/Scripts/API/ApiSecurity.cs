using System;
using System.Collections.Generic;
using System.Text;
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
            if (!string.IsNullOrEmpty(headerApiKey) && FixedTimeEquals(headerApiKey, config.apiKey))
                return true;

            if (!string.IsNullOrEmpty(authorizationHeader) &&
                authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string token = authorizationHeader.Substring(7).Trim();
                return FixedTimeEquals(token, config.apiKey);
            }
            return false;
        }

        /// <summary>
        /// Compares the UTF-8 bytes in time independent of where they first differ, so a
        /// guesser on the LAN learns nothing from response timing. Only the length leaks.
        /// (Manual loop: CryptographicOperations is not guaranteed in Unity's .NET profile.)
        /// </summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] x = Encoding.UTF8.GetBytes(a), y = Encoding.UTF8.GetBytes(b);
            if (x.Length != y.Length) return false;
            int diff = 0;
            for (int i = 0; i < x.Length; i++) diff |= x[i] ^ y[i];
            return diff == 0;
        }

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private double lastPruneSeconds;

        /// <summary>
        /// Clients that proved the key get this many times <see cref="ApiSecurityConfig.maxRequestsPerWindow"/>:
        /// the plain limit (60/min in the scenes) is a brute-force guard, and a PC tool pushing
        /// live positions at a few Hz must not be throttled by it.
        /// </summary>
        public const int AuthenticatedRateMultiplier = 10;

        /// <summary>Wrong keys allowed per IP per window before that IP gets 429 for the rest of it.</summary>
        public const int MaxAuthFailuresPerWindow = 10;

        /// <returns>true if allowed</returns>
        public bool CheckRateLimit(string clientKey)
        {
            return Hit("req:", clientKey, config.maxRequestsPerWindow, true);
        }

        /// <summary>Rate limit for a request that already passed the key check (higher budget).</summary>
        /// <returns>true if allowed</returns>
        public bool CheckAuthenticatedRateLimit(string clientKey)
        {
            if (config.maxRequestsPerWindow <= 0) return true;
            long max = (long)config.maxRequestsPerWindow * AuthenticatedRateMultiplier;
            return Hit("auth:", clientKey, (int)Math.Min(int.MaxValue, max), true);
        }

        /// <summary>True while this client has used up its wrong-key attempts for the window.</summary>
        public bool IsLockedOut(string clientKey)
        {
            return !Hit("fail:", clientKey, MaxAuthFailuresPerWindow, false);
        }

        /// <summary>Count one wrong/missing key from this client (see <see cref="IsLockedOut"/>).</summary>
        public void RecordAuthFailure(string clientKey)
        {
            Hit("fail:", clientKey, MaxAuthFailuresPerWindow, true);
        }

        /// <summary>Fixed-window counter: optionally counts one hit, returns whether the client is within max.</summary>
        private bool Hit(string scope, string clientKey, int max, bool count)
        {
            if (max <= 0 || config.maxRequestsPerWindow <= 0) return true;
            if (string.IsNullOrEmpty(clientKey)) clientKey = "unknown";
            string key = scope + clientKey;

            lock (lockObj)
            {
                // Worker-thread safe clock. Double, not float — float only holds
                // ~2-minute precision at unix-epoch magnitudes, breaking the window math.
                double now = (DateTime.UtcNow - UnixEpoch).TotalSeconds;

                PruneStaleBuckets(now);

                if (!buckets.TryGetValue(key, out RateBucket b))
                {
                    if (!count) return true;
                    b = new RateBucket { windowStart = now, count = 0 };
                    buckets[key] = b;
                }

                if (now - b.windowStart > config.windowSeconds)
                {
                    b.windowStart = now;
                    b.count = 0;
                }

                if (!count) return b.count < max;
                b.count++;
                return b.count <= max;
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
