using System.Collections.Generic;
using UnityEngine;
using MelonLoader;

namespace AI_Mod.Runtime.Brain
{
    internal sealed class KitingPlanner
    {
        private const float MaxClusterDistance = 12f;
        private const float MaxClusterDistanceSquared = MaxClusterDistance * MaxClusterDistance;
        private const float MinimumAnchorSeparation = 0.2f;
        private const float MinimumKiteRadius = 3f;
        private const float SafeRadiusPadding = 0.75f;
        private const float RadiusBand = 0.9f;
        private const float LaneAngleWindowDegrees = 70f;
        private const float LanePenaltyBlockThreshold = 2.4f;
        private const float FastSwarmMultiplier = 1.15f;
        private const float SwarmOutrunScale = 1.5f;
        private const float WeightBias = 0.35f;
        private const float MinimumWeight = 0.05f;
        private const float PlayerSpeedFallback = 0.0094f; // TODO: replace with the live CharacterController speed once exposed.
        private const float SpeedEpsilon = 0.00025f;

        private readonly List<DynamicObstacle> _clusterMembers = new List<DynamicObstacle>(64);
        private readonly List<float> _weights = new List<float>(64);
        private bool _playerSpeedFallbackLogged;
        private bool _fastSwarmLogged;

        internal KitingDirective BuildDirective(AiWorldState world)
        {
            if (!world.Player.IsValid)
            {
                return KitingDirective.None;
            }

            var playerSpeed = world.Player.MoveSpeed;
            if (playerSpeed <= SpeedEpsilon)
            {
                playerSpeed = PlayerSpeedFallback;
                if (!_playerSpeedFallbackLogged)
                {
                    MelonLogger.Msg("Kiting planner using fallback player speed; TODO: expose live value.");
                    _playerSpeedFallbackLogged = true;
                }
            }
            else
            {
                _playerSpeedFallbackLogged = false;
            }

            CollectClusterMembers(world, playerSpeed, out var fastEnemyDetected);
            if (_clusterMembers.Count == 0)
            {
                _fastSwarmLogged = false;
                return KitingDirective.None;
            }

            if (fastEnemyDetected)
            {
                if (!_fastSwarmLogged)
                {
                    MelonLogger.Msg("Kiting skipped: enemy swarm is faster than the player; falling back to avoidance.");
                    _fastSwarmLogged = true;
                }

                return KitingDirective.None;
            }

            _fastSwarmLogged = false;

            var playerPosition = world.Player.Position;
            var anchor = ComputeAnchor(playerPosition);
            var anchorToPlayer = playerPosition - anchor;
            var currentRadius = anchorToPlayer.magnitude;

            var radialDirection = currentRadius > MinimumAnchorSeparation
                ? anchorToPlayer / currentRadius
                : DeriveRadialFromEnemies(playerPosition);

            if (radialDirection.sqrMagnitude < 0.0001f)
            {
                return KitingDirective.None;
            }

            var preferredRadius = ComputePreferredRadius(world, playerPosition, currentRadius);
            var radiusTolerance = Mathf.Max(world.Player.Radius + SafeRadiusPadding, RadiusBand);

            var tangents = ComputeTangents(radialDirection);
            var clockwisePenalty = EvaluateLanePenalty(anchor, tangents.clockwise, radialDirection, preferredRadius, radiusTolerance);
            var counterClockwisePenalty = EvaluateLanePenalty(anchor, tangents.counterClockwise, radialDirection, preferredRadius, radiusTolerance);

            var selectedDirection = tangents.counterClockwise;
            var alternateDirection = tangents.clockwise;
            var selectedPenalty = counterClockwisePenalty;

            if (clockwisePenalty < counterClockwisePenalty)
            {
                selectedDirection = tangents.clockwise;
                alternateDirection = tangents.counterClockwise;
                selectedPenalty = clockwisePenalty;
            }

            var fallbackRequested = clockwisePenalty >= LanePenaltyBlockThreshold && counterClockwisePenalty >= LanePenaltyBlockThreshold;

            var averageVelocity = ComputeAverageVelocity();
            var swarmSpeed = averageVelocity.magnitude;
            var swarmOutrunBias = 0f;

            if (playerSpeed > SpeedEpsilon && swarmSpeed > playerSpeed * FastSwarmMultiplier)
            {
                var excess = swarmSpeed - playerSpeed * FastSwarmMultiplier;
                swarmOutrunBias = Mathf.Clamp01(excess / Mathf.Max(playerSpeed, SpeedEpsilon)) * SwarmOutrunScale;
            }

            var clearanceScore = Mathf.Max(0f, LanePenaltyBlockThreshold - selectedPenalty);

            return KitingDirective.Create(
                anchor,
                preferredRadius,
                radiusTolerance,
                radialDirection,
                selectedDirection,
                alternateDirection,
                currentRadius,
                swarmSpeed,
                swarmOutrunBias,
                clearanceScore,
                fallbackRequested,
                _clusterMembers.Count);
        }

