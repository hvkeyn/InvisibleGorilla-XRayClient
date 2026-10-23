using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace InvisibleGorillaXRay.Models
{
    /// <summary>
    /// Shared server-scope rules so Windows, Linux and Android pick nodes the same way.
    /// 0 = any node, 1 = foreign only, 2 = Russia only.
    /// </summary>
    public static class ServerScope
    {
        public const int Any = 0;
        public const int ForeignOnly = 1;
        public const int RussiaOnly = 2;

        private static readonly string[] RussianMarkers =
        {
            "росси", "рф", "russia", "москв", "все операторы", "игровой"
        };

        public static bool IsRussianNode(string name)
        {
            string value = (name ?? string.Empty).ToLowerInvariant();
            if (value.Length == 0)
                return false;

            foreach (string marker in RussianMarkers)
            {
                if (value.Contains(marker))
                    return true;
            }

            return Regex.IsMatch(value, @"\bru\b");
        }

        public static bool Matches(string name, int scope)
        {
            bool russian = IsRussianNode(name);

            return scope switch
            {
                ForeignOnly => !russian,
                RussiaOnly => russian,
                _ => true
            };
        }

        public static List<string> Filter(IEnumerable<string> names, int scope)
        {
            var result = new List<string>();
            foreach (string name in names)
            {
                if (Matches(name, scope))
                    result.Add(name);
            }

            return result;
        }
    }
}