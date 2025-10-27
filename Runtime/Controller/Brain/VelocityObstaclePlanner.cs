using System;
using System.Collections.Generic;
using AI_Mod.Runtime.Geometry;
using UnityEngine;

namespace AI_Mod.Runtime.Brain
{
    internal sealed class VelocityObstaclePlanner
    {
        private const int DirectionSamples = 20;
        private const float SimulationStep = 0.2f;
        private const int SimulationSteps = 8;
        private const float SimulationDuration = SimulationSteps * SimulationStep;
        internal const float MinimumSeparation = 0.66f;
        private const float WallPenaltyWeight = 100f;
        private const float GemRewardWeight = 12f;
        private const float OverlapPenaltyScale = GemRewardWeight;
        private const float BreakoutRewardScale = GemRewardWeight;
        private const float GemAttractionDistance = 8f;
        private const float GemAttractionDistanceSquared = GemAttractionDistance * GemAttractionDistance;
        private const float KitingAlignmentWeight = 12f;
        private const float KitingRadiusWeight = 8f;
        private const float KitingOutrunWeight = 6f;
        private const float KitingAlignmentThreshold = 0.2f;
        private const float PlanWallCullRadius = 5.35f;

        private readonly PlannerDebugInfo _debugInfo = new PlannerDebugInfo();
        private readonly List<Vector2> _trajectoryScratch = new List<Vector2>(SimulationSteps + 1);
        private readonly Vector2[][] _enemyProjectedPositions = CreateProjectionBuffer();
        private readonly Vector2[][] _bulletProjectedPositions = CreateProjectionBuffer();
        private float[] _enemyCombinedRadii = Array.Empty<float>();
        private float[] _enemyCombinedRadiiSquared = Array.Empty<float>();
        private float[] _bulletCombinedRadii = Array.Empty<float>();
        private float[] _bulletCombinedRadiiSquared = Array.Empty<float>();
        private int _enemyCount;
        private int _bulletCount;
        private float _maxGemRewardPerStep;
        private readonly List<WallTilemap> _planWallTilemaps = new List<WallTilemap>(8);

        internal PlannerDebugInfo DebugInfo => _debugInfo;

