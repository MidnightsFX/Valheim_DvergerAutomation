using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Epic Loot compatibility, in two halves.
    ///
    /// Outward: registers the autosorter's container pool as an Epic Loot *inventory provider*, so the
    /// enchanting table spends runestones, shards and dust out of linked chests. Epic Loot keeps its own
    /// provider-based accounting rather than going through <see cref="Inventory.CountItems"/>, so none of
    /// the Harmony patches in <see cref="CraftFromStoragePatches"/> reach it - this is the only route in.
    ///
    /// Inward: <see cref="IsProtectedItem"/> keeps enchanted gear in linked chests from being spent as
    /// plain crafting material. Vanilla consumption matches on <c>m_shared.m_name</c>, which an enchanted
    /// item shares with its mundane counterpart, so without this an upgrade recipe can quietly destroy a
    /// legendary sitting in a chest.
    ///
    /// The whole API is reflection-bound (see common/EpicLootAPI): with Epic Loot absent every call is a
    /// no-op returning false, so nothing here needs guarding beyond <see cref="Active"/>.
    /// </summary>
    internal static class EpicLootIntegration {
        /// <summary>Epic Loot's BepInEx plugin GUID, used for the soft dependency that orders load.</summary>
        internal const string EpicLootGUID = "randyknapp.mods.epicloot";

        private static readonly List<Container> Empty = new List<Container>();

        /// <summary>True once Epic Loot is present and the inventory provider is registered.</summary>
        internal static bool Active { get; private set; }

        internal static void Register() {
            if (Active) { return; }

            if (!EpicLootAPI.EpicLoot.IsLoaded()) {
                Logger.LogDebug("[Autosorter] Epic Loot not present; skipping integration.");
                return;
            }

            Active = EpicLootAPI.EpicLoot.RegisterInventoryProvider(
                DvergerAutomation.PluginGUID,
                GetItems,
                CountItem,
                RemoveItem,
                RemoveExactItem);

            if (Active) {
                Logger.LogInfo($"[Autosorter] Registered inventory provider with Epic Loot {EpicLootAPI.EpicLoot.GetPluginVersion()}.");
            } else {
                Logger.LogWarning("[Autosorter] Epic Loot is present but refused the inventory provider registration.");
            }
        }

        internal static void Unregister() {
            if (!Active) { return; }
            EpicLootAPI.EpicLoot.UnregisterInventoryProvider(DvergerAutomation.PluginGUID);
            Active = false;
        }

        /// <summary>
        /// True when this item must not be consumed as a by-name crafting material.
        ///
        /// Ordered so the common case costs nothing: with Epic Loot absent it stops at
        /// <see cref="Active"/>, and Epic Loot only enchants equipment, which never stacks in Valheim -
        /// so materials, which are what recipes actually ask for, stop at the stack-size check before
        /// reaching the reflection call. That matters because the per-frame aggregate in
        /// <see cref="ContainerNetwork"/> walks every item in every linked chest.
        /// </summary>
        internal static bool IsProtectedItem(ItemDrop.ItemData item) {
            if (!Active || item == null || item.m_shared == null) { return false; }
            if (item.m_shared.m_maxStackSize > 1) { return false; }
            return EpicLootAPI.EpicLoot.IsMagicItem(item);
        }

        // ---- provider callbacks -------------------------------------------------

        // Same pool the crafting and building paths use: the station's chests when the player is at a
        // station (Epic Loot's enchanting table is one), otherwise every hub whose radius covers them.
        private static List<Container> Pool() {
            Player player = Player.m_localPlayer;
            if (player == null) { return Empty; }
            CraftingStation station = player.GetCurrentCraftingStation();
            return station != null
                ? ContainerNetwork.GetContainersForStation(station)
                : ContainerNetwork.GetContainersNearPoint(player.transform.position);
        }

        // Live instances, deliberately including magic items: this list is what the table may act *on*,
        // and Epic Loot addresses those by reference through RemoveExactItem. IsProtectedItem only guards
        // the by-name material accounting below.
        //
        // Frame-memoized for the same reason ContainerNetwork memoizes its aggregate: the enchanting UI
        // asks once per frame while it is open, and rebuilding this across a few hundred linked chests
        // every frame is exactly the stall that memo exists to avoid. Reusing the list is safe because
        // Epic Loot copies the contents out rather than holding the reference.
        private static readonly List<ItemDrop.ItemData> ItemsResult = new List<ItemDrop.ItemData>();
        private static int itemsFrame = -1;

        private static List<ItemDrop.ItemData> GetItems() {
            if (itemsFrame == Time.frameCount) { return ItemsResult; }

            ItemsResult.Clear();
            foreach (Container container in Pool()) {
                if (container == null) { continue; }
                Inventory inv = container.GetInventory();
                if (inv == null) { continue; }
                ItemsResult.AddRange(inv.GetAllItems());
            }
            itemsFrame = Time.frameCount;
            return ItemsResult;
        }

        private static int CountItem(string name) {
            return string.IsNullOrEmpty(name) ? 0 : ContainerNetwork.CountInPool(Pool(), name);
        }

        private static int RemoveItem(string name, int amount) {
            if (string.IsNullOrEmpty(name) || amount <= 0) { return 0; }
            return CraftFromStoragePatches.RemoveFromContainers(Pool(), name, amount, -1);
        }

        // Reference match, per Epic Loot's contract: magic data lives on the instance, so finding the
        // stack by name would consume the wrong enchanted item.
        private static int RemoveExactItem(ItemDrop.ItemData item, int amount) {
            if (item == null || amount <= 0) { return 0; }
            foreach (Container container in Pool()) {
                if (container == null) { continue; }
                Inventory inv = container.GetInventory();
                if (inv == null || !inv.ContainsItem(item)) { continue; }

                CraftFromStoragePatches.ClaimOwnership(container);
                int take = Mathf.Min(item.m_stack, amount);
                inv.RemoveItem(item, take);
                return take;
            }
            return 0;
        }
    }
}
