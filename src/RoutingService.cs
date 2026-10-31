using System;
using System.Collections.Generic;
using Timberborn.BlockObjectPickingSystem;
using Timberborn.BlockObjectTools;
using Timberborn.BlockSystem;
using Timberborn.Coordinates;
using Timberborn.PathSystem;
using Timberborn.SingletonSystem;
using Timberborn.TubeSystem;
using UnityEngine;

namespace Pfadfinder
{
    /// <summary>
    /// Turns the vanilla L-shaped drag of the path and tubeway tools into a route between the same two
    /// end tiles, or with "Route across levels" to the tile under the cursor on another height level,
    /// through existing stairs. Tiles are checked with the game's own placement validation (a hidden
    /// preview of the piece, repositioned per tile), so the route avoids exactly what the tool would refuse.
    /// </summary>
    public class RoutingService : ILoadableSingleton, IUnloadableSingleton
    {
        public static RoutingService Instance { get; private set; }

        readonly PreviewFactory _previewFactory;
        readonly IBlockService _blockService;
        readonly BlockObjectPreviewPicker _previewPicker;

        readonly GridRouter _router = new GridRouter();
        readonly NavEdgeReader _navEdges = new NavEdgeReader();
        readonly List<Vector3Int> _route = new List<Vector3Int>();
        readonly HashSet<Vector2Int> _lTiles = new HashSet<Vector2Int>();

        Preview _validationPreview;
        PlaceableBlockObjectSpec _validationTemplate;

        // Per-drag state. A drag starts when the calls resume after a pause, or the level or
        // template changes, and ends when it's placed.
        int _lastFrame = -100;
        int _z;
        PlaceableBlockObjectSpec _template;
        bool _isTube; // routing tubeways: existing tubes are the network to join, not paths
        Orientation _orientation;
        FlipMode _flipMode;

        // Last route: reused while the ends stay put, and shown while the next one is being searched.
        Vector3Int _lastA, _lastB;
        bool _hasLast;
        List<Placement> _lastResult;

        bool _loggedError;

        public RoutingService(PreviewFactory previewFactory, IBlockService blockService, BlockObjectPreviewPicker previewPicker)
        {
            _previewFactory = previewFactory;
            _blockService = blockService;
            _previewPicker = previewPicker;
        }

        public void Load()
        {
            Instance = this;
        }

        public void Unload()
        {
            if (Instance == this) Instance = null;
            DestroyValidationPreview();
        }

        /// <summary>
        /// Tools that lay a walkable network one tile at a time with the L-shaped drag: 1x1x1 pieces
        /// with a PathSpec and the TwoSegmentLine layout. In the base game that's Path, Tubeway and
        /// Vertical Tubeway (fences and power shafts drag the same way but have no PathSpec).
        /// Tubeways only while "Route tubeways" is on.
        /// </summary>
        public static bool IsRoutableTemplate(PlaceableBlockObjectSpec spec)
        {
            try
            {
                return spec != null
                       && spec.Layout == BlockObjectLayout.TwoSegmentLine
                       && spec.GetSpec<BlockObjectSpec>()?.Size == Vector3Int.one
                       && spec.GetSpec<PathSpec>() != null
                       && (spec.GetSpec<TubeSpec>() == null || (PfadfinderSettings.Instance?.RouteTubeways.Value ?? true));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Called with the vanilla placements for every preview frame and for the final click.
        /// Returns the placements to use instead, or null to keep vanilla.
        /// </summary>
        /// <param name="endRay">the cursor ray of the drag end, if known (for routing across levels)</param>
        public List<Placement> Reroute(PlaceableBlockObjectSpec template, List<Placement> vanilla, Ray? endRay, bool isAction)
        {
            try
            {
                if (BypassHeld()) return null;
                var result = Route(template, vanilla, endRay, isAction);
                // The tool has a fixed number of preview slots and cuts the vanilla L off at that
                // length too. A detour can be longer, so cut it off the same way: the route runs from the
                // drag start, so the player gets its first part.
                int capacity = PreviewCapacity(template);
                if (result != null && result.Count > capacity) result = result.GetRange(0, capacity);
                if (isAction) EndDrag();
                return result;
            }
            catch (Exception e)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    Debug.LogError("[Pfadfinder] Routing failed, falling back to the vanilla tool: " + e);
                }
                EndDrag();
                return null;
            }
        }