        private void CollectClusterMembers(AiWorldState world, float playerSpeed, out bool fastEnemyDetected)
        {
            _clusterMembers.Clear();
            _weights.Clear();
            fastEnemyDetected = false;

            var enemies = world.EnemyObstacles;
            if (enemies.Count == 0)
            {
                return;
            }

            var playerPosition = world.Player.Position;

            for (var i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                var offset = enemy.Position - playerPosition;
                var distanceSquared = offset.sqrMagnitude;
                if (distanceSquared > MaxClusterDistanceSquared)
                {
                    continue;
                }

                var distance = Mathf.Sqrt(distanceSquared);
                var weight = 1f / Mathf.Max(distance + WeightBias, 0.1f);
                _clusterMembers.Add(enemy);
                _weights.Add(Mathf.Max(weight, MinimumWeight));

                if (!fastEnemyDetected && enemy.Velocity.magnitude > playerSpeed + SpeedEpsilon)
                {
                    fastEnemyDetected = true;
                }
            }
        }

        private Vector2 ComputeAnchor(Vector2 playerPosition)
        {
            var weightedSum = Vector2.zero;
            var weightTotal = 0f;

            for (var i = 0; i < _clusterMembers.Count; i++)
            {
                var weight = _weights[i];
                weightedSum += _clusterMembers[i].Position * weight;
                weightTotal += weight;
            }

            if (weightTotal <= 0f)
            {
                return playerPosition;
            }

            return weightedSum / weightTotal;
        }

        private Vector2 DeriveRadialFromEnemies(Vector2 playerPosition)
        {
            var aggregate = Vector2.zero;

            for (var i = 0; i < _clusterMembers.Count; i++)
            {
                var vector = _clusterMembers[i].Position - playerPosition;
                if (vector.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                aggregate += vector.normalized;
            }

            if (aggregate.sqrMagnitude < 0.0001f)
            {
                return Vector2.up;
            }

            return (-aggregate).normalized;
        }

        private float ComputePreferredRadius(AiWorldState world, Vector2 playerPosition, float currentRadius)
        {
            var safeRadius = Mathf.Max(MinimumKiteRadius, world.Player.Radius * 3f);
            var nearestEnemy = float.MaxValue;

            for (var i = 0; i < _clusterMembers.Count; i++)
            {
                var enemy = _clusterMembers[i];
                var delta = enemy.Position - playerPosition;
                var distance = delta.magnitude - enemy.Radius;
                if (distance < nearestEnemy)
                {
                    nearestEnemy = distance;
                }
            }

            if (nearestEnemy < float.MaxValue)
            {
                safeRadius = Mathf.Max(safeRadius, nearestEnemy + world.Player.Radius + SafeRadiusPadding);
            }

            var maxRadius = safeRadius + 4f;
            if (currentRadius < safeRadius)
            {
                return safeRadius;
            }

            if (currentRadius > maxRadius)
            {
                return maxRadius;
            }

            return currentRadius;
        }

        private (Vector2 clockwise, Vector2 counterClockwise) ComputeTangents(Vector2 radialDirection)
        {
            var clockwise = new Vector2(radialDirection.y, -radialDirection.x);
            var counterClockwise = new Vector2(-radialDirection.y, radialDirection.x);
            return (clockwise.normalized, counterClockwise.normalized);
        }

        private float EvaluateLanePenalty(
            Vector2 anchor,
            Vector2 tangentDirection,
            Vector2 referenceRadial,
            float preferredRadius,
            float radiusTolerance)
        {
            var penalty = 0f;
            var orientation = Mathf.Sign(Vector2.Dot(tangentDirection, new Vector2(-referenceRadial.y, referenceRadial.x)));
            if (orientation == 0f)
            {
                orientation = 1f;
            }

            for (var i = 0; i < _clusterMembers.Count; i++)
            {
                var enemy = _clusterMembers[i];
                var anchorToEnemy = enemy.Position - anchor;
                var enemyDistance = anchorToEnemy.magnitude;
                if (enemyDistance < 0.0001f)
                {
                    continue;
                }

                var angle = Vector2.SignedAngle(referenceRadial, anchorToEnemy);
                if (orientation > 0f && angle <= 0f)
                {
                    continue;
                }

                if (orientation < 0f && angle >= 0f)
                {
                    continue;
                }

                var absAngle = Mathf.Abs(angle);
                if (absAngle > LaneAngleWindowDegrees)
                {
                    continue;
                }

                var radialDelta = Mathf.Abs(enemyDistance - preferredRadius);
                if (radialDelta > radiusTolerance * 1.5f)
                {
                    continue;
                }

                var angleFactor = 1f - Mathf.Clamp01(absAngle / LaneAngleWindowDegrees);
                var distanceFactor = 1f / (1f + Mathf.Abs(enemyDistance - preferredRadius));
                penalty += angleFactor * distanceFactor * Mathf.Max(0.25f, 1f + enemy.Radius);
            }

            return penalty;
        }

        private Vector2 ComputeAverageVelocity()
        {
            if (_clusterMembers.Count == 0)
            {
                return Vector2.zero;
            }

            var sum = Vector2.zero;
            for (var i = 0; i < _clusterMembers.Count; i++)
            {
                sum += _clusterMembers[i].Velocity;
            }

            return sum / _clusterMembers.Count;
        }
    }
}