        internal PlannerResult Plan(AiWorldState world, KitingDirective directive, EncirclementSnapshot encirclement)
        {
            _debugInfo.Begin();

            if (!world.Player.IsValid)
            {
                return PlannerResult.Zero;
            }

            var speed = Mathf.Max(0.01f, world.Player.MoveSpeed);
            var origin = world.Player.Position;
            var playerRadius = world.Player.Radius;
            var playerSafeRadius = playerRadius + MinimumSeparation;
            var gemScale = Mathf.Clamp01(1f - encirclement.Intensity);
            PrepareDynamicObstacleCache(
                world.EnemyObstacles,
                _enemyProjectedPositions,
                ref _enemyCombinedRadii,
                ref _enemyCombinedRadiiSquared,
                playerSafeRadius,
                out _enemyCount);
            PrepareDynamicObstacleCache(
                world.BulletObstacles,
                _bulletProjectedPositions,
                ref _bulletCombinedRadii,
                ref _bulletCombinedRadiiSquared,
                playerSafeRadius,
                out _bulletCount);
            _maxGemRewardPerStep = ComputeMaxGemRewardPerStep(world.Gems, gemScale);
            var planCullRadius = Mathf.Max(PlanWallCullRadius, playerSafeRadius);
            CollectRelevantWallTilemaps(world.WallTilemaps, origin, planCullRadius, _planWallTilemaps);
            var bestScore = float.MinValue;
            var bestDirection = Vector2.zero;
            var bestAlignment = float.NegativeInfinity;
            var breakoutPreferred = false;
            var bestBreakoutExitTime = float.PositiveInfinity;

            foreach (var candidate in EnumerateDirections())
            {
                if (directive.HasDirective && candidate.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                var velocity = candidate * speed;
                _trajectoryScratch.Clear();
                float enemyOverlapSeconds;
                float bulletOverlapSeconds;
                float breakoutExitTime;
                var score = EvaluateCandidate(
                    origin,
                    velocity,
                    playerRadius,
                    _planWallTilemaps,
                    world.Gems,
                    encirclement,
                    gemScale,
                    _trajectoryScratch,
                    bestScore,
                    out enemyOverlapSeconds,
                    out bulletOverlapSeconds,
                    out breakoutExitTime);
                var direction = velocity.sqrMagnitude > 0.0001f ? velocity.normalized : Vector2.zero;
                score += ComputeKitingBonus(direction, directive);
                var breakoutBonus = ComputeBreakoutBonus(direction, encirclement, breakoutExitTime);
                score += breakoutBonus;

                _debugInfo.RecordCandidate(direction, score, enemyOverlapSeconds, bulletOverlapSeconds);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDirection = direction;
                    bestBreakoutExitTime = breakoutExitTime;
                    breakoutPreferred = breakoutBonus > 0f && encirclement.Intensity > 0f;
                    if (directive.HasDirective)
                    {
                        bestAlignment = ComputeTangentialAlignment(direction, directive);
                    }
                    _debugInfo.RecordBest(
                        bestDirection,
                        bestScore,
                        _trajectoryScratch,
                        enemyOverlapSeconds,
                        bulletOverlapSeconds,
                        breakoutExitTime,
                        breakoutPreferred,
                        encirclement.Intensity);
                }
            }

            if (bestDirection == Vector2.zero)
            {
                return PlannerResult.Zero;
            }

            SteeringMode mode;
            if (encirclement.Intensity >= 0.35f && breakoutPreferred)
            {
                mode = SteeringMode.Breakout;
            }
            else if (!directive.HasDirective)
            {
                mode = SteeringMode.VelocityObstacle;
            }
            else
            {
                if (!float.IsFinite(bestAlignment))
                {
                    bestAlignment = -1f;
                }

                mode = bestAlignment >= KitingAlignmentThreshold
                    ? SteeringMode.Kiting
                    : SteeringMode.Fallback;
            }

            return new PlannerResult(bestDirection, mode);
        }

        private static IEnumerable<Vector2> EnumerateDirections()
        {
            yield return Vector2.zero;
            for (var i = 0; i < DirectionSamples; i++)
            {
                var angle = (Mathf.PI * 2f * i) / DirectionSamples;
                yield return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }
        }

        private float ComputeKitingBonus(Vector2 direction, KitingDirective directive)
        {
            if (!directive.HasDirective || direction.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }

            direction = direction.normalized;
            var desiredTangent = directive.ArcDirectionSign >= 0 ? directive.OrbitDirection : directive.AlternateOrbitDirection;
            var alternateTangent = directive.ArcDirectionSign >= 0 ? directive.AlternateOrbitDirection : directive.OrbitDirection;

            var desiredAlignment = Mathf.Max(0f, Vector2.Dot(direction, desiredTangent));
            var alternateAlignment = Mathf.Max(0f, Vector2.Dot(direction, alternateTangent));

            var boundaryBuffer = Mathf.Max(5f, directive.ArcHalfAngleDegrees * 0.25f);
            var distanceFromEdge = directive.ArcHalfAngleDegrees - Mathf.Abs(directive.ArcAngleDegrees);
            var boundaryFactor = directive.ArcHalfAngleDegrees > 0.1f
                ? Mathf.Clamp01(distanceFromEdge / boundaryBuffer)
                : 1f;

            var tangentialScore = desiredAlignment * KitingAlignmentWeight * Mathf.Max(0.25f, boundaryFactor);
            if (distanceFromEdge <= boundaryBuffer * 0.6f && alternateAlignment > 0f)
            {
                var reverseFactor = 1f - Mathf.Clamp01(distanceFromEdge / (boundaryBuffer * 0.6f));
                tangentialScore += alternateAlignment * KitingAlignmentWeight * reverseFactor * 0.6f;
            }

            var radialComponent = Vector2.Dot(direction, directive.RadialDirection);
            var radiusDelta = directive.CurrentRadius - directive.PreferredRadius;
            var radialScore = 0f;

            if (radiusDelta > directive.RadiusTolerance)
            {
                radialScore -= radialComponent * KitingRadiusWeight;
            }
            else if (radiusDelta < -directive.RadiusTolerance)
            {
                radialScore += radialComponent * KitingRadiusWeight;
            }

            var outrunScore = directive.SwarmOutrunBias > 0f
                ? directive.SwarmOutrunBias * radialComponent * KitingOutrunWeight
                : 0f;

            var total = tangentialScore + radialScore + outrunScore;
            if (!float.IsFinite(total))
            {
                return 0f;
            }

            return total;
        }

