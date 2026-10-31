using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Pfadfinder
{
    /// <summary>
    /// The district's path range overlay is drawn when the last path preview is shown
    /// (PathRangeDrawer.OnPreviewSelect). With "Hide build range while routing" set, skip it for routed drags.
    /// The type is internal to the game, so it's looked up by name and the patch is skipped if it's gone.
    /// </summary>
    [HarmonyPatch]
    static class RangeOverlayPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("Timberborn.BuildingsNavigation.PathRangeDrawer"), "OnPreviewSelect");

        static bool Prepare()
        {
            if (TargetMethod() != null) return true;
            Debug.LogWarning("[Pfadfinder] PathRangeDrawer.OnPreviewSelect not found - \"Hide build range while routing\" has no effect.");
            return false;
        }

        static bool Prefix() =>
            !(RoutedCallback.ShowingRoutedPreview && (PfadfinderSettings.Instance?.HideRangeWhileRouting.Value ?? false));
    }
}
