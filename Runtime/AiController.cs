using AI_Mod.Runtime.Brain;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using CharacterController = Il2CppVampireSurvivors.Objects.Characters.CharacterController;

namespace AI_Mod.Runtime
{
    internal sealed class AiController : MonoBehaviour
    {
        private const float WorldRefreshIntervalSeconds = 0.2f;

        private readonly AiWorldState _world = new AiWorldState();
        private readonly VelocityObstaclePlanner _planner = new VelocityObstaclePlanner();
        private readonly AiGameStateMonitor _stateMonitor = new AiGameStateMonitor();
        private readonly KitingPlanner _kitingPlanner = new KitingPlanner();

        private CharacterController? _player;
        private Vector2 _desiredDirection = Vector2.zero;
        private PlannerResult _lastPlan = PlannerResult.Zero;
        private float _lastWorldSyncTime;
        private int _lastPlannedWorldVersion = -1;
        private bool _playerLookupWarned;
        private bool _kitingFallbackActive;
        private bool _breakoutActive;

        public AiController(IntPtr pointer) : base(pointer)
        {
        }

        public AiController() : base(ClassInjector.DerivedConstructorPointer<AiController>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }

        private void Awake()
        {
            AiRuntime.Attach(this);
            DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            MelonLogger.Msg("AI controller awake.");
        }

        private void OnDestroy()
        {
            AiRuntime.Detach(this);
        }

        internal void HandleSceneChanged(Scene scene)
        {
            _stateMonitor.OnSceneChanged(scene);
            if (!_stateMonitor.IsGameplayScene)
            {
                _player = null;
                _desiredDirection = Vector2.zero;
                _world.ClearTransient();
                _lastPlannedWorldVersion = -1;
                _breakoutActive = false;
            }
        }

        private void Update()
        {
            _stateMonitor.Refresh();
            if (!_stateMonitor.IsGameplayActive)
            {
                _desiredDirection = Vector2.zero;
                return;
            }

            EnsurePlayerReference();
            if (_player == null)
            {
                return;
            }

            var worldUpdated = false;
            if (Time.unscaledTime - _lastWorldSyncTime >= WorldRefreshIntervalSeconds)
            {
                _world.Refresh(_player);
                _lastWorldSyncTime = Time.unscaledTime;
                worldUpdated = true;
            }

            if (_world.Version >= 0 && (worldUpdated || _lastPlannedWorldVersion != _world.Version))
            {
                var kitingDirective = _kitingPlanner.BuildDirective(_world);
                var plannerDirective = kitingDirective;
                string? fallbackMessage = null;

                if (kitingDirective.HasDirective && kitingDirective.FallbackRequested)
                {
                    fallbackMessage = "Kiting fallback engaged: orbit lanes blocked. Defaulting to velocity-obstacle escape.";
                    plannerDirective = KitingDirective.None;
                }

                var plan = _planner.Plan(_world, plannerDirective, _world.Encirclement);

                if (plannerDirective.HasDirective && plan.Mode == SteeringMode.Fallback && fallbackMessage == null)
                {
                    fallbackMessage = "Kiting fallback engaged: no safe orbit alignment. Defaulting to velocity-obstacle escape.";
                }

                if (fallbackMessage != null)
                {
                    if (!_kitingFallbackActive)
                    {
                        MelonLogger.Msg(fallbackMessage);
                    }

                    _kitingFallbackActive = true;
                }
                else
                {
                    if (_kitingFallbackActive && plan.Mode == SteeringMode.Kiting)
                    {
                        MelonLogger.Msg("Kiting fallback cleared; resuming orbit.");
                    }

                    _kitingFallbackActive = false;
                }

                _lastPlan = plan;
                _desiredDirection = plan.Direction;
                _lastPlannedWorldVersion = _world.Version;

                if (plan.Mode == SteeringMode.Breakout)
                {
                    if (!_breakoutActive)
                    {
                        MelonLogger.Msg($"Breakout engaged. Encirclement intensity {_world.Encirclement.Intensity:F2}.");
                    }
                    _breakoutActive = true;
                }
                else if (_breakoutActive)
                {
                    MelonLogger.Msg("Breakout cleared.");
                    _breakoutActive = false;
                }
            }
        }

        private void LateUpdate()
        {
            if (_player == null || !_stateMonitor.IsGameplayActive)
            {
                return;
            }

            ApplyDirection(_desiredDirection);
        }