        private static float ComputeTangentialAlignment(Vector2 direction, KitingDirective directive)
        {
            if (!directive.HasDirective || direction.sqrMagnitude < 0.0001f)
            {
                return -1f;
            }

            direction = direction.normalized;
            var desired = directive.ArcDirectionSign >= 0 ? directive.OrbitDirection : directive.AlternateOrbitDirection;
            var alignment = Vector2.Dot(direction, desired);
            if (alignment <= 0f)
            {
                return alignment;
            }

            if (directive.ArcHalfAngleDegrees <= 0.1f)
            {
                return alignment;
            }

            var distanceFromEdge = directive.ArcHalfAngleDegrees - Mathf.Abs(directive.ArcAngleDegrees);
            var boundaryBuffer = Mathf.Max(5f, directive.ArcHalfAngleDegrees * 0.25f);
            var boundaryFactor = Mathf.Clamp01(distanceFromEdge / boundaryBuffer);
            return alignment * Mathf.Max(0.2f, boundaryFactor);
        }

        private float EvaluateCandidate(
            Vector2 origin,
            Vector2 velocity,
            float playerRadius,
            IReadOnlyList<WallTilemap> wallTilemaps,
            IReadOnlyList<GemSnapshot> gems,
            EncirclementSnapshot encirclement,
            float gemScale,
            List<Vector2>? pathRecorder,
            float bestScore,
            out float enemyOverlapSeconds,
            out float bulletOverlapSeconds,
            out float breakoutExitTime)
        {
            var position = origin;
            var score = 0f;
            var dt = SimulationStep;
            enemyOverlapSeconds = 0f;
            bulletOverlapSeconds = 0f;
            breakoutExitTime = float.PositiveInfinity;

            pathRecorder?.Add(position);

            for (var step = 0; step < SimulationSteps; step++)
            {
                position += velocity * dt;
                pathRecorder?.Add(position);

                if (_enemyCount > 0)
                {
                    var overlap = EvaluateDynamicObstacles(
                        position,
                        _enemyProjectedPositions[step],
                        _enemyCombinedRadii,
                        _enemyCombinedRadiiSquared,
                        _enemyCount);
                    if (overlap > 0f)
                    {
                        enemyOverlapSeconds += overlap * dt;
                        score -= overlap * dt * OverlapPenaltyScale;
                    }
                }

                if (_bulletCount > 0)
                {
                    var overlap = EvaluateDynamicObstacles(
                        position,
                        _bulletProjectedPositions[step],
                        _bulletCombinedRadii,
                        _bulletCombinedRadiiSquared,
                        _bulletCount);
                    if (overlap > 0f)
                    {
                        bulletOverlapSeconds += overlap * dt;
                        score -= overlap * dt * OverlapPenaltyScale;
                    }
                }

                var wallPenalty = EvaluateWallPenalty(position, wallTilemaps, playerRadius);
                if (float.IsPositiveInfinity(wallPenalty) || float.IsNaN(wallPenalty))
                {
                    return float.NegativeInfinity;
                }

                score -= WallPenaltyWeight * wallPenalty;
                if (!float.IsFinite(score))
                {
                    return float.NegativeInfinity;
                }
                var gemReward = EvaluateGemReward(position, gems);
                if (gemReward > 0f && gemScale > 0f)
                {
                    score += GemRewardWeight * gemReward * gemScale;
                }

                if (encirclement.HasRing && float.IsPositiveInfinity(breakoutExitTime))
                {
                    var radialDistance = (position - origin).magnitude;
                    if (radialDistance >= encirclement.ExitRadius)
                    {
                        breakoutExitTime = (step + 1) * dt;
                    }
                }

                var remainingSteps = SimulationSteps - step - 1;
                if (remainingSteps <= 0)
                {
                    continue;
                }

                var optimisticScore = score + remainingSteps * _maxGemRewardPerStep;
                if (optimisticScore <= bestScore)
                {
                    break;
                }
            }

            return score;
        }

