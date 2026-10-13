using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Every loaded piece a Dverger Lighter can keep fuelled, and one way of reading and filling each.
    ///
    /// Three vanilla components burn fuel, and none of them shares a base class or a list of instances:
    ///  - <see cref="Fireplace"/>: campfires, hearths, bonfires, braziers, standing and wall torches,
    ///    the jack-o-turnip and the snow lantern.
    ///  - <see cref="CookingStation"/> with <c>m_useFuel</c>: the stone oven.
    ///  - <see cref="Smelter"/> with no ore input (<c>m_maxOre</c> 0): the hot tub, which vanilla builds
    ///    as a smelter that burns wood and makes nothing. A Smelter that takes ore is the Hopper's job.
    ///
    /// Each registers itself as it wakes (LighterPatches.cs, and HopperPatches.cs for the Smelter), so a Lighter finds what is in
    /// range by walking this list rather than overlapping every collider within 64 metres of it.
    ///
    /// All three refuse fuel the same way when a player adds it by hand: once whole units no longer fit
    /// under <c>m_maxFuel</c>. <see cref="Room"/> follows that rule, so a Lighter never fills a piece
    /// further than a player could. Only the cooking station and the smelter enforce it on the asking
    /// client rather than the owner, which is why <see cref="Add"/> is only ever called by the piece's
    /// owner.
    /// </summary>
    internal static class FuelBurners {
        // What InRange needs of each piece, read once as it registers: the piece it belongs to and its
        // prefab name both take a lookup (the name an allocation), and InRange walks every piece.
        private struct Entry {
            internal Component Burner;
            internal ZNetView View;
            internal Piece Piece;
            internal string Prefab;
        }

        private static readonly List<Entry> All = new List<Entry>();
        private static readonly HashSet<Component> Known = new HashSet<Component>();

        // Unloaded pieces leave destroyed entries behind; they are swept whenever a Lighter walks the list,
        // and here as well once the list has doubled, for a world with no Lighter in it.
        private static int sweptAt;

        /// <summary>Adds a freshly woken component, if it is a placed piece that burns fuel.</summary>
        internal static void Register(Component burner) {
            if (burner == null || !Burns(burner)) { return; }
            // A placement ghost has no ZDO, and never burns anything.
            ZNetView view = ViewOf(burner);
            if (view == null || view.GetZDO() == null) { return; }
            if (!Known.Add(burner)) { return; }
            All.Add(new Entry {
                Burner = burner,
                View = view,
                Piece = burner.GetComponentInParent<Piece>(),
                Prefab = global::Utils.GetPrefabName(view.gameObject),
            });
            if (All.Count > sweptAt * 2 + 64) { Sweep(); }
        }

        // A destroyed component still hashes and compares by its instance id, so it can be removed from
        // the set after it is gone.
        private static void Sweep() {
            for (int i = All.Count - 1; i >= 0; --i) {
                if (All[i].Burner != null) { continue; }
                Known.Remove(All[i].Burner);
                All.RemoveAt(i);
            }
            sweptAt = All.Count;
        }

        /// <summary>
        /// Every registered piece within <paramref name="radius"/> of <paramref name="center"/> that a player
        /// built, that is loaded and still burns fuel, and that the server has not told Lighters to leave
        /// alone. Fires the world placed itself - a trader's camp, a fuling village - are not anyone's to tend.
        /// </summary>
        internal static void InRange(Vector3 center, float radius, List<Component> into) {
            into.Clear();
            bool swept = false;
            float sqr = radius * radius;
            for (int i = 0; i < All.Count; ++i) {
                Entry entry = All[i];
                if (entry.Burner == null) { swept = true; continue; }
                if ((entry.Burner.transform.position - center).sqrMagnitude > sqr) { continue; }
                if (entry.View == null || !entry.View.IsValid()) { continue; }
                if (entry.Piece == null || !entry.Piece.IsPlacedByPlayer()) { continue; }
                if (LighterHub.IsIgnored(entry.Prefab)) { continue; }
                into.Add(entry.Burner);
            }
            if (swept) { Sweep(); }
        }

        // ---- what each component is ------------------------------------------------

        /// <summary>Whether the component burns a refillable fuel at all. Fixed per prefab.</summary>
        private static bool Burns(Component burner) {
            switch (burner) {
                case Fireplace fire:
                    return fire.m_canRefill && !fire.m_infiniteFuel && fire.m_fuelItem != null && fire.m_maxFuel > 0f;
                case CookingStation oven:
                    return oven.m_useFuel && oven.m_fuelItem != null && oven.m_maxFuel > 0;
                case Smelter tub:
                    return tub.m_maxOre <= 0 && tub.m_fuelItem != null && tub.m_maxFuel > 0;
                default:
                    return false;
            }
        }

        internal static ZNetView ViewOf(Component burner) {
            switch (burner) {
                case Fireplace fire: return fire.m_nview;
                case CookingStation oven: return oven.m_nview;
                case Smelter tub: return tub.m_nview;
                default: return null;
            }
        }

        /// <summary>The item this piece burns, as it is named on the items themselves.</summary>
        internal static string FuelName(Component burner) {
            ItemDrop item = FuelItem(burner);
            return item != null && item.m_itemData != null && item.m_itemData.m_shared != null ? item.m_itemData.m_shared.m_name : null;
        }

        private static ItemDrop FuelItem(Component burner) {
            switch (burner) {
                case Fireplace fire: return fire.m_fuelItem;
                case CookingStation oven: return oven.m_fuelItem;
                case Smelter tub: return tub.m_fuelItem;
                default: return null;
            }
        }

        private static float MaxFuel(Component burner) {
            switch (burner) {
                case Fireplace fire: return fire.m_maxFuel;
                case CookingStation oven: return oven.m_maxFuel;
                case Smelter tub: return tub.m_maxFuel;
                default: return 0f;
            }
        }

        /// <summary>The fuel the piece holds, from this client's copy of its ZDO.</summary>
        internal static float Fuel(Component burner) {
            ZNetView view = ViewOf(burner);
            return view != null && view.IsValid() ? view.GetZDO().GetFloat(ZDOVars.s_fuel) : 0f;
        }

        /// <summary>How full the piece is, 0-1, so the emptiest can be served first.</summary>
        internal static float Fill(Component burner) {
            float max = MaxFuel(burner);
            return max > 0f ? Fuel(burner) / max : 1f;
        }

        /// <summary>
        /// Whole units of fuel the piece would take right now, by the rule vanilla applies to a player
        /// adding fuel by hand. A fire switched off is left as it is: it would not burn what it was given.
        /// </summary>
        internal static int Room(Component burner) {
            ZNetView view = ViewOf(burner);
            if (view == null || !view.IsValid()) { return 0; }
            if (burner is Fireplace && view.GetZDO().GetInt(ZDOVars.s_state, 1) != 1) { return 0; }
            return Mathf.Max(0, Mathf.FloorToInt(MaxFuel(burner) - Fuel(burner)));
        }

        /// <summary>
        /// Puts <paramref name="units"/> of fuel into a piece this client owns, with the effect vanilla plays
        /// for one unit, once. Callers check ownership and <see cref="Room"/> first.
        ///
        /// The fire goes through its own RPC, which runs here and now because this client owns it, and
        /// clamps to the cap on its own. The oven and the hot tub only have a one-unit RPC, which plays
        /// its sound - and the oven logs a line - for every unit, so their fuel is written directly, which
        /// is all that RPC does on the owner.
        /// </summary>
        internal static void Add(Component burner, int units) {
            if (units <= 0) { return; }
            switch (burner) {
                case Fireplace fire:
                    fire.m_nview.InvokeRPC("RPC_AddFuelAmount", (float)units);
                    break;
                case CookingStation oven:
                    oven.SetFuel(oven.GetFuel() + units);
                    oven.m_fuelAddedEffects.Create(oven.transform.position, oven.transform.rotation, oven.transform);
                    break;
                case Smelter tub:
                    tub.SetFuel(tub.GetFuel() + units);
                    tub.m_fuelAddedEffects.Create(tub.transform.position, tub.transform.rotation, tub.transform);
                    break;
            }
        }

        /// <summary>The fuel-burning component on a networked object, for a handoff request naming it.</summary>
        internal static Component Find(GameObject root) {
            if (root == null) { return null; }
            Component burner = root.GetComponentInChildren<Fireplace>();
            if (burner != null && Burns(burner)) { return burner; }
            burner = root.GetComponentInChildren<CookingStation>();
            if (burner != null && Burns(burner)) { return burner; }
            burner = root.GetComponentInChildren<Smelter>();
            return burner != null && Burns(burner) ? burner : null;
        }
    }
}