        internal bool ShouldOverrideInputFor(CharacterController subject)
        {
            return _stateMonitor.IsGameplayActive && _player != null && subject.Pointer == _player.Pointer;
        }

        [HideFromIl2Cpp]
        internal AiWorldState WorldState => _world;
        internal PlannerResult LastPlan => _lastPlan;
        [HideFromIl2Cpp]
        internal PlannerDebugInfo PlannerDebug => _planner.DebugInfo;
        internal Vector2 DesiredDirection => _desiredDirection;
        internal bool IsGameplayActive => _stateMonitor.IsGameplayActive;
        [HideFromIl2Cpp]
        internal string? CurrentGameState => _stateMonitor.CurrentStateName;

        private void EnsurePlayerReference()
        {
            if (_player != null && _player.gameObject != null && _player.gameObject.activeInHierarchy)
            {
                return;
            }

            var controllers = UnityEngine.Object.FindObjectsOfType<CharacterController>(true);
            foreach (var candidate in controllers)
            {
                if (candidate != null && candidate.gameObject != null && candidate.gameObject.activeInHierarchy)
                {
                    _player = candidate;
                    _playerLookupWarned = false;
                    return;
                }
            }

            var fallback = GameObject.Find("Character_Default(Clone)");
            if (fallback != null)
            {
                var component = fallback.GetComponent(Il2CppType.Of<CharacterController>());
                var controller = component?.TryCast<CharacterController>();
                if (controller != null)
                {
                    _player = controller;
                    _playerLookupWarned = false;
                    MelonLogger.Msg("Character controller resolved via fallback GameObject lookup.");
                    return;
                }
            }

            if (!_playerLookupWarned)
            {
                MelonLogger.Warning("Player CharacterController not located. AI remains idle. TODO: validate lookup path.");
                _playerLookupWarned = true;
            }
        }

        private void ApplyDirection(Vector2 desiredDirection)
        {
            if (_player == null || _player.Equals(null))
            {
                return;
            }

            var direction = desiredDirection.sqrMagnitude > 0.0001f ? desiredDirection.normalized : Vector2.zero;

            _player._currentDirection = direction;
            _player._currentDirectionRaw = direction;
            _player._lastMovementDirection = direction;
            _player._lastFacingDirection = direction;
        }
    }

    internal readonly struct PlannerResult
    {
        internal static readonly PlannerResult Zero = new PlannerResult(Vector2.zero, SteeringMode.Idle);

        internal PlannerResult(Vector2 direction, SteeringMode mode)
        {
            Direction = direction;
            Mode = mode;
        }

        internal Vector2 Direction { get; }
        internal SteeringMode Mode { get; }
    }

    internal enum SteeringMode
    {
        Idle,
        VelocityObstacle,
        Kiting,
        Fallback,
        Breakout
    }

    internal sealed class PlannerDebugInfo
    {
        private readonly List<PlannerCandidate> _candidates = new List<PlannerCandidate>(32);
        private readonly List<Vector2> _bestTrajectory = new List<Vector2>();
        private float _bestScore = float.MinValue;
        private Vector2 _bestDirection = Vector2.zero;
        private bool _hasBest;
        private float _bestEnemyOverlapSeconds;
        private float _bestBulletOverlapSeconds;
        private float _bestBreakoutExitTime;
        private bool _bestBreakoutActive;
        private float _encirclementIntensity;

        internal IReadOnlyList<PlannerCandidate> Candidates => _candidates;
        internal IReadOnlyList<Vector2> BestTrajectory => _bestTrajectory;
        internal float BestScore => _hasBest ? _bestScore : float.MinValue;
        internal Vector2 BestDirection => _hasBest ? _bestDirection : Vector2.zero;
        internal bool HasBest => _hasBest;
        internal float BestEnemyOverlapSeconds => _hasBest ? _bestEnemyOverlapSeconds : 0f;
        internal float BestBulletOverlapSeconds => _hasBest ? _bestBulletOverlapSeconds : 0f;
        internal float BestTotalOverlapSeconds => _hasBest ? _bestEnemyOverlapSeconds + _bestBulletOverlapSeconds : 0f;
        internal float BestBreakoutExitTime => _hasBest ? _bestBreakoutExitTime : float.PositiveInfinity;
        internal bool BreakoutActive => _hasBest && _bestBreakoutActive;
        internal float EncirclementIntensity => _hasBest ? _encirclementIntensity : 0f;