        private static void CollectRelevantWallTilemaps(
            IReadOnlyList<WallTilemap> source,
            Vector2 center,
            float radius,
            List<WallTilemap> destination)
        {
            destination.Clear();
            if (source.Count == 0)
            {
                return;
            }

            for (var i = 0; i < source.Count; i++)
            {
                var entry = source[i];
                var tilemap = entry.Tilemap;
                if (tilemap == null || tilemap.Equals(null))
                {
                    continue;
                }

                if (radius <= 0f)
                {
                    var boundsCenter = entry.WorldBounds.center;
                    if (entry.WorldBounds.Contains(new Vector3(center.x, center.y, boundsCenter.z)))
                    {
                        destination.Add(entry);
                    }
                    continue;
                }

                if (WallGeometry.CircleIntersectsBounds(center, radius, entry.WorldBounds))
                {
                    destination.Add(entry);
                }
            }
        }

        private float ComputeBreakoutBonus(Vector2 direction, EncirclementSnapshot encirclement, float breakoutExitTime)
        {
            if (!encirclement.HasRing || encirclement.Intensity <= 0f || direction.sqrMagnitude < 0.0001f || !encirclement.HasBreakoutDirection)
            {
                return 0f;
            }

            var alignment = Mathf.Max(0f, Vector2.Dot(direction, encirclement.BreakoutDirection));
            if (alignment <= 0f)
            {
                return 0f;
            }

            var exitProgress = float.IsPositiveInfinity(breakoutExitTime)
                ? 0f
                : Mathf.Clamp01(1f - breakoutExitTime / SimulationDuration);

            var gapEase = 1f - encirclement.GapOccupancy;
            var weight = encirclement.Intensity * BreakoutRewardScale;
            var blended = Mathf.Max(exitProgress, gapEase * 0.5f);
            var bonus = weight * alignment * blended;
            if (!float.IsFinite(bonus))
            {
                return 0f;
            }

            return bonus;
        }

        private static float EvaluateDynamicObstacles(
            Vector2 position,
            Vector2[] projectedPositions,
            float[] combinedRadii,
            float[] combinedRadiiSquared,
            int obstacleCount)
        {
            var penalty = 0f;

            for (var i = 0; i < obstacleCount; i++)
            {
                var projected = projectedPositions[i];
                var dx = projected.x - position.x;
                var dy = projected.y - position.y;
                var distanceSquared = dx * dx + dy * dy;
                var combinedSquared = combinedRadiiSquared[i];

                if (distanceSquared < combinedSquared)
                {
                    var combined = combinedRadii[i];
                    if (combined <= 0f)
                    {
                        continue;
                    }

                    var distance = distanceSquared > 0f ? Mathf.Sqrt(distanceSquared) : 0f;
                    penalty += (combined - distance) / combined;
                }
            }

            return penalty;
        }

