using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DvergerAutomation {
    /// <summary>
    /// The Seeking Table's search box.
    ///
    /// The query language is Epic Loot's own, so a player who knows the enchanting table's search knows
    /// this one. <see cref="Parse"/>, the enchantment half of <see cref="Matches"/>, <see cref="StripRichText"/>
    /// and <see cref="NormalizeEffectName"/> are a port of Epic Loot's <c>ItemSearchFilter</c>
    /// (EpicLoot/src/UI/Enchanting/ItemSearchFilter.cs, Epic Loot 0.15). Epic Loot is only a soft
    /// dependency, and that class is newer than most installs of it, so it is copied rather than called:
    ///  - plain text shows an item whose name contains any of its words, or which carries every
    ///    comma-separated phrase as an enchantment;
    ///  - a leading <c>@</c> searches enchantments only, each phrase matched whole against one
    ///    enchantment name, so "attack speed" does not match "Movement Speed".
    /// On top of that, plain text also finds an item whose description contains the whole query - as one
    /// phrase, because single words like "iron" turn up in half the descriptions in the game.
    ///
    /// Without Epic Loot, or with one too old to describe its enchantments, there is nothing for
    /// <c>@</c> to search and it is taken as an ordinary character.
    /// </summary>
    internal static class SeekingSearch {
        internal const char EnchantOnlyPrefix = '@';

        private static readonly Regex RichTextRegex = new Regex(@"<[^>]*>", RegexOptions.Compiled);
        private static readonly Regex PlaceholderRegex = new Regex(@"\{[^}]*\}", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly char[] NameWordSeparators = { ' ', ',' };

        internal sealed class Query {
            internal bool EnchantOnly;
            internal string[] NameWords = Array.Empty<string>();
            internal readonly List<string> Phrases = new List<string>();
            /// <summary>The whole query, whitespace collapsed, for the description match.</summary>
            internal string Whole = "";

            // An empty box, a bare @ or nothing but commas: everything shows.
            internal bool IsEmpty => Phrases.Count == 0;
        }

        internal static Query Parse(string text) {
            Query query = new Query();
            string trimmed = text?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && trimmed[0] == EnchantOnlyPrefix && EnchantSearchAvailable) {
                query.EnchantOnly = true;
                trimmed = trimmed.Substring(1);
            }

            foreach (string part in trimmed.Split(',')) {
                string phrase = CollapseWhitespace(part);
                if (phrase.Length > 0) { query.Phrases.Add(phrase); }
            }

            if (!query.EnchantOnly) {
                query.NameWords = trimmed.Split(NameWordSeparators, StringSplitOptions.RemoveEmptyEntries);
                query.Whole = CollapseWhitespace(trimmed);
            }
            return query;
        }

        internal static bool Matches(Query query, SeekEntry entry) {
            if (query.IsEmpty) { return true; }

            if (!query.EnchantOnly) {
                foreach (string word in query.NameWords) {
                    if (entry.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
                }
                if (query.Whole.Length > 0 && entry.Description.IndexOf(query.Whole, StringComparison.OrdinalIgnoreCase) >= 0) {
                    return true;
                }
            }

            IReadOnlyList<string> enchantmentNames = entry.EnchantmentNames;
            if (enchantmentNames == null || enchantmentNames.Count == 0) { return false; }

            foreach (string phrase in query.Phrases) {
                bool found = false;
                foreach (string enchantmentName in enchantmentNames) {
                    if (enchantmentName.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0) {
                        found = true;
                        break;
                    }
                }
                if (!found) { return false; }
            }
            return true;
        }

        internal static string StripRichText(string text) {
            return RichTextRegex.Replace(text ?? string.Empty, string.Empty);
        }

        /// <summary>
        /// A localized effect DisplayText reduced to the words a player would search for: no rich text, no
        /// {0} value slots, single spaces. "Attack Speed +{0:0.#}%" becomes "Attack Speed +%".
        /// </summary>
        internal static string NormalizeEffectName(string localizedDisplayText) {
            return CollapseWhitespace(PlaceholderRegex.Replace(StripRichText(localizedDisplayText), string.Empty));
        }

        private static string CollapseWhitespace(string text) {
            return WhitespaceRegex.Replace(text, " ").Trim();
        }

        // ---- what an item is searched by --------------------------------------------

        private sealed class Texts {
            internal string Name;
            internal string Description;
        }

        // Keyed on the shared data, which every item of a kind points at. Localized text cannot change
        // under a running game short of a language switch, which a reopened table picks up anyway because
        // the caches are dropped on close.
        private static readonly Dictionary<ItemDrop.ItemData.SharedData, Texts> PlainTexts = new Dictionary<ItemDrop.ItemData.SharedData, Texts>();

        private sealed class MagicTexts {
            internal string Name;
            internal string[] Enchantments;
        }

        // Keyed on the item's custom data, where Epic Loot keeps its magic: asking Epic Loot goes through
        // reflection and a JSON round trip, and a chest reload hands back new instances of the same items.
        private static readonly Dictionary<string, MagicTexts> Magic = new Dictionary<string, MagicTexts>();
        private static readonly Dictionary<string, string> EffectNames = new Dictionary<string, string>();
        private static readonly StringBuilder KeyBuilder = new StringBuilder();

        private static bool? enchantSearch;

        /// <summary>
        /// Whether the installed Epic Loot can tell this mod what an item's enchantments are called. Asked
        /// once: the answer cannot change while the game runs.
        /// </summary>
        internal static bool EnchantSearchAvailable {
            get {
                if (enchantSearch == null) {
                    try {
                        enchantSearch = EpicLootAPI.EpicLoot.IsLoaded()
                            && EpicLootAPI.EpicLoot.HasEndpoint("GetMagicItemJson")
                            && EpicLootAPI.EpicLoot.HasEndpoint("GetMagicItemEffectDefinition");
                    } catch (Exception ex) {
                        Logger.LogWarning($"[Seeking Table] could not ask Epic Loot about enchantments: {ex.Message}");
                        enchantSearch = false;
                    }
                }
                return enchantSearch.Value;
            }
        }

        /// <summary>Fills in what <paramref name="entry"/> is found by and shown as.</summary>
        internal static void Describe(SeekEntry entry) {
            ItemDrop.ItemData item = entry.Sample;
            Texts plain = PlainTextsOf(item.m_shared);
            entry.Description = plain.Description;
            entry.Name = plain.Name;
            entry.EnchantmentNames = Array.Empty<string>();
            if (!entry.Magic) { return; }

            MagicTexts magic = MagicTextsOf(item);
            if (magic == null) { return; }
            if (!string.IsNullOrEmpty(magic.Name)) { entry.Name = magic.Name; }
            entry.EnchantmentNames = magic.Enchantments;
        }

        /// <summary>Drops the caches. Called when the table closes, so a long session does not hoard them.</summary>
        internal static void ClearCaches() {
            PlainTexts.Clear();
            Magic.Clear();
            EffectNames.Clear();
        }

        private static Texts PlainTextsOf(ItemDrop.ItemData.SharedData shared) {
            if (PlainTexts.TryGetValue(shared, out Texts texts)) { return texts; }
            Localization loc = Localization.instance;
            texts = new Texts {
                Name = StripRichText(loc != null ? loc.Localize(shared.m_name) : shared.m_name),
                Description = StripRichText(loc != null ? loc.Localize(shared.m_description) : shared.m_description),
            };
            PlainTexts[shared] = texts;
            return texts;
        }

        private static MagicTexts MagicTextsOf(ItemDrop.ItemData item) {
            string key = MagicKey(item);
            if (Magic.TryGetValue(key, out MagicTexts cached)) { return cached; }

            MagicTexts texts = new MagicTexts { Enchantments = Array.Empty<string>() };
            try {
                Localization loc = Localization.instance;
                string display = EpicLootAPI.EpicLoot.GetDisplayName(item);
                if (!string.IsNullOrEmpty(display)) {
                    texts.Name = StripRichText(loc != null ? loc.Localize(display) : display);
                }
                // An unidentified item's effects are not the player's to know yet, so search must not
                // give them away.
                if (EnchantSearchAvailable && !EpicLootAPI.EpicLoot.IsUnidentified(item)) {
                    texts.Enchantments = EnchantmentNamesOf(item);
                }
            } catch (Exception ex) {
                Logger.LogWarning($"[Seeking Table] reading the enchantments of {item.m_shared.m_name} failed: {ex.Message}");
            }
            Magic[key] = texts;
            return texts;
        }

        private static string[] EnchantmentNamesOf(ItemDrop.ItemData item) {
            EpicLootAPI.MagicItem magic = EpicLootAPI.EpicLoot.GetMagicItem(item);
            if (magic == null) { return Array.Empty<string>(); }

            List<string> names = new List<string>();
            if (magic.Effects != null) {
                foreach (EpicLootAPI.MagicItemEffect effect in magic.Effects) { AddEffectName(effect, names); }
            }
            // Socketed shards and runestones count, as they do on the enchanting table.
            if (magic.Sockets != null) {
                foreach (EpicLootAPI.SocketedEffect socket in magic.Sockets) { AddEffectName(socket?.Effect, names); }
            }
            return names.ToArray();
        }

        private static void AddEffectName(EpicLootAPI.MagicItemEffect effect, List<string> into) {
            if (effect == null || string.IsNullOrEmpty(effect.EffectType)) { return; }
            if (!EffectNames.TryGetValue(effect.EffectType, out string name)) {
                EpicLootAPI.MagicItemEffectDefinition definition = EpicLootAPI.MagicItemEffectDefinition.Copy(effect.EffectType);
                string display = definition != null ? definition.DisplayText : "";
                Localization loc = Localization.instance;
                name = string.IsNullOrEmpty(display) ? "" : NormalizeEffectName(loc != null ? loc.Localize(display) : display);
                EffectNames[effect.EffectType] = name;
            }
            if (name.Length > 0) { into.Add(name); }
        }

        // Everything Epic Loot could have written about the item, in a stable order.
        private static string MagicKey(ItemDrop.ItemData item) {
            KeyBuilder.Clear();
            KeyBuilder.Append(item.m_shared.m_name).Append('\n').Append(item.m_quality);
            ItemIdentity.AppendCustomData(KeyBuilder, item);
            return KeyBuilder.ToString();
        }
    }
}