        /// <summary>Alt held gives the vanilla L-shape, or with "Hold Alt to route" set, Alt not held does.</summary>
        static bool BypassHeld()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            bool altHeld = keyboard != null && keyboard.altKey.isPressed;
            bool altRoutes = PfadfinderSettings.Instance?.AltEnablesRouting.Value ?? false;
            return altHeld != altRoutes;
        }

        /// <summary>
        /// Number of preview slots the tool's PreviewPlacer has (PreviewPlacerFactory sizes it with
        /// Layout.GetPreviewCount(), 40 for the path's TwoSegmentLine). More placements throw.
        /// </summary>
        static int PreviewCapacity(PlaceableBlockObjectSpec template)
        {
            try
            {
                int count = template.Layout.GetPreviewCount();
                if (count > 0) return count;
            }
            catch
            {
                // unknown layout: RoutedCallback's safety net still applies
            }
            return int.MaxValue;
        }

        List<Placement> Route(PlaceableBlockObjectSpec template, List<Placement> vanilla, Ray? endRay, bool isAction)
        {
            if (vanilla.Count < 2) return null; // hovering or single tile

            int z = vanilla[0].Coordinates.z;
            _lTiles.Clear();
            foreach (var placement in vanilla)
            {
                var c = placement.Coordinates;
                if (c.z != z) return null; // not a levelled drag: leave it alone
                _lTiles.Add(new Vector2Int(c.x, c.y));
            }

            // The vanilla L starts at the drag start and ends at the (possibly cut-off) far end, both on
            // the start's level: the game projects the cursor onto it. Across levels, use the tile the
            // cursor actually points at instead.
            var a = vanilla[0].Coordinates;
            var b = vanilla[vanilla.Count - 1].Coordinates;
            var cursor = AcrossLevels() ? PickEnd(template, endRay) : null;
            if (cursor.HasValue && cursor.Value.z != z) b = cursor.Value;
            if (a == b) return null;

            int frame = Time.frameCount;
            if (frame - _lastFrame > 2 || z != _z || !ReferenceEquals(template, _template))
                StartDrag(template, vanilla[0], z);
            _lastFrame = frame;

            if (_hasLast && _lastA == a && _lastB == b) return _lastResult;
            if (!EnsureValidationPreview(template)) return null;

            var size = _blockService.Size;
            var result = _router.FindRoute(a, b, size.x, size.y, Classify, _lTiles, _route);
            // Tile checks are spread over frames while previewing, but a click gets the finished route.
            // Every pass adds to the tile cache, so this ends.
            while (isAction && result == RouteResult.BudgetExceeded)
                result = _router.FindRoute(a, b, size.x, size.y, Classify, _lTiles, _route);
            if (result == RouteResult.BudgetExceeded)
                return _hasLast ? _lastResult : null; // still searching: keep the previous route on screen

            List<Placement> placements = null;
            if (result == RouteResult.Found)
            {
                placements = new List<Placement>(_route.Count);
                foreach (var tile in _route)
                {
                    if (_router.Cached(tile).IsExisting) continue;
                    placements.Add(new Placement(tile, _orientation, _flipMode));
                }
            }

            _lastA = a;
            _lastB = b;
            _hasLast = true;
            _lastResult = placements; // null (no route) also cached: keep showing vanilla
            return placements;
        }

        static bool AcrossLevels() => PfadfinderSettings.Instance?.RouteAcrossLevels.Value ?? true;

        /// <summary>Where the tool would put the piece under the cursor, as the drag start is picked.</summary>
        Vector3Int? PickEnd(PlaceableBlockObjectSpec template, Ray? endRay) =>
            endRay.HasValue ? _previewPicker.CenteredPreviewCoordinates(template, Orientation.Cw0, endRay.Value)?.Coordinates : null;

        void StartDrag(PlaceableBlockObjectSpec template, Placement start, int z)
        {
            EndDrag();
            _z = z;
            _template = template;
            _isTube = template.GetSpec<TubeSpec>() != null;
            _orientation = start.Orientation;
            _flipMode = start.FlipMode;
        }

        void EndDrag()
        {
            _lastFrame = -100;
            _hasLast = false;
            _lastResult = null;
            _router.ResetCache();
            _navEdges.Clear();
        }

