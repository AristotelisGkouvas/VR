using System.Text;

namespace QuestMarkerApp.API
{
    /// <summary>
    /// Shared JSON string escaping for the hand-built JSON writers
    /// (LocalAPIServer, MarkerManager, RequestLog, NetworkDiscovery).
    /// </summary>
    public static class JsonText
    {
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            AppendEscaped(sb, s);
            return sb.ToString();
        }

        /// <summary>Append "key":"value" with the value escaped.</summary>
        public static void AppendField(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":\"");
            AppendEscaped(sb, value);
            sb.Append('"');
        }

        public static void AppendEscaped(StringBuilder sb, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
        }
    }
}
