using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Timberborn.AreaSelectionSystem;
using Timberborn.BlockObjectTools;
using Timberborn.BlockSystem;
using Timberborn.Coordinates;
using UnityEngine;

namespace Pfadfinder
{
    /// <summary>
    /// Every block-object drag (paths, platforms, buildings…) goes through
    /// AreaPicker.PickBlockObjectArea(template, orientation, flipMode, previewCallback, actionCallback),
    /// once per frame. For the path and tubeway tools we swap in wrapped callbacks: the vanilla code still does all
    /// input handling, previews, validation colours, unlocking, sounds and placing; only the list of
    /// placements it hands to the tool is replaced.
    /// </summary>
    [HarmonyPatch(typeof(AreaPicker), nameof(AreaPicker.PickBlockObjectArea))]
    static class PathToolPatch
    {
        static void Prefix(PlaceableBlockObjectSpec blockObjectSpec,
            ref AreaPicker.BlockObjectAreaCallback previewCallback,
            ref AreaPicker.BlockObjectAreaCallback actionCallback)
        {
            if (RoutingService.Instance == null || !RoutingService.IsRoutableTemplate(blockObjectSpec)) return;
            previewCallback = new RoutedCallback(blockObjectSpec, previewCallback, isAction: false).Invoke;
            actionCallback = new RoutedCallback(blockObjectSpec, actionCallback, isAction: true).Invoke;
        }
    }

    /// <summary>
    /// AreaPicker.GetBlocks(startRay, endRay, …) turns the cursor rays into the vanilla placements right
    /// before each callback, projecting the end onto the start's level. Remember the end ray so routing
    /// across levels can see where the cursor really points. Skipped (no across-level routing) if gone.
    /// </summary>
    [HarmonyPatch]
    static class DragEndPatch
    {
        public static Ray? LastEndRay { get; private set; }

        static MethodBase TargetMethod() => AccessTools.Method(typeof(AreaPicker), "GetBlocks");

        static bool Prepare()
        {
            if (TargetMethod() != null) return true;
            Debug.LogWarning("[Pfadfinder] AreaPicker.GetBlocks not found - routing across levels is off.");
            return false;
        }

        static void Prefix(Ray endRay) => LastEndRay = endRay;
    }

    /// <summary>Stands in for BlockObjectTool.PreviewCallback or ActionCallback and forwards the routed placements.</summary>
    sealed class RoutedCallback
    {
        readonly PlaceableBlockObjectSpec _template;
        readonly AreaPicker.BlockObjectAreaCallback _inner;
        readonly bool _isAction;

        static bool _loggedTooLong;

        /// <summary>True while a routed drag is being previewed (see <see cref="RangeOverlayPatch"/>).</summary>
        public static bool ShowingRoutedPreview { get; private set; }

        public RoutedCallback(PlaceableBlockObjectSpec template, AreaPicker.BlockObjectAreaCallback inner, bool isAction)
        {
            _template = template;
            _inner = inner;
            _isAction = isAction;
        }

        public void Invoke(IEnumerable<Placement> placements)
        {
            var service = RoutingService.Instance;
            if (service == null || placements == null)
            {
                _inner(placements);
                return;
            }

            // AreaPicker hands over a lazy sequence: enumerate it once and give vanilla the same list.
            var vanilla = placements as List<Placement> ?? placements.ToList();
            var routed = service.Reroute(_template, vanilla, DragEndPatch.LastEndRay, _isAction);
            if (routed == null)
            {
                _inner(vanilla);
                return;
            }

            try
            {
                ShowingRoutedPreview = !_isAction;
                _inner(routed);
            }
            catch (IndexOutOfRangeException)
            {
                ShowingRoutedPreview = false;
                // More placements than the preview placer has slots (routes are capped, so this is a
                // safety net). PreviewPlacer.ShowPreviews leaves the list in its _coordinatesCache when it
                // throws, which would make every later call throw too, so clear it before showing vanilla.
                if (!_loggedTooLong)
                {
                    _loggedTooLong = true;
                    Debug.LogWarning($"[Pfadfinder] Route of {routed.Count} tiles was too long for the tool; showing the vanilla L instead.");
                }
                ResetPreviewPlacer();
                if (!_isAction) _inner(vanilla); // never silently build an L the player didn't see
            }
            finally
            {
                ShowingRoutedPreview = false;
            }
        }

        static readonly FieldInfo PreviewPlacerField = AccessTools.Field(typeof(BlockObjectTool), "_previewPlacer");
        static readonly FieldInfo CoordinatesCacheField = AccessTools.Field(typeof(PreviewPlacer), "_coordinatesCache");
        static readonly FieldInfo BuildablePreviewsField = AccessTools.Field(typeof(PreviewPlacer), "_buildablePreviews");

        /// <summary>Puts the tool's PreviewPlacer back into a clean state after it threw half-way.</summary>
        void ResetPreviewPlacer()
        {
            try
            {
                if (!(_inner.Target is BlockObjectTool tool) || PreviewPlacerField == null) return;
                if (!(PreviewPlacerField.GetValue(tool) is PreviewPlacer placer)) return;
                (CoordinatesCacheField?.GetValue(placer) as IList)?.Clear();
                (BuildablePreviewsField?.GetValue(placer) as IList)?.Clear();
                placer.HideAllPreviews();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Pfadfinder] Could not reset the preview placer: " + e.Message);
            }
        }
    }
}