        Tile Classify(int x, int y, int z)
        {
            var c = new Vector3Int(x, y, z);
            // The block grid (MapSize.TotalSize) reaches above the highest terrain level, so
            // platforms up there are inside it.
            if (!_blockService.Contains(c)) return Tile.Blocked;

            var existing = ExistingNetworkPiece(_blockService.GetPathObjectAt(c), c);
            if (existing.Kind != TileKind.Unknown) return existing;

            // The same per-tile check the tool's own previews go through. It compares occupations,
            // so it allows tiles under overhangs and platforms, and over overridable stumps and dead
            // bushes (placing the path deletes those), and refuses buildings, finished or not.
            _validationPreview.Reposition(new Placement(c, _orientation, _flipMode));
            return _validationPreview.BlockObject.IsValid() ? Tile.Free : Tile.Blocked;
        }

        /// <summary>
        /// GetPathObjectAt is whatever fills the tile's Path occupation: also buildings (finished or
        /// not), stumps and dead bushes. Like the game's PathService.IsPath, only objects with a PathSpec
        /// are walkable (unfinished ones count: they'll be built anyway), and each tool only joins its
        /// own kind (tubes for tubeways, the rest for paths). Which ways a piece can be walked comes from
        /// its navigation edges: paths and tubes on all four sides, gates, bridges and airlocks along one
        /// axis, stairs at the bottom front and the top back with a climb in between (and tubes and
        /// vertical tubeways up and down). Zipline towers have a PathSpec but no such edges. Buildings
        /// with an entrance (district center, tubeway and zipline stations, earth recultivator) do declare
        /// edges, but they're entered through their door, not walked across. Both return Unknown and go
        /// to the placement check, which blocks them (a route may still end at one: the target tile is
        /// always allowed).
        /// </summary>
        Tile ExistingNetworkPiece(BlockObject obj, Vector3Int c)
        {
            if (obj == null || obj.IsPreview || !obj.HasComponent<PathSpec>()) return Tile.Unknown;
            if (obj.HasComponent<TubeSpec>() != _isTube || obj.HasEntrance) return Tile.Unknown;
            var moves = _navEdges.MovesAt(obj, c) ?? FallbackMoves(obj);
            return moves != 0 ? new Tile(TileKind.Existing, moves) : Tile.Unknown;
        }

        /// <summary>
        /// If the navigation edges can't be read: single-tile pieces on all four sides, straight 1xN pieces
        /// (bridges, airlocks) along their length (zipline beams have that shape but are full blocks past
        /// their first tile), anything else not at all.
        /// </summary>
        static byte FallbackMoves(BlockObject obj)
        {
            var size = obj.GetComponent<BlockObjectSpec>()?.Size ?? Vector3Int.zero;
            if (size == Vector3Int.one) return Moves.Horizontal;
            if (size.z != 1 || Math.Min(size.x, size.y) != 1) return 0;

            bool sameX = true, sameY = true;
            int count = 0;
            Vector3Int first = default;
            foreach (var block in obj.PositionedBlocks.GetAllBlocks())
            {
                if (block.Occupation.IsFull()) return 0;
                if (count++ == 0) first = block.Coordinates;
                sameX &= block.Coordinates.x == first.x;
                sameY &= block.Coordinates.y == first.y;
            }
            if (count < 2) return 0;
            if (sameX) return Moves.AlongY;
            return sameY ? Moves.AlongX : (byte)0;
        }

        /// <summary>A hidden preview of the template, kept for the session; false if it can't be made.</summary>
        bool EnsureValidationPreview(PlaceableBlockObjectSpec template)
        {
            if (ReferenceEquals(template, _validationTemplate)) return _validationPreview != null;
            DestroyValidationPreview();
            _validationTemplate = template;
            try
            {
                _validationPreview = _previewFactory.Create(template);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Pfadfinder] Could not create a validation preview, paths won't be routed: " + e.Message);
            }
            return _validationPreview != null;
        }

        void DestroyValidationPreview()
        {
            if (_validationPreview != null && _validationPreview.GameObject) UnityEngine.Object.Destroy(_validationPreview.GameObject);
            _validationPreview = null;
            _validationTemplate = null;
        }
    }
}
