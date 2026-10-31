using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using UnityEngine;

namespace Pfadfinder
{
    /// <summary>
    /// Reads the walkable edges a block object declares for the beavers' navigation mesh
    /// (BlockObjectNavMeshSettingsSpec: edge groups marked IsPath, each edge from one block to a
    /// neighbouring one, in the object's local coordinates). That's what makes a path walkable on all
    /// four sides, a gate or bridge only along one axis, and stairs enterable at the bottom front and
    /// the top back with a climb in between. The spec types are internal to the game, so they're read
    /// by reflection; if that ever fails, <see cref="MovesAt"/> returns null and callers fall back.
    /// </summary>
    sealed class NavEdgeReader
    {
        static readonly MethodInfo GetSettings = FindGetSettings();
        static bool _loggedFailure;

        // Per object: moves at each of its blocks (world coordinates). Objects don't move during a drag.
        readonly Dictionary<BlockObject, Dictionary<Vector3Int, byte>> _cache =
            new Dictionary<BlockObject, Dictionary<Vector3Int, byte>>();

        public void Clear() => _cache.Clear();

        /// <summary>Directions (see <see cref="Moves"/>) the object can be walked in or out of at c; null if unreadable.</summary>
        public byte? MovesAt(BlockObject obj, Vector3Int c)
        {
            if (!_cache.TryGetValue(obj, out var moves))
            {
                moves = Read(obj);
                _cache[obj] = moves;
            }
            if (moves == null) return null;
            return moves.TryGetValue(c, out var m) ? m : (byte)0;
        }

        static MethodInfo FindGetSettings()
        {
            var type = AccessTools.TypeByName("Timberborn.BlockSystemNavigation.BlockObjectNavMeshSettingsSpec");
            var method = AccessTools.Method(typeof(BaseComponent), nameof(BaseComponent.GetComponent));
            return type == null || method == null ? null : method.MakeGenericMethod(type);
        }

        static Dictionary<Vector3Int, byte> Read(BlockObject obj)
        {
            if (GetSettings == null) return null;
            try
            {
                var moves = new Dictionary<Vector3Int, byte>();
                var settings = GetSettings.Invoke(obj, null);
                if (settings == null) return moves; // no navigation of its own: not walkable
                foreach (var group in Items(Traverse.Create(settings).Property("EdgeGroups").GetValue()))
                {
                    var g = Traverse.Create(group);
                    if (!g.Property("IsPath").GetValue<bool>()) continue;
                    foreach (var edge in Items(g.Property("AddedEdges").GetValue()))
                    {
                        var e = Traverse.Create(edge);
                        var start = obj.TransformCoordinates(e.Property("Start").GetValue<Vector3Int>());
                        var end = obj.TransformCoordinates(e.Property("End").GetValue<Vector3Int>());
                        int d = Moves.DirectionOf(end - start);
                        if (d < 0) continue;
                        // Edges out of an object are one-way; the neighbouring piece adds the way back.
                        // For routing a connection is a connection, so record it at both ends.
                        Add(moves, start, d);
                        Add(moves, end, d ^ 1);
                    }
                }
                return moves;
            }
            catch (Exception e)
            {
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    Debug.LogWarning("[Pfadfinder] Could not read navigation edges, using simpler rules: " + e.Message);
                }
                return null;
            }
        }

        static void Add(Dictionary<Vector3Int, byte> moves, Vector3Int c, int d)
        {
            moves.TryGetValue(c, out var m);
            moves[c] = (byte)(m | Moves.Bit(d));
        }

        /// <summary>Items of a boxed ImmutableArray (empty if it's default).</summary>
        static IEnumerable Items(object immutableArray)
        {
            if (immutableArray == null || Traverse.Create(immutableArray).Property("IsDefault").GetValue<bool>())
                return Array.Empty<object>();
            return (IEnumerable)immutableArray;
        }
    }
}
