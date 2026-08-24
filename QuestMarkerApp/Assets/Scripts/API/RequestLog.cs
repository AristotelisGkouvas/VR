using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace QuestMarkerApp.API
{
    /// <summary>
    /// Ring buffer of recent API requests for observability (GET /api/logs).
    /// Thread-safe.
    /// </summary>
    public class RequestLog
    {
        public struct Entry
        {
            public long timestampMs;
            public string method;
            public string path;
            public int statusCode;
            public int durationMs;
            public string client;
            public string note;
        }

        private readonly int capacity;
        private readonly Queue<Entry> entries;
        private readonly object lockObj = new object();

        public RequestLog(int capacity = 100)
        {
            this.capacity = Mathf.Max(10, capacity);
            entries = new Queue<Entry>(this.capacity);
        }

        public void Add(string method, string path, int statusCode, int durationMs, string client = null, string note = null)
        {
            var e = new Entry
            {
                timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                method = method ?? "",
                path = path ?? "",
                statusCode = statusCode,
                durationMs = durationMs,
                client = client ?? "",
                note = note ?? ""
            };

            lock (lockObj)
            {
                if (entries.Count >= capacity)
                    entries.Dequeue();
                entries.Enqueue(e);
            }
        }

        public Entry[] Snapshot()
        {
            lock (lockObj)
            {
                return entries.ToArray();
            }
        }

        public string ToJson()
        {
            var list = Snapshot();
            var sb = new StringBuilder(128 + list.Length * 96);
            sb.Append("{\"count\":").Append(list.Length).Append(",\"entries\":[");
            for (int i = 0; i < list.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var e = list[i];
                sb.Append('{')
                  .Append("\"ts\":").Append(e.timestampMs).Append(',')
                  .Append("\"method\":\"").Append(Esc(e.method)).Append("\",")
                  .Append("\"path\":\"").Append(Esc(e.path)).Append("\",")
                  .Append("\"status\":").Append(e.statusCode).Append(',')
                  .Append("\"ms\":").Append(e.durationMs).Append(',')
                  .Append("\"client\":\"").Append(Esc(e.client)).Append("\",")
                  .Append("\"note\":\"").Append(Esc(e.note)).Append("\"")
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Esc(string s) => JsonText.Escape(s);
    }
}
