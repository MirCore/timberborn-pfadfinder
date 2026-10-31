using System;
using System.Collections.Generic;
using UnityEngine;

namespace Pfadfinder
{
    /// <summary>What a single tile is, for routing.</summary>
    public enum TileKind : byte
    {
        Unknown = 0,
        Blocked = 1,
        Free = 2,     // a new piece can be built here
        Existing = 3, // already part of the network: walk on it, don't build
    }

    /// <summary>
    /// Step directions: 0 +x, 1 -x, 2 +y, 3 -y, 4 +z (up), 5 -z (down). The opposite of d is d ^ 1.
    /// A tile's moves are a bit mask of the directions it can be left or entered by.
    /// </summary>
    public static class Moves
    {
        public const byte Horizontal = 0b001111;
        public const byte AlongX = 0b000011;
        public const byte AlongY = 0b001100;
        public const int Count = 6;

        public static readonly Vector3Int[] Step =
        {
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };

        public static byte Bit(int d) => (byte)(1 << d);

        /// <summary>The direction of a unit step, or -1 if delta isn't one.</summary>
        public static int DirectionOf(Vector3Int delta)
        {
            for (int d = 0; d < Count; d++)
                if (Step[d] == delta) return d;
            return -1;
        }
    }

    public readonly struct Tile
    {
        public readonly TileKind Kind;
        public readonly byte Moves;

        public Tile(TileKind kind, byte moves)
        {
            Kind = kind;
            Moves = moves;
        }

        public static readonly Tile Blocked = new Tile(TileKind.Blocked, 0);
        public static readonly Tile Free = new Tile(TileKind.Free, Pfadfinder.Moves.Horizontal);
        public static readonly Tile Unknown = new Tile(TileKind.Unknown, 0);

        public bool IsExisting => Kind == TileKind.Existing;
        public bool Allows(int d) => (Moves & (1 << d)) != 0;
    }

    public enum RouteResult
    {
        Found,
        NoRoute,
        BudgetExceeded, // ran out of tile checks this frame; try again next frame (cache persists)
        TooLarge,
    }

    /// <summary>
    /// Pure A* on a block grid. Knows nothing about Timberborn: tiles are classified lazily through a
    /// callback and memoized until <see cref="ResetCache"/>. A step from one tile to the next needs both
    /// tiles to allow it (each tile's move mask), so bridges can be one-way-axis and stairs can link
    /// levels. New tiles only ever allow horizontal steps; levels change only through existing pieces.
    /// The search stays between the levels of the two ends.
    /// State = (tile, incoming direction) so that turns can be penalised, which keeps routes
    /// straight instead of zig-zagging.
    /// </summary>
    public sealed class GridRouter
    {
        public int Margin = 10;                  // how far the search may stray outside the start/end box
        public int MaxVolume = 160 * 160 * 4;    // bigger boxes (tiles x levels) fall back to vanilla
        public int MaxNewChecksPerSearch = 1500; // expensive game validations per call
        public float FreeCost = 1f;
        public float PreferredFreeCost = 0.95f;  // tiles on the vanilla L-shape: keeps the vanilla L when it's clear
        public float ExistingPathCost = 0.6f;    // reuse existing paths
        public float VerticalCost = 0.6f;        // one level up or down inside stairs
        public float TurnPenalty = 0.3f;

        const int NoDir = Moves.Count;
        const int Dirs = Moves.Count + 1;

        readonly Dictionary<long, Tile> _cache = new Dictionary<long, Tile>();

        // Reusable search buffers.
        float[] _g = Array.Empty<float>();
        int[] _came = Array.Empty<int>();
        int[] _seen = Array.Empty<int>();   // generation stamp: g/came valid for this search
        int[] _closed = Array.Empty<int>();
        int _generation;
        readonly MinHeap _open = new MinHeap();

        public int CacheSize => _cache.Count;

        public void ResetCache() => _cache.Clear();

        static long Key(int x, int y, int z) => ((long)(x & 0xFFFFF) << 40) | ((long)(y & 0xFFFFF) << 20) | (uint)(z & 0xFFFFF);

