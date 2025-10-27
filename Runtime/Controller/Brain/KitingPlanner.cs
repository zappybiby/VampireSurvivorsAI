using System.Collections.Generic;
using UnityEngine;
using MelonLoader;

namespace AI_Mod.Runtime.Brain
{
    internal sealed class KitingPlanner
    {
        private const float MaxClusterDistance = 6.1f;
        private const float MaxClusterDistanceSquared = MaxClusterDistance * MaxClusterDistance;
        private const float MinimumAnchorSeparation = 0.2f;
        private const float MinimumKiteRadius = 0.32f;
        private const float SafeRadiusPadding = 0.18f;
        private const float RadiusBand = 0.28f;
        private const float LaneAngleWindowDegrees = 70f;
        private const float LanePenaltyBlockThreshold = 2.4f;
        private const float FastSwarmMultiplier = 1.15f;
        private const float SwarmOutrunScale = 1.5f;
        private const float WeightBias = 0.35f;
        private const float MinimumWeight = 0.05f;
        private const float PlayerSpeedFallback = 0.0094f; // TODO: replace with the live CharacterController speed once exposed.
        private const float SpeedEpsilon = 0.00025f;

        private const float AnchorSmoothingFactor = 0.25f; // TODO: tune smoothing factors after telemetry review.
        private const float AnchorResetDistanceSquared = 20f;
        private const float RadiusSmoothingFactor = 0.2f; // TODO: tune smoothing factors after telemetry review.
        private const float RadiusAdjustScale = 0.35f;
        private const float RadiusResetThreshold = 5.5f;
        private const float RadiusSpreadClampMin = 0.12f;
        private const float RadiusSpreadClampMax = 0.9f;
        private const float StragglerAlignmentThreshold = 0.1f;
        private const float StragglerRadiusSlackMultiplier = 1.35f;
        private const float StragglerAdjustClamp = 0.85f;
        private const float DesiredEnemyGapMultiplier = 3f;
        private const float DesiredEnemyGapSlack = 0.12f;
        private const float PreferredRadiusMaxOffset = 0.95f;
        private const int StragglerClusterBaseline = 10;
        private const float StragglerClusterDamp = 0.045f;
        private const float GemAlignmentThreshold = 0.2f;
        private const float GemDistanceSlack = 1.2f;
        private const float MinimumGemConsiderDistance = 0.35f;
        private const float LaneSwitchCooldownSeconds = 1.25f;
        private const float LanePenaltyWeight = 1f;
        private const float StragglerBiasWeight = 0.85f;
        private const float GemBiasWeight = 0.65f;
        private const float EscapeBiasWeight = 0.75f;
        private const float ClearanceScoreScale = 1.1f;
        private const float ArcHalfAngleDegreesBase = 85f;
        private const float ArcHalfAngleDegreesMin = 55f;
        private const float ArcHalfAngleDegreesMax = 120f;
        private const float ArcHalfAngleTightenPerSpread = 35f;
        private const float ArcHalfAngleTightenPerEncirclement = 25f;
        private const float ArcCenterUpdateThresholdDegrees = 10f;
        private const float ArcCenterSmoothingFactor = 0.15f;
        private const float ArcSwitchHysteresisDegrees = 6f;
        private const float ArcSwitchCooldownSeconds = 0.4f;

        private readonly List<DynamicObstacle> _clusterMembers = new List<DynamicObstacle>(64);
        private readonly List<float> _weights = new List<float>(64);
        private bool _playerSpeedFallbackLogged;
        private bool _fastSwarmLogged;
        private Vector2 _smoothedAnchor;
        private bool _hasSmoothedAnchor;
        private float _smoothedRadius;
        private bool _hasSmoothedRadius;
        private int _activeLaneOrientation;
        private float _lastLaneSwitchTime = float.NegativeInfinity;
        private Vector2 _arcCenterDirection;
        private bool _hasArcCenter;