        internal void Begin()
        {
            _candidates.Clear();
            _bestTrajectory.Clear();
            _bestDirection = Vector2.zero;
            _bestScore = float.MinValue;
            _hasBest = false;
            _bestEnemyOverlapSeconds = 0f;
            _bestBulletOverlapSeconds = 0f;
            _bestBreakoutExitTime = float.PositiveInfinity;
            _bestBreakoutActive = false;
            _encirclementIntensity = 0f;
        }

        internal void RecordCandidate(Vector2 direction, float score, float enemyOverlapSeconds, float bulletOverlapSeconds)
        {
            _candidates.Add(new PlannerCandidate(direction, score, enemyOverlapSeconds, bulletOverlapSeconds));
        }

        internal void RecordBest(
            Vector2 direction,
            float score,
            IReadOnlyList<Vector2> trajectory,
            float enemyOverlapSeconds,
            float bulletOverlapSeconds,
            float breakoutExitTime,
            bool breakoutActive,
            float encirclementIntensity)
        {
            _bestDirection = direction;
            _bestScore = score;
            _hasBest = true;
            _bestEnemyOverlapSeconds = enemyOverlapSeconds;
            _bestBulletOverlapSeconds = bulletOverlapSeconds;
            _bestBreakoutExitTime = breakoutExitTime;
            _bestBreakoutActive = breakoutActive;
            _encirclementIntensity = encirclementIntensity;

            _bestTrajectory.Clear();
            if (trajectory != null)
            {
                for (var i = 0; i < trajectory.Count; i++)
                {
                    _bestTrajectory.Add(trajectory[i]);
                }
            }
        }
    }

    internal readonly struct PlannerCandidate
    {
        internal PlannerCandidate(Vector2 direction, float score, float enemyOverlapSeconds, float bulletOverlapSeconds)
        {
            Direction = direction;
            Score = score;
            EnemyOverlapSeconds = enemyOverlapSeconds;
            BulletOverlapSeconds = bulletOverlapSeconds;
        }

        internal Vector2 Direction { get; }
        internal float Score { get; }
        internal float EnemyOverlapSeconds { get; }
        internal float BulletOverlapSeconds { get; }
    }

    internal sealed class VelocityObstaclePlanner
    {
        private const int DirectionSamples = 20;
        private const float SimulationStep = 0.1f;
        private const int SimulationSteps = 8;
        private const float SimulationDuration = SimulationSteps * SimulationStep;
        private const float MinimumSeparation = 1.5f;
        private const float WallPenaltyWeight = 60f;
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

            var alignment = ComputeTangentialAlignment(direction, directive);
            var tangentialScore = alignment * KitingAlignmentWeight;

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

            var primary = Vector2.Dot(direction, directive.OrbitDirection);
            if (!directive.HasAlternateOrbit)
            {
                return primary;
            }

            var alternate = Vector2.Dot(direction, directive.AlternateOrbitDirection);
            return Mathf.Abs(alternate) > Mathf.Abs(primary) ? alternate : primary;
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

                if (CircleIntersectsBounds(center, radius, entry.WorldBounds))
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

                if (!CircleIntersectsBounds(position, safeRadius, entry.WorldBounds))
                {
                    continue;
                }

                for (var j = 0; j < boundingBoxes.Length; j++)
                {
                    var rect = boundingBoxes[j];
                    var distanceSquared = DistanceSquaredToRect(rect, position);

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

        private static bool CircleIntersectsBounds(Vector2 center, float radius, Bounds bounds)
        {
            var query = new Vector3(center.x, center.y, bounds.center.z);
            if (radius <= 0f)
            {
                return bounds.Contains(query);
            }

            var closest = bounds.ClosestPoint(query);
            var dx = closest.x - center.x;
            var dy = closest.y - center.y;
            var radiusSquared = radius * radius;
            return dx * dx + dy * dy <= radiusSquared;
        }

        private static float DistanceSquaredToRect(Rect rect, Vector2 point)
        {
            var dx = 0f;
            if (point.x < rect.xMin)
            {
                dx = rect.xMin - point.x;
            }
            else if (point.x > rect.xMax)
            {
                dx = point.x - rect.xMax;
            }

            var dy = 0f;
            if (point.y < rect.yMin)
            {
                dy = rect.yMin - point.y;
            }
            else if (point.y > rect.yMax)
            {
                dy = point.y - rect.yMax;
            }

            return dx * dx + dy * dy;
        }

        private float EvaluateGemReward(Vector2 position, IReadOnlyList<GemSnapshot> gems)
        {
            var best = 0f;
            // Use only the most accessible gem per step so safety penalties remain dominant.
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
