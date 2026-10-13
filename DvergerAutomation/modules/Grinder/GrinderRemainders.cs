using System.Collections.Generic;

namespace DvergerAutomation {
    /// <summary>
    /// The fractions of a unit a Grinder owes, kept on its ZDO in thousandths per material, so grinding five
    /// swords at 20% returns exactly one sword's worth rather than losing the fractions to rounding.
    /// </summary>
    internal static class GrinderRemainders {
        private static readonly int Key = "DA_grinder_rem".GetStableHashCode();

        private const int Version = 1;

        // Keyed by material prefab name, prefixed when the material came from a cheated item, so a cheated
        // sword never tops up a legitimate remainder.
        private const char CheatedMark = '!';

        internal static string KeyFor(string prefab, bool cheated) => cheated ? CheatedMark + prefab : prefab;

        internal static string PrefabOf(string key, out bool cheated) {
            cheated = key.Length > 0 && key[0] == CheatedMark;
            return cheated ? key.Substring(1) : key;
        }

        internal static Dictionary<string, long> Load(ZDO zdo) {
            Dictionary<string, long> milli = new Dictionary<string, long>();
            byte[] raw = zdo.GetByteArray(Key);
            if (raw == null || raw.Length == 0) { return milli; }
            ZPackage pkg = new ZPackage(raw);
            if (pkg.ReadInt() != Version) { return milli; }
            int count = pkg.ReadInt();
            for (int i = 0; i < count; ++i) {
                string key = pkg.ReadString();
                milli[key] = pkg.ReadLong();
            }
            return milli;
        }

        internal static void Save(ZDO zdo, Dictionary<string, long> milli) {
            ZPackage pkg = new ZPackage();
            pkg.Write(Version);
            int count = 0;
            foreach (KeyValuePair<string, long> entry in milli) {
                if (entry.Value > 0) { ++count; }
            }
            pkg.Write(count);
            foreach (KeyValuePair<string, long> entry in milli) {
                if (entry.Value <= 0) { continue; }
                pkg.Write(entry.Key);
                pkg.Write(entry.Value);
            }
            zdo.Set(Key, pkg.GetArray());
        }
    }
}
