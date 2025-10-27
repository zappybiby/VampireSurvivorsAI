using AI_Mod.Runtime.Brain;
using AI_Mod.Runtime.Geometry;
using MelonLoader;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AI_Mod.Runtime
{
    internal sealed partial class AiController
    {
        private void MaybeLogDebugInfo()
        {
            if (Time.unscaledTime - _lastDebugLogTime < DebugLogIntervalSeconds)
            {
                return;
            }

            _lastDebugLogTime = Time.unscaledTime;

            var plannerDebug = _planner.DebugInfo;
            MelonLogger.Msg($"AI Debug Mode: {_lastPlan.Mode}");

            if (plannerDebug.HasBest)
            {
                MelonLogger.Msg($"AI Debug Planner Score: {plannerDebug.BestScore:F2}");
                MelonLogger.Msg($"AI Debug Planned Steps: {FormatTrajectorySteps(plannerDebug.BestTrajectory)}");
            }
            else
            {
                MelonLogger.Msg("AI Debug Planner Score: fallback active (no planner result ready).");
                MelonLogger.Msg("AI Debug Planned Steps: fallback active (no planner trajectory recorded).");
            }

            var playerSnapshot = _world.Player;
            if (playerSnapshot.IsValid)
            {
                var position = playerSnapshot.Position;
                MelonLogger.Msg($"AI Debug Player Position: ({position.x:F2}, {position.y:F2})");

                CollectWallDistances(position, playerSnapshot.Radius, playerWalls: false, _wallDistanceBuffer);
                MelonLogger.Msg($"AI Debug Nearest Walls: {FormatWallDistanceList(_wallDistanceBuffer)}");

                CollectWallDistances(position, playerSnapshot.Radius, playerWalls: true, _playerWallDistanceBuffer);
                MelonLogger.Msg($"AI Debug Nearest PlayerWalls: {FormatWallDistanceList(_playerWallDistanceBuffer)}");
            }
            else
            {
                MelonLogger.Msg("AI Debug Player Position: fallback active (player snapshot invalid).");
                MelonLogger.Msg("AI Debug Nearest Walls: fallback active (player snapshot unavailable).");
                MelonLogger.Msg("AI Debug Nearest PlayerWalls: fallback active (player snapshot unavailable).");
            }
        }

        private void CollectWallDistances(Vector2 playerPosition, float playerRadius, bool playerWalls, List<WallDistanceInfo> destination)
        {
            destination.Clear();

            var tilemaps = _world.WallTilemaps;
            if (tilemaps.Count == 0)
            {
                return;
            }

            var safeRadius = Mathf.Max(playerRadius + VelocityObstaclePlanner.MinimumSeparation, 0f);
            var radiusSquared = Mathf.Max(playerRadius * playerRadius, 0f);

            for (var i = 0; i < tilemaps.Count; i++)
            {
                var entry = tilemaps[i];
                var tilemap = entry.Tilemap;
                if (tilemap == null || tilemap.Equals(null))
                {
                    continue;
                }

                var go = tilemap.gameObject;
                var rawName = go != null && !go.Equals(null) ? go.name ?? "<unnamed>" : "<missing>";
                var isPlayerWall = rawName.IndexOf("PlayerWall", System.StringComparison.OrdinalIgnoreCase) >= 0;

                if (playerWalls)
                {
                    if (!isPlayerWall)
                    {
                        continue;
                    }
                }
                else
                {
                    if (isPlayerWall)
                    {
                        continue;
                    }

                    if (rawName.IndexOf("Walls", System.StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                }

                var boundingBoxes = entry.BoundingBoxes;
                if (boundingBoxes == null || boundingBoxes.Length == 0)
                {
                    continue;
                }

                if (safeRadius > 0f && !WallGeometry.CircleIntersectsBounds(playerPosition, safeRadius, entry.WorldBounds))
                {
                    continue;
                }

                var bestDistanceSquared = float.PositiveInfinity;
                var bestPoint = Vector2.zero;
                for (var j = 0; j < boundingBoxes.Length; j++)
                {
                    var rect = boundingBoxes[j];
                    var distanceSquared = WallGeometry.DistanceSquaredToRect(rect, playerPosition);

                    if (distanceSquared <= radiusSquared)
                    {
                        bestPoint = WallGeometry.ClosestPointOnRect(rect, playerPosition);
                        bestDistanceSquared = 0f;
                        break;
                    }

                    if (distanceSquared < bestDistanceSquared)
                    {
                        bestPoint = WallGeometry.ClosestPointOnRect(rect, playerPosition);
                        bestDistanceSquared = distanceSquared;
                    }
                }

                if (!float.IsFinite(bestDistanceSquared))
                {
                    continue;
                }

                var bestDistance = bestDistanceSquared > 0f ? Mathf.Sqrt(bestDistanceSquared) : 0f;
                destination.Add(new WallDistanceInfo(rawName, bestDistance, bestPoint));
            }

            if (destination.Count <= 1)
            {
                return;
            }

            destination.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        }

        private static string FormatWallDistanceList(List<WallDistanceInfo> entries)
        {
            if (entries.Count == 0)
            {
                return "fallback active (no matching wall tilemaps)";
            }

            var builder = new StringBuilder(entries.Count * 48);
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(" | ");
                }

                var entry = entries[i];
                builder.Append(entry.Name)
                    .Append(':')
                    .Append(' ')
                    .Append(entry.Distance.ToString("F2"))
                    .Append(" @ (")
                    .Append(entry.ClosestPoint.x.ToString("F2"))
                    .Append(", ")
                    .Append(entry.ClosestPoint.y.ToString("F2"))
                    .Append(')');
            }

            return builder.ToString();
        }

        private static string FormatTrajectorySteps(IReadOnlyList<Vector2> trajectory)
        {
            if (trajectory == null)
            {
                return "fallback active: trajectory list unavailable";
            }

            var availableSteps = trajectory.Count - 1;
            if (availableSteps <= 0)
            {
                return "fallback active: trajectory contains no steps";
            }

            var stepsToReport = Mathf.Min(2, availableSteps);
            var builder = new StringBuilder(stepsToReport * 48);

            for (var i = 0; i < stepsToReport; i++)
            {
                if (i > 0)
                {
                    builder.Append(" | ");
                }

                var point = trajectory[i + 1];
                builder.Append("Step ")
                    .Append(i + 1)
                    .Append(':')
                    .Append(' ')
                    .Append('(')
                    .Append(point.x.ToString("F2"))
                    .Append(", ")
                    .Append(point.y.ToString("F2"))
                    .Append(')');
            }

            if (availableSteps < 2)
            {
                builder.Append(" | fallback active: trajectory shorter than two steps");
            }

            return builder.ToString();
        }

        private readonly struct WallDistanceInfo
        {
            internal WallDistanceInfo(string name, float distance, Vector2 closestPoint)
            {
                Name = name;
                Distance = distance;
                ClosestPoint = closestPoint;
            }

            internal string Name { get; }
            internal float Distance { get; }
            internal Vector2 ClosestPoint { get; }
        }
    }
}
