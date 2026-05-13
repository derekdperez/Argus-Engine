using System;
using System.Collections.Generic;
using System.Linq;

namespace ArgusEngine.CommandCenter.Contracts
{
    public static class TargetRootNormalization
    {
        private static readonly string[] LineSeparators = ["\r\n", "\r", "\n"];

        public static bool TryNormalize(string raw, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var trimmed = raw.Trim().ToLowerInvariant();
            
            // Basic normalization: remove http:// or https:// if present
            if (trimmed.StartsWith("http://", StringComparison.Ordinal))
                trimmed = trimmed.Substring(7);
            else if (trimmed.StartsWith("https://", StringComparison.Ordinal))
                trimmed = trimmed.Substring(8);

            // Remove trailing slashes or paths
            var slashIndex = trimmed.IndexOf('/');
            if (slashIndex >= 0)
                trimmed = trimmed.Substring(0, slashIndex);

            if (string.IsNullOrWhiteSpace(trimmed))
                return false;

            normalized = trimmed;
            return true;
        }

        public static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<string>();

            return text.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries)
                       .Select(l => l.Trim())
                       .Where(l => l.Length > 0)
                       .ToArray();
        }
    }
}
