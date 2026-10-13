using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    // Crafting and building that have to wait a moment for materials another player's chest is sending,
    // driven by the gates in CraftFromStoragePatches.

    /// <summary>
    /// Keeps a finished craft bar full while the craft's materials are still on their way from another
    /// player's chest, instead of failing the craft and making the player start the bar again.
    ///
    /// <c>InventoryGui.UpdateRecipe</c> runs the bar: once it fills it calls <c>DoCrafting</c> and resets
    /// the timer, whatever DoCrafting did. So when the craft has to wait, DoCrafting is skipped and the
    /// timer is put back to where the frame found it. Vanilla then fills the bar and tries again on the
    /// next frame, and the frame after, until the materials are in or patience runs out.
    /// </summary>
    internal static class CraftHold {
        // A round trip to another player is a fraction of this. Longer means nobody is answering.
        private const float Patience = 3f;

        private static bool inUpdateRecipe;
        private static float timerBefore;
        private static int heldFrame = -1;
        private static float since;

        internal static void Enter(InventoryGui gui) {
            inUpdateRecipe = true;
            timerBefore = gui.m_craftTimer;
        }

        internal static void Exit(InventoryGui gui) {
            inUpdateRecipe = false;
            if (heldFrame == Time.frameCount) { gui.m_craftTimer = timerBefore; }
        }

        /// <summary>
        /// Called when the craft has to wait: true while it should be held, false once it should be
        /// given up on. Only a craft run from the bar can be held - anything else calling DoCrafting
        /// has no timer to put back.
        /// </summary>
        internal static bool KeepWaiting() {
            if (!inUpdateRecipe) { return false; }
            // Not carrying on from the frame before, so this is a new wait.
            if (heldFrame != Time.frameCount - 1) { since = Time.time; }
            if (Time.time - since > Patience) { return false; }
            heldFrame = Time.frameCount;
            return true;
        }
    }

    /// <summary>
    /// Finishes a Hammer placement that had to wait for materials from another player's chest.
    ///
    /// The click that could not be paid for is turned down, as it has to be - vanilla places the piece
    /// before it spends. Once the materials are in, the click is pressed again on the player's behalf by
    /// setting the time vanilla's own buffered-click check reads, so the placement goes through vanilla's
    /// whole path: stamina, the ghost's validity at that moment, durability, skill. Only for the piece
    /// that was clicked, only while it is still selected, and only for a short while.
    /// </summary>
    internal static class PlaceRetry {
        private const float Patience = 3f;

        private static Piece piece;
        private static float until;
        // The frame the gate in front of TryPlacePiece last turned this piece down, and the frame a press
        // was last made for it.
        private static int blockedFrame = -1;
        private static int pressedFrame = -1;

        /// <summary>The gate turned a placement down for want of materials that are on their way.</summary>
        internal static void Arm(Piece wanted) {
            blockedFrame = Time.frameCount;
            if (piece == wanted) { return; }
            piece = wanted;
            until = Time.time + Patience;
            pressedFrame = -1;
        }

        internal static void Disarm() {
            piece = null;
            pressedFrame = -1;
        }

        /// <summary>Run ahead of <c>Player.UpdatePlacement</c>, which is what reads the press.</summary>
        internal static void Tick(Player player) {
            if (piece == null) { return; }
            if (!player.InPlaceMode() || player.GetSelectedPiece() != piece) {
                Disarm();
                return;
            }

            // Vanilla sets the time far into the past when it takes a press. One it took without coming
            // back to the gate was stopped by something else - no stamina, a ghost that is no longer valid -
            // and vanilla has said so; pressing again would only repeat it every frame.
            if (pressedFrame >= 0 && player.m_placePressedTime < 0f) {
                if (blockedFrame != pressedFrame) {
                    Disarm();
                    return;
                }
                pressedFrame = -1;
            }

            if (Time.time > until) {
                Disarm();
                player.Message(MessageHud.MessageType.Center, "$DA_storage_waiting");
                return;
            }
            List<Container> pool = ContainerNetwork.GetContainersNearPoint(player.transform.position);
            if (StorageOwnership.CheckStorage(player, pool, piece.m_resources, null, 0, 1) == StorageReserve.Readiness.Pending) { return; }

            // Vanilla takes the press on the first frame its placement delay allows; until then it is
            // pressed afresh each frame. The gate disarms this when it lets the piece through.
            player.m_placePressedTime = Time.time;
            pressedFrame = Time.frameCount;
        }
    }
}
