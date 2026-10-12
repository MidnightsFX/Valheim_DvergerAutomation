using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// The short line a Seeking Table draws to the chest an item was taken from: the workbench
    /// extension's connection effect, tinted to the table's blue.
    ///
    /// Drawn the way vanilla draws that effect (<c>StationExtension.StartConnectionEffect</c>): the effect is
    /// built one metre long down its Z axis, its emitter scales with the transform, so it is pointed at
    /// the target and stretched to the distance, then simply destroyed when its time is up.
    /// </summary>
    internal static class RetrievalBeam {
        internal const string PrefabName = "DA_RetrievalBeam";

        private static GameObject prefab;
        private static GameObject fallback;

        /// <summary>
        /// Registers the bundle's line with Jotunn, so the vanilla shader and textures it names through
        /// mocks are swapped in. Null when the bundle predates the table; the vanilla line stands in.
        /// </summary>
        internal static void Register(GameObject bundlePrefab) {
            if (bundlePrefab == null) { return; }
            prefab = bundlePrefab;
            PrefabManager.Instance.AddPrefab(new CustomPrefab(bundlePrefab, fixReference: true));
        }

        internal static void Play(Vector3 from, Vector3 to, float seconds) {
            GameObject template = Template();
            if (template == null) { return; }
            Vector3 span = to - from;
            float distance = span.magnitude;
            if (distance < 0.1f) { return; }

            GameObject line = Object.Instantiate(template, from, Quaternion.LookRotation(span / distance));
            line.transform.localScale = new Vector3(1f, 1f, distance);
            Object.Destroy(line, seconds);
        }

        // The vanilla workbench line, untinted, for a bundle that has no line of its own. It is not a
        // networked prefab, so it is reached through the extension that uses it.
        private static GameObject Template() {
            if (prefab != null) { return prefab; }
            if (fallback == null && ZNetScene.instance != null) {
                GameObject extension = ZNetScene.instance.GetPrefab("piece_workbench_ext1");
                StationExtension station = extension != null ? extension.GetComponent<StationExtension>() : null;
                fallback = station != null ? station.m_connectionPrefab : null;
            }
            return fallback;
        }
    }
}