        internal KitingDirective BuildDirective(AiWorldState world)
        {
            if (!world.Player.IsValid)
            {
                ResetClusterState();
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
                ResetClusterState();
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
            var rawAnchor = ComputeAnchor(playerPosition);
            var anchor = SmoothAnchor(rawAnchor, playerPosition);
            var anchorToPlayer = playerPosition - anchor;
            var currentRadius = anchorToPlayer.magnitude;

            var radialDirection = currentRadius > MinimumAnchorSeparation
                ? anchorToPlayer / Mathf.Max(currentRadius, 0.0001f)
                : DeriveRadialFromEnemies(playerPosition);

            if (radialDirection.sqrMagnitude < 0.0001f)
            {
                return KitingDirective.None;
            }

            var basePreferredRadius = ComputePreferredRadius(world, playerPosition, currentRadius, out var safeRadius, out var maxRadius);
            var baseRadiusSpread = ComputeRadiusSpread(anchor, basePreferredRadius);
            var radiusTolerance = Mathf.Max(world.Player.Radius + SafeRadiusPadding, RadiusBand, baseRadiusSpread);

            var tangents = ComputeTangents(radialDirection);
            var clockwiseEvaluation = EvaluateLane(-1, anchor, tangents.clockwise, radialDirection, basePreferredRadius, radiusTolerance, world);
            var counterClockwiseEvaluation = EvaluateLane(1, anchor, tangents.counterClockwise, radialDirection, basePreferredRadius, radiusTolerance, world);

            var time = Time.unscaledTime;
            if (_activeLaneOrientation == 0)
            {
                _activeLaneOrientation = counterClockwiseEvaluation.Score >= clockwiseEvaluation.Score ? 1 : -1;
                _lastLaneSwitchTime = time;
            }
            else
            {
                var currentLane = _activeLaneOrientation > 0 ? counterClockwiseEvaluation : clockwiseEvaluation;
                var oppositeLane = _activeLaneOrientation > 0 ? clockwiseEvaluation : counterClockwiseEvaluation;
                if (currentLane.Penalty >= LanePenaltyBlockThreshold * 1.2f &&
                    oppositeLane.Penalty + 0.35f < currentLane.Penalty &&
                    time - _lastLaneSwitchTime >= LaneSwitchCooldownSeconds)
                {
                    _activeLaneOrientation = -_activeLaneOrientation;
                    _lastLaneSwitchTime = time;
                }
            }

            var selected = _activeLaneOrientation >= 0 ? counterClockwiseEvaluation : clockwiseEvaluation;
            var alternate = _activeLaneOrientation >= 0 ? clockwiseEvaluation : counterClockwiseEvaluation;

            var adjustedPreferred = Mathf.Clamp(basePreferredRadius + selected.RadialAdjustment * RadiusAdjustScale, safeRadius, maxRadius);
            var preferredRadius = SmoothPreferredRadius(adjustedPreferred, safeRadius, maxRadius);
            var finalRadiusSpread = ComputeRadiusSpread(anchor, preferredRadius);
            radiusTolerance = Mathf.Max(radiusTolerance, finalRadiusSpread);

            var arcHalfAngle = ResolveArcHalfAngle(finalRadiusSpread, safeRadius, world.Encirclement);
            var arcAngle = UpdateArcCenter(radialDirection);

            if (_activeLaneOrientation > 0 &&
                arcAngle >= arcHalfAngle - ArcSwitchHysteresisDegrees &&
                time - _lastLaneSwitchTime >= ArcSwitchCooldownSeconds)
            {
                _activeLaneOrientation = -1;
                _lastLaneSwitchTime = time;
                selected = clockwiseEvaluation;
                alternate = counterClockwiseEvaluation;
            }
            else if (_activeLaneOrientation < 0 &&
                arcAngle <= -arcHalfAngle + ArcSwitchHysteresisDegrees &&
                time - _lastLaneSwitchTime >= ArcSwitchCooldownSeconds)
            {
                _activeLaneOrientation = 1;
                _lastLaneSwitchTime = time;
                selected = counterClockwiseEvaluation;
                alternate = clockwiseEvaluation;
            }

            var fallbackRequested = ShouldRequestFallback(selected, alternate, world.Encirclement);
            var averageVelocity = ComputeAverageVelocity();
            var swarmSpeed = averageVelocity.magnitude;
            var swarmOutrunBias = 0f;

            if (playerSpeed > SpeedEpsilon && swarmSpeed > playerSpeed * FastSwarmMultiplier)
            {
                var excess = swarmSpeed - playerSpeed * FastSwarmMultiplier;
                swarmOutrunBias = Mathf.Clamp01(excess / Mathf.Max(playerSpeed, SpeedEpsilon)) * SwarmOutrunScale;
            }

            var clearanceScore = Mathf.Max(0f, LanePenaltyBlockThreshold - selected.Penalty) * ClearanceScoreScale;
            clearanceScore += Mathf.Max(0f, selected.Score);

            return KitingDirective.Create(
                anchor,
                preferredRadius,
                radiusTolerance,
                finalRadiusSpread,
                radialDirection,
                selected.Direction,
                alternate.Direction,
                currentRadius,
                swarmSpeed,
                swarmOutrunBias,
                clearanceScore,
                fallbackRequested,
                _clusterMembers.Count,
                selected.Penalty,
                selected.Score,
                selected.StragglerScore,
                selected.GemScore,
                selected.EscapeScore,
                selected.Orientation,
                selected.RadialAdjustment,
                alternate.Penalty,
                alternate.Score,
                alternate.Orientation,
                arcHalfAngle,
                arcAngle,
                _arcCenterDirection,
                _activeLaneOrientation);
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

        private Vector2 SmoothAnchor(Vector2 rawAnchor, Vector2 playerPosition)
        {
            if (!_hasSmoothedAnchor)
            {
                _smoothedAnchor = rawAnchor;
                _hasSmoothedAnchor = true;
                return _smoothedAnchor;
            }

            var delta = rawAnchor - _smoothedAnchor;
            if (!float.IsFinite(delta.sqrMagnitude) || delta.sqrMagnitude > AnchorResetDistanceSquared)
            {
                _smoothedAnchor = rawAnchor;
                return _smoothedAnchor;
            }

            _smoothedAnchor = Vector2.Lerp(_smoothedAnchor, rawAnchor, AnchorSmoothingFactor);
            var offsetToPlayer = playerPosition - _smoothedAnchor;
            if (offsetToPlayer.sqrMagnitude > MaxClusterDistanceSquared * 1.1f)
            {
                var clamped = offsetToPlayer.normalized * MaxClusterDistance;
                _smoothedAnchor = playerPosition - clamped;
            }

            return _smoothedAnchor;
        }

        private float SmoothPreferredRadius(float target, float safeRadius, float maxRadius)
        {
            target = Mathf.Clamp(target, safeRadius, maxRadius);
            if (!_hasSmoothedRadius || !float.IsFinite(_smoothedRadius))
            {
                _smoothedRadius = target;
                _hasSmoothedRadius = true;
                return _smoothedRadius;
            }

            if (Mathf.Abs(_smoothedRadius - target) > RadiusResetThreshold)
            {
                _smoothedRadius = target;
                return _smoothedRadius;
            }

            _smoothedRadius = Mathf.Lerp(_smoothedRadius, target, RadiusSmoothingFactor);
            return _smoothedRadius;
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

        private float ComputePreferredRadius(
            AiWorldState world,
            Vector2 playerPosition,
            float currentRadius,
            out float safeRadius,
            out float maxRadius)
        {
            var desiredGap = Mathf.Max(MinimumKiteRadius, world.Player.Radius * DesiredEnemyGapMultiplier);
            safeRadius = Mathf.Max(MinimumKiteRadius, desiredGap);
            var nearestEnemyEdge = float.MaxValue;

            for (var i = 0; i < _clusterMembers.Count; i++)
            {
                var enemy = _clusterMembers[i];
                var delta = enemy.Position - playerPosition;
                var distance = delta.magnitude - enemy.Radius;
                if (distance < nearestEnemyEdge)
                {
                    nearestEnemyEdge = distance;
                }
            }

            if (nearestEnemyEdge < float.MaxValue)
            {
                var clampedNearest = Mathf.Max(0f, nearestEnemyEdge);
                safeRadius = Mathf.Max(safeRadius, clampedNearest + world.Player.Radius + SafeRadiusPadding);
                var gapLimited = clampedNearest + world.Player.Radius + desiredGap + DesiredEnemyGapSlack;
                maxRadius = Mathf.Min(safeRadius + PreferredRadiusMaxOffset, gapLimited);
            }
            else
            {
                maxRadius = safeRadius + PreferredRadiusMaxOffset;
            }

            maxRadius = Mathf.Max(maxRadius, safeRadius + 0.1f);
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

        private float ComputeRadiusSpread(Vector2 anchor, float preferredRadius)
        {
            if (_clusterMembers.Count == 0)
            {
                return RadiusBand;
            }

            var sum = 0f;
            for (var i = 0; i < _clusterMembers.Count; i++)
            {
                var enemy = _clusterMembers[i];
                var distance = (enemy.Position - anchor).magnitude;
                sum += Mathf.Abs(distance - preferredRadius);
            }

            var average = sum / Mathf.Max(1, _clusterMembers.Count);
            return Mathf.Clamp(average, RadiusSpreadClampMin, RadiusSpreadClampMax);
        }

        private float ResolveArcHalfAngle(float radiusSpread, float safeRadius, EncirclementSnapshot encirclement)
        {
            var spreadRange = Mathf.Max(RadiusSpreadClampMax - RadiusSpreadClampMin, 0.0001f);
            var spreadFactor = Mathf.Clamp01((radiusSpread - RadiusSpreadClampMin) / spreadRange);
            var encirclementFactor = Mathf.Clamp01(encirclement.Intensity);
            var baseAngle = ArcHalfAngleDegreesBase
                - spreadFactor * ArcHalfAngleTightenPerSpread
                - encirclementFactor * ArcHalfAngleTightenPerEncirclement;
            return Mathf.Clamp(baseAngle, ArcHalfAngleDegreesMin, ArcHalfAngleDegreesMax);
        }

        private float UpdateArcCenter(Vector2 radialDirection)
        {
            if (!_hasArcCenter || radialDirection.sqrMagnitude < 0.0001f)
            {
                _arcCenterDirection = radialDirection.normalized;
                _hasArcCenter = true;
                return 0f;
            }

            var dot = Vector2.Dot(_arcCenterDirection, radialDirection);
            if (!float.IsFinite(dot) || dot <= -0.1f)
            {
                _arcCenterDirection = radialDirection.normalized;
                return 0f;
            }

            var angle = Mathf.Clamp(Vector2.SignedAngle(_arcCenterDirection, radialDirection), -180f, 180f);

            if (Mathf.Abs(angle) <= ArcCenterUpdateThresholdDegrees)
            {
                var blended = Vector2.Lerp(_arcCenterDirection, radialDirection, ArcCenterSmoothingFactor);
                if (blended.sqrMagnitude > 0.0001f)
                {
                    _arcCenterDirection = blended.normalized;
                }
            }

            return angle;
        }

        private (Vector2 clockwise, Vector2 counterClockwise) ComputeTangents(Vector2 radialDirection)
        {
            var clockwise = new Vector2(radialDirection.y, -radialDirection.x);
            var counterClockwise = new Vector2(-radialDirection.y, radialDirection.x);
            return (clockwise.normalized, counterClockwise.normalized);
        }

        private LaneEvaluation EvaluateLane(
            int orientation,
            Vector2 anchor,
            Vector2 tangentDirection,
            Vector2 radialDirection,
            float preferredRadius,
            float radiusTolerance,
            AiWorldState world)
        {
            var penalty = EvaluateLanePenalty(anchor, tangentDirection, radialDirection, preferredRadius, radiusTolerance);
            var (stragglerScore, radialAdjustment) = ComputeStragglerMetrics(anchor, tangentDirection, preferredRadius, radiusTolerance);
            var gemBias = ComputeGemBias(anchor, tangentDirection, preferredRadius, radiusTolerance, world.Gems);
            var escapeBias = ComputeEscapeBias(orientation, radialDirection, world.Encirclement);

            var score = (-penalty * LanePenaltyWeight)
                + (stragglerScore * StragglerBiasWeight)
                + (gemBias * GemBiasWeight)
                + (escapeBias * EscapeBiasWeight);

            return new LaneEvaluation(
                orientation,
                tangentDirection,
                penalty,
                score,
                stragglerScore,
                gemBias,
                escapeBias,
                radialAdjustment);
        }

        private (float score, float radialAdjustment) ComputeStragglerMetrics(
            Vector2 anchor,
            Vector2 tangentDirection,
            float preferredRadius,
            float radiusTolerance)
        {
            if (_clusterMembers.Count == 0)
            {
                return (0f, 0f);
            }

            var forward = tangentDirection.normalized;
            var radiusSlack = radiusTolerance * StragglerRadiusSlackMultiplier;
            var outwardAdjust = 0f;
            var inwardAdjust = 0f;
            var scoreAccumulator = 0f;
            var weightSum = 0f;

            for (var i = 0; i < _clusterMembers.Count; i++)
            {
                var obstacle = _clusterMembers[i];
                var offset = obstacle.Position - anchor;
                var distance = offset.magnitude;
                if (distance < 0.0001f)
                {
                    continue;
                }

                var direction = offset / distance;
                var alignment = Vector2.Dot(direction, forward);
                if (alignment <= StragglerAlignmentThreshold)
                {
                    continue;
                }

                var radialDelta = distance - preferredRadius;
                var weight = alignment * (1f + obstacle.Radius);
                weightSum += weight;

                if (radialDelta > radiusTolerance)
                {
                    var normalized = Mathf.Clamp(radialDelta / Mathf.Max(radiusSlack, 0.001f), 0f, 3f);
                    scoreAccumulator += weight * normalized;
                    outwardAdjust += Mathf.Clamp(radialDelta, 0f, StragglerAdjustClamp) * weight;
                }
                else if (radialDelta < -radiusTolerance)
                {
                    var normalized = Mathf.Clamp(-radialDelta / Mathf.Max(radiusSlack, 0.001f), 0f, 3f);
                    scoreAccumulator += weight * normalized * 0.6f;
                    inwardAdjust += Mathf.Clamp(-radialDelta, 0f, StragglerAdjustClamp) * weight;
                }
            }

            if (weightSum <= 0f)
            {
                return (0f, 0f);
            }

            var normalization = Mathf.Max(weightSum, 1f);
            var adjustment = Mathf.Clamp((outwardAdjust - inwardAdjust) / normalization, -StragglerAdjustClamp, StragglerAdjustClamp);
            var normalizedScore = Mathf.Clamp(scoreAccumulator / normalization, -3f, 3f);

            var clusterPenalty = Mathf.Max(0, _clusterMembers.Count - StragglerClusterBaseline);
            var clusterScale = 1f / (1f + clusterPenalty * StragglerClusterDamp);

            return (normalizedScore * clusterScale, adjustment * clusterScale);
        }

        private float ComputeGemBias(
            Vector2 anchor,
            Vector2 tangentDirection,
            float preferredRadius,
            float radiusTolerance,
            IReadOnlyList<GemSnapshot> gems)
        {
            if (gems.Count == 0)
            {
                return 0f;
            }

            var forward = tangentDirection.normalized;
            var minDistance = Mathf.Max(MinimumGemConsiderDistance, preferredRadius - radiusTolerance - 1f);
            var maxDistance = preferredRadius + radiusTolerance + GemDistanceSlack;
            var bias = 0f;

            for (var i = 0; i < gems.Count; i++)
            {
                var gem = gems[i];
                if (!gem.IsCollectible)
                {
                    continue;
                }

                var offset = gem.Position - anchor;
                var distance = offset.magnitude;
                if (distance < minDistance || distance > maxDistance)
                {
                    continue;
                }

                var direction = distance > 0f ? offset / distance : Vector2.zero;
                var alignment = Vector2.Dot(direction, forward);
                if (alignment <= GemAlignmentThreshold)
                {
                    continue;
                }

                var band = Mathf.Clamp01(1f - Mathf.Abs(distance - preferredRadius) / (radiusTolerance + 0.0001f));
                bias += alignment * band * (gem.Radius + 0.25f);
            }

            return bias;
        }

        private float ComputeEscapeBias(int orientation, Vector2 radialDirection, EncirclementSnapshot encirclement)
        {
            if (!encirclement.HasRing || !encirclement.HasBreakoutDirection || encirclement.Intensity <= 0f)
            {
                return 0f;
            }

            var breakout = encirclement.BreakoutDirection;
            if (breakout.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }

            var radialAngle = Mathf.Atan2(radialDirection.y, radialDirection.x) * Mathf.Rad2Deg;
            var breakoutAngle = Mathf.Atan2(breakout.y, breakout.x) * Mathf.Rad2Deg;
            var delta = Mathf.DeltaAngle(radialAngle, breakoutAngle);
            if (Mathf.Abs(delta) < 1f)
            {
                return 0f;
            }

            var preferredOrientation = delta > 0f ? -1 : 1;
            var magnitude = encirclement.Intensity * (1.15f - encirclement.GapOccupancy * 0.35f);
            return orientation == preferredOrientation ? magnitude : -magnitude * 0.65f;
        }

        private bool ShouldRequestFallback(LaneEvaluation selected, LaneEvaluation alternate, EncirclementSnapshot encirclement)
        {
            if (selected.Penalty >= LanePenaltyBlockThreshold && alternate.Penalty >= LanePenaltyBlockThreshold)
            {
                return true;
            }

            if (encirclement.HasRing && encirclement.Intensity >= 0.7f)
            {
                if (selected.Score < -0.35f && alternate.Score < 0f)
                {
                    return true;
                }
            }

            return false;
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

        private void ResetClusterState()
        {
            _hasSmoothedAnchor = false;
            _hasSmoothedRadius = false;
            _activeLaneOrientation = 0;
            _lastLaneSwitchTime = float.NegativeInfinity;
            _hasArcCenter = false;
            _arcCenterDirection = Vector2.zero;
        }

        private readonly struct LaneEvaluation
        {
            internal LaneEvaluation(
                int orientation,
                Vector2 direction,
                float penalty,
                float score,
                float stragglerScore,
                float gemScore,
                float escapeScore,
                float radialAdjustment)
            {
                Orientation = orientation;
                Direction = direction;
                Penalty = penalty;
                Score = score;
                StragglerScore = stragglerScore;
                GemScore = gemScore;
                EscapeScore = escapeScore;
                RadialAdjustment = radialAdjustment;
            }

            internal int Orientation { get; }
            internal Vector2 Direction { get; }
            internal float Penalty { get; }
            internal float Score { get; }
            internal float StragglerScore { get; }
            internal float GemScore { get; }
            internal float EscapeScore { get; }
            internal float RadialAdjustment { get; }
        }

    }
}