        /// <param name="mapWidth">map size in x (tiles outside are blocked)</param>
        /// <param name="mapHeight">map size in y</param>
        /// <param name="classify">expensive tile check, called once per tile until ResetCache</param>
        /// <param name="preferred">tiles on a's level to slightly prefer (the vanilla L), may be null</param>
        /// <param name="path">receives the tiles from a to b inclusive</param>
        public RouteResult FindRoute(
            Vector3Int a, Vector3Int b, int mapWidth, int mapHeight,
            Func<int, int, int, Tile> classify, HashSet<Vector2Int> preferred, List<Vector3Int> path)
        {
            path.Clear();
            if (a == b)
            {
                path.Add(a);
                return RouteResult.Found;
            }

            int minX = Math.Max(0, Math.Min(a.x, b.x) - Margin);
            int minY = Math.Max(0, Math.Min(a.y, b.y) - Margin);
            int maxX = Math.Min(mapWidth - 1, Math.Max(a.x, b.x) + Margin);
            int maxY = Math.Min(mapHeight - 1, Math.Max(a.y, b.y) + Margin);
            int minZ = Math.Min(a.z, b.z);
            int maxZ = Math.Max(a.z, b.z);
            int w = maxX - minX + 1;
            int h = maxY - minY + 1;
            int levels = maxZ - minZ + 1;
            if (w <= 0 || h <= 0 || (long)w * h * levels > MaxVolume) return RouteResult.TooLarge;

            int states = w * h * levels * Dirs;
            EnsureBuffers(states);
            _generation++;
            if (_generation == int.MaxValue)
            {
                Array.Clear(_seen, 0, _seen.Length);
                Array.Clear(_closed, 0, _closed.Length);
                _generation = 1;
            }
            _open.Clear();

            int newChecks = 0;
            // Weighting the heuristic by the cost of a new tile (not the cheaper existing-path cost)
            // cuts tile checks ~10x on long drags; routes stay the same in practice.
            float minStep = Math.Min(FreeCost, PreferredFreeCost);

            int StateOf(int x, int y, int z, int dir) =>
                ((((z - minZ) * h) + (y - minY)) * w + (x - minX)) * Dirs + dir;

            int start = StateOf(a.x, a.y, a.z, NoDir);
            _g[start] = 0f;
            _came[start] = -1;
            _seen[start] = _generation;
            _open.Push(Heuristic(a, b, minStep), start);

            while (_open.Count > 0)
            {
                int cur = _open.Pop();
                if (_closed[cur] == _generation) continue;
                _closed[cur] = _generation;

                int dir = cur % Dirs;
                var c = TileOf(cur / Dirs, w, h, minX, minY, minZ);

                if (c == b)
                {
                    Reconstruct(cur, w, h, minX, minY, minZ, path);
                    return RouteResult.Found;
                }

                float gCur = _g[cur];
                // The start tile isn't classified: it may be left sideways like a new tile.
                byte curMoves = _cache.TryGetValue(Key(c.x, c.y, c.z), out var curTile) ? curTile.Moves : Moves.Horizontal;
                for (int d = 0; d < Moves.Count; d++)
                {
                    if ((curMoves & (1 << d)) == 0) continue;
                    var n = c + Moves.Step[d];
                    if (n.x < minX || n.y < minY || n.z < minZ || n.x > maxX || n.y > maxY || n.z > maxZ) continue;

                    int next = StateOf(n.x, n.y, n.z, d);
                    if (_closed[next] == _generation) continue;

                    long key = Key(n.x, n.y, n.z);
                    if (!_cache.TryGetValue(key, out var tile))
                    {
                        if (newChecks >= MaxNewChecksPerSearch) return RouteResult.BudgetExceeded;
                        tile = classify(n.x, n.y, n.z);
                        _cache[key] = tile;
                        newChecks++;
                    }

                    if (tile.Kind == TileKind.Blocked && n == b) tile = Tile.Free; // always allow reaching the target
                    if (tile.Kind == TileKind.Blocked || !tile.Allows(d ^ 1)) continue;

                    float step;
                    if (d >= 4) step = VerticalCost;
                    else if (tile.IsExisting) step = ExistingPathCost;
                    else if (preferred != null && n.z == a.z && preferred.Contains(new Vector2Int(n.x, n.y))) step = PreferredFreeCost;
                    else step = FreeCost;
                    if (dir < 4 && d < 4 && dir != d) step += TurnPenalty; // stairs force their own turns

                    float gNext = gCur + step;
                    if (_seen[next] == _generation && gNext >= _g[next]) continue;

                    _seen[next] = _generation;
                    _g[next] = gNext;
                    _came[next] = cur;
                    _open.Push(gNext + Heuristic(n, b, minStep), next);
                }
            }

            return RouteResult.NoRoute;
        }

        /// <summary>Returns the memoized classification of a tile, or Unknown.</summary>
        public Tile Cached(Vector3Int c) =>
            _cache.TryGetValue(Key(c.x, c.y, c.z), out var t) ? t : Tile.Unknown;

        float Heuristic(Vector3Int p, Vector3Int b, float minStep) =>
            (Math.Abs(p.x - b.x) + Math.Abs(p.y - b.y)) * minStep + Math.Abs(p.z - b.z) * VerticalCost;

        static Vector3Int TileOf(int tile, int w, int h, int minX, int minY, int minZ) =>
            new Vector3Int(tile % w + minX, tile / w % h + minY, tile / (w * h) + minZ);

        void Reconstruct(int state, int w, int h, int minX, int minY, int minZ, List<Vector3Int> path)
        {
            int lastTile = -1;
            while (state >= 0)
            {
                int tile = state / Dirs;
                if (tile != lastTile)
                {
                    path.Add(TileOf(tile, w, h, minX, minY, minZ));
                    lastTile = tile;
                }
                state = _came[state];
            }
            path.Reverse();
        }

        void EnsureBuffers(int states)
        {
            if (_g.Length >= states) return;
            int size = Math.Max(states, _g.Length * 2);
            _g = new float[size];
            _came = new int[size];
            _seen = new int[size];
            _closed = new int[size];
            _generation = 0;
        }

        /// <summary>Small binary min-heap (netstandard2.1 has no PriorityQueue).</summary>
        sealed class MinHeap
        {
            float[] _keys = new float[256];
            int[] _vals = new int[256];
            public int Count { get; private set; }

            public void Clear() => Count = 0;

            public void Push(float key, int val)
            {
                if (Count == _keys.Length)
                {
                    Array.Resize(ref _keys, Count * 2);
                    Array.Resize(ref _vals, Count * 2);
                }
                int i = Count++;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (_keys[parent] <= key) break;
                    _keys[i] = _keys[parent];
                    _vals[i] = _vals[parent];
                    i = parent;
                }
                _keys[i] = key;
                _vals[i] = val;
            }

            public int Pop()
            {
                int result = _vals[0];
                Count--;
                if (Count == 0) return result;
                float key = _keys[Count];
                int val = _vals[Count];
                int i = 0;
                while (true)
                {
                    int child = (i << 1) + 1;
                    if (child >= Count) break;
                    if (child + 1 < Count && _keys[child + 1] < _keys[child]) child++;
                    if (_keys[child] >= key) break;
                    _keys[i] = _keys[child];
                    _vals[i] = _vals[child];
                    i = child;
                }
                _keys[i] = key;
                _vals[i] = val;
                return result;
            }
        }
    }
}