        private float EvaluateWallPenalty(Vector2 position, IReadOnlyList<WallTilemap> wallTilemaps, float radius)
        {
            if (wallTilemaps.Count == 0)
            {
                return 0f;
            }

            var safeRadius = radius + MinimumSeparation;
            if (safeRadius <= 0f)
            {
                return 0f;
            }

            var radiusSquared = Mathf.Max(radius * radius, 0f);
            var safeRadiusSquared = safeRadius * safeRadius;
            var penalty = 0f;

            for (var i = 0; i < wallTilemaps.Count; i++)
            {
                var entry = wallTilemaps[i];
                var tilemap = entry.Tilemap;
                if (tilemap == null || tilemap.Equals(null))
                {
                    continue;
                }

                var boundingBoxes = entry.BoundingBoxes;
                if (boundingBoxes == null || boundingBoxes.Length == 0)
                {
                    continue;
                }

                for (var j = 0; j < boundingBoxes.Length; j++)
                {
                    var rect = boundingBoxes[j];
                    var distanceSquared = WallGeometry.DistanceSquaredToRect(rect, position);

                    if (distanceSquared <= radiusSquared)
                    {
                        return float.PositiveInfinity;
                    }

                    if (distanceSquared >= safeRadiusSquared)
                    {
                        continue;
                    }

                    var clampedDistanceSquared = Mathf.Max(distanceSquared, 0f);
                    var distance = clampedDistanceSquared > 0f ? Mathf.Sqrt(clampedDistanceSquared) : 0f;
                    var separation = safeRadius - distance;
                    if (separation <= 0f)
                    {
                        continue;
                    }

                    var normalized = separation / safeRadius;
                    var amplification = safeRadius / Mathf.Max(distance - radius, 0.001f);
                    penalty += normalized * amplification;
                }
            }

            return penalty;
        }

        private float EvaluateGemReward(Vector2 position, IReadOnlyList<GemSnapshot> gems)
        {
            var best = 0f;
            for (var i = 0; i < gems.Count; i++)
            {
                var gem = gems[i];
                if (!gem.IsCollectible)
                {
                    continue;
                }

                var dx = gem.Position.x - position.x;
                var dy = gem.Position.y - position.y;
                var distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > GemAttractionDistanceSquared)
                {
                    continue;
                }

                var distance = distanceSquared > 0f ? Mathf.Sqrt(distanceSquared) : 0f;
                var normalized = (GemAttractionDistance - distance) / GemAttractionDistance;
                if (normalized > best)
                {
                    best = normalized;
                    if (best >= 1f)
                    {
                        break;
                    }
                }
            }

            return best;
        }

        private static Vector2[][] CreateProjectionBuffer()
        {
            var buffer = new Vector2[SimulationSteps][];
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = Array.Empty<Vector2>();
            }

            return buffer;
        }

        private static void EnsureProjectionCapacity(Vector2[][] buffer, int count)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].Length < count)
                {
                    buffer[i] = new Vector2[count];
                }
            }
        }

        private static void PrepareDynamicObstacleCache(
            IReadOnlyList<DynamicObstacle> obstacles,
            Vector2[][] projections,
            ref float[] combinedRadii,
            ref float[] combinedRadiiSquared,
            float playerSafeRadius,
            out int count)
        {
            count = obstacles.Count;
            if (count == 0)
            {
                return;
            }

            if (combinedRadii.Length < count)
            {
                combinedRadii = new float[count];
            }

            if (combinedRadiiSquared.Length < count)
            {
                combinedRadiiSquared = new float[count];
            }

            EnsureProjectionCapacity(projections, count);

            for (var i = 0; i < count; i++)
            {
                var obstacle = obstacles[i];
                var combined = obstacle.Radius + playerSafeRadius;
                combinedRadii[i] = combined;
                combinedRadiiSquared[i] = combined * combined;
            }

            for (var step = 0; step < SimulationSteps; step++)
            {
                var timeAhead = (step + 1) * SimulationStep;
                var projection = projections[step];
                for (var i = 0; i < count; i++)
                {
                    var obstacle = obstacles[i];
                    projection[i] = obstacle.Position + obstacle.Velocity * timeAhead;
                }
            }
        }

        private static float ComputeMaxGemRewardPerStep(IReadOnlyList<GemSnapshot> gems, float gemScale)
        {
            if (gemScale <= 0f)
            {
                return 0f;
            }

            for (var i = 0; i < gems.Count; i++)
            {
                if (gems[i].IsCollectible)
                {
                    return GemRewardWeight * gemScale;
                }
            }

            return 0f;
        }
    }
}
