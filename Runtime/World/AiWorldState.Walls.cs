using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace AI_Mod.Runtime
{
    internal sealed partial class AiWorldState
    {
        private void RefreshWalls()
        {
            _wallTilemaps.Clear();
            var tilemaps = UnityEngine.Object.FindObjectsOfType<Tilemap>(true);
            foreach (var tilemap in tilemaps)
            {
                if (tilemap == null || tilemap.gameObject == null || tilemap.gameObject.Equals(null))
                {
                    continue;
                }

                var name = tilemap.gameObject.name ?? string.Empty;
                if (!name.Contains("Walls", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("PlayerWall", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryCreateWallTilemap(tilemap, out var wallTilemap))
                {
                    _wallTilemaps.Add(wallTilemap);
                }
            }
        }

        private bool TryCreateWallTilemap(Tilemap tilemap, out WallTilemap wallTilemap)
        {
            wallTilemap = default;
            if (tilemap == null || tilemap.Equals(null))
            {
                return false;
            }

            var identifier = tilemap.gameObject != null ? tilemap.gameObject.name ?? "<unnamed>" : "<unknown>";

            var grid = tilemap.layoutGrid;
            if (grid == null || grid.Equals(null))
            {
                _fallbacks.WarnOnce($"WallTilemapGridMissing:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' missing layout grid; skipping wall registration.");
                return false;
            }

            var cellSize = grid.cellSize;
            if (Mathf.Abs(cellSize.x) < 0.0001f || Mathf.Abs(cellSize.y) < 0.0001f)
            {
                _fallbacks.WarnOnce($"WallTilemapCellSizeInvalid:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' contained a near-zero cell size; skipping wall registration.");
                return false;
            }

            int usedTileCount;
            try
            {
                usedTileCount = tilemap.GetUsedTilesCount();
            }
            catch (Exception ex)
            {
                _fallbacks.WarnOnce($"WallTilemapUsedTilesFallback:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' GetUsedTilesCount failed: {ex.Message}; assuming tiles are present.");
                usedTileCount = -1;
            }

            if (usedTileCount == 0)
            {
                return false;
            }

            var cellBounds = tilemap.cellBounds;
            if (cellBounds.size.x <= 0 || cellBounds.size.y <= 0)
            {
                return false;
            }

            Bounds bounds;
            try
            {
                bounds = ComputeTilemapWorldBounds(tilemap, cellSize, cellBounds);
            }
            catch (Exception ex)
            {
                _fallbacks.WarnOnce($"WallTilemapBoundsFallback:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' world bounds computation failed: {ex.Message}; skipping wall registration.");
                return false;
            }

            if (bounds.size.sqrMagnitude <= 0f)
            {
                _fallbacks.WarnOnce($"WallTilemapEmptyBounds:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' produced empty world bounds; skipping wall registration.");
                return false;
            }

            if (!TryExtractPhaserBounds(tilemap, identifier, out var boundingBoxes))
            {
                return false;
            }

            wallTilemap = new WallTilemap(tilemap, bounds, boundingBoxes);
            return true;
        }

        private bool TryExtractPhaserBounds(Tilemap tilemap, string identifier, out Rect[] boundingBoxes)
        {
            boundingBoxes = Array.Empty<Rect>();

            var go = tilemap.gameObject;
            if (go == null || go.Equals(null))
            {
                _fallbacks.WarnOnce($"WallTilemapGameObjectMissing:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' missing GameObject; skipping wall registration.");
                return false;
            }

            var component = go.GetComponent(Il2CppType.Of<PhaserTilemap>());
            var phaserTilemap = component?.TryCast<PhaserTilemap>();
            if (phaserTilemap == null || phaserTilemap.Equals(null))
            {
                _fallbacks.WarnOnce($"WallTilemapPhaserMissing:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' missing PhaserTilemap; skipping wall registration.");
                return false;
            }

            Il2CppStructArray<float4>? precachedBounds;
            try
            {
                precachedBounds = phaserTilemap.precachedBounds;
            }
            catch (Exception ex)
            {
                _fallbacks.WarnOnce($"WallTilemapPrecBoundsError:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' precached bounds unavailable: {ex.Message}; skipping wall registration.");
                return false;
            }

            if (precachedBounds == null || precachedBounds.Count <= 0)
            {
                _fallbacks.WarnOnce($"WallTilemapPrecBoundsEmpty:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' contains no precached bounds; skipping wall registration.");
                return false;
            }

            var rects = new List<Rect>(precachedBounds.Count);
            var discardedDegenerate = false;

            for (var i = 0; i < precachedBounds.Count; i++)
            {
                var entry = precachedBounds[i];

                var xMin = Mathf.Min(entry.x, entry.z);
                var xMax = Mathf.Max(entry.x, entry.z);
                var yMin = Mathf.Min(entry.y, entry.w);
                var yMax = Mathf.Max(entry.y, entry.w);

                var width = xMax - xMin;
                var height = yMax - yMin;
                if (width <= 0f || height <= 0f)
                {
                    discardedDegenerate = true;
                    continue;
                }

                rects.Add(new Rect(xMin, yMin, width, height));
            }

            if (rects.Count == 0)
            {
                _fallbacks.WarnOnce($"WallTilemapPrecBoundsInvalid:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' precached bounds produced no valid rectangles; skipping wall registration.");
                return false;
            }

            if (discardedDegenerate)
            {
                _fallbacks.InfoOnce($"WallTilemapPrecBoundsDiscarded:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' precached bounds contained degenerate rectangles that were discarded.");
            }

            boundingBoxes = rects.ToArray();
            return true;
        }

        private static Bounds ComputeTilemapWorldBounds(Tilemap tilemap, Vector3 cellSize, BoundsInt cellBounds)
        {
            var minCell = cellBounds.min;
            var maxCell = cellBounds.max;
            var minCorner = new Vector3Int(minCell.x, minCell.y, minCell.z);
            var maxCorner = new Vector3Int(maxCell.x - 1, maxCell.y - 1, minCell.z);
            if (cellBounds.size.z > 0)
            {
                maxCorner.z = maxCell.z - 1;
            }

            var minCenter = tilemap.GetCellCenterWorld(minCorner);
            var maxCenter = tilemap.GetCellCenterWorld(maxCorner);
            var halfSize = new Vector3(Mathf.Abs(cellSize.x) * 0.5f, Mathf.Abs(cellSize.y) * 0.5f, Mathf.Abs(cellSize.z) * 0.5f);

            var min = new Vector3(
                Mathf.Min(minCenter.x - halfSize.x, maxCenter.x - halfSize.x),
                Mathf.Min(minCenter.y - halfSize.y, maxCenter.y - halfSize.y),
                Mathf.Min(minCenter.z - halfSize.z, maxCenter.z - halfSize.z));

            var max = new Vector3(
                Mathf.Max(minCenter.x + halfSize.x, maxCenter.x + halfSize.x),
                Mathf.Max(minCenter.y + halfSize.y, maxCenter.y + halfSize.y),
                Mathf.Max(minCenter.z + halfSize.z, maxCenter.z + halfSize.z));

            var center = (min + max) * 0.5f;
            var size = new Vector3(
                Mathf.Max(max.x - min.x, Mathf.Abs(cellSize.x)),
                Mathf.Max(max.y - min.y, Mathf.Abs(cellSize.y)),
                Mathf.Max(max.z - min.z, Mathf.Abs(cellSize.z)));

            return new Bounds(center, size);
        }
    }
}
