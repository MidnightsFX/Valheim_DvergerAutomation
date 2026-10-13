using System;
using System.Collections.Generic;

namespace DvergerAutomation {
    /// <summary>
    /// A comma-separated list of names - a config entry, or a list kept on a ZDO - parsed once per distinct
    /// text and asked about by name. Asked every pass or every frame, so it re-parses only when the text
    /// itself changes: a server sync, an admin edit, another player's choice arriving.
    /// </summary>
    internal sealed class NameSet {
        private readonly HashSet<string> names;
        private string parsedFrom;

        internal NameSet(StringComparer comparer = null) {
            names = new HashSet<string>(comparer ?? StringComparer.Ordinal);
        }

        /// <summary>Whether <paramref name="list"/> names <paramref name="name"/>.</summary>
        internal bool Contains(string list, string name) {
            return Parsed(list).Contains(name);
        }

        /// <summary><paramref name="list"/> as a set. Read it, never change it: it is this NameSet's own.</summary>
        internal HashSet<string> Parsed(string list) {
            list = list ?? "";
            if (list != parsedFrom) {
                names.Clear();
                foreach (string name in Split(list)) { names.Add(name); }
                parsedFrom = list;
            }
            return names;
        }

        /// <summary>The names in a comma-separated list, trimmed, with empty slots left out.</summary>
        internal static IEnumerable<string> Split(string list) {
            if (string.IsNullOrEmpty(list)) { yield break; }
            foreach (string part in list.Split(',')) {
                string name = part.Trim();
                if (name.Length > 0) { yield return name; }
            }
        }
    }
}
