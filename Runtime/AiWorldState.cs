using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.InteropTypes.Fields;
using Il2CppQFSW.MOP2;
using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.Tilemaps;
using Il2Cpp;
using CharacterController = Il2CppVampireSurvivors.Objects.Characters.CharacterController;
using EnemyController = Il2CppVampireSurvivors.Objects.Characters.EnemyController;
using Gem = Il2CppVampireSurvivors.Objects.Items.Gem;
using Stage = Il2CppVampireSurvivors.Objects.Stage;
using ClrBindingFlags = System.Reflection.BindingFlags;
using Il2CppBindingFlags = Il2CppSystem.Reflection.BindingFlags;

namespace AI_Mod.Runtime
{
    internal sealed class AiWorldState
    {
        private readonly List<DynamicObstacle> _enemies = new List<DynamicObstacle>();
        private readonly List<DynamicObstacle> _bullets = new List<DynamicObstacle>();
        private readonly List<GemSnapshot> _gems = new List<GemSnapshot>();
        private readonly List<WallTilemap> _wallTilemaps = new List<WallTilemap>();
        private readonly FallbackLogger _fallbacks = new FallbackLogger();
        private readonly List<BulletPoolBinding> _bulletPools = new List<BulletPoolBinding>();
        private static readonly HashSet<string> ProjectilePoolNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "BULLET_1",
            "BULLET_W"
        };
        private readonly Dictionary<string, VelocityLookup> _velocityLookupCache = new Dictionary<string, VelocityLookup>(StringComparer.Ordinal);
        private readonly Dictionary<string, BodyVelocityLookup> _bodyVelocityLookupCache = new Dictionary<string, BodyVelocityLookup>(StringComparer.Ordinal);
        private static bool _enemyVelocityPropertyAvailable = true;

        private Stage? _stage;
        private bool _wallsCached;
        private bool _bulletPoolsInitialized;
        private int _version = -1;
        private EncirclementSnapshot _encirclement = EncirclementSnapshot.Empty;

        internal PlayerSnapshot Player { get; private set; } = PlayerSnapshot.Empty;
        internal IReadOnlyList<DynamicObstacle> EnemyObstacles => _enemies;
        internal IReadOnlyList<DynamicObstacle> BulletObstacles => _bullets;
        internal IReadOnlyList<GemSnapshot> Gems => _gems;
        internal IReadOnlyList<WallTilemap> WallTilemaps => _wallTilemaps;
        [HideFromIl2Cpp]
        internal int Version => _version;
        internal EncirclementSnapshot Encirclement => _encirclement;

        internal void ClearTransient()
        {
            _enemies.Clear();
            _bullets.Clear();
            _gems.Clear();
            _wallTilemaps.Clear();
            _wallsCached = false;
            _stage = null;
            _fallbacks.ResetTransient();
            Player = PlayerSnapshot.Empty;
            _version = -1;
            _encirclement = EncirclementSnapshot.Empty;
        }

        internal void Refresh(CharacterController controller)
        {
            Player = BuildPlayerSnapshot(controller);
            RefreshEnemies();
            RefreshBullets();
            RefreshGems();
            RefreshEncirclement();

            if (!_wallsCached)
            {
                RefreshWalls();
                _wallsCached = true;
            }

            if (_version == int.MaxValue)
            {
                _fallbacks.WarnOnce("WorldVersionWrap", "World snapshot version wrapped to zero; TODO: monitor long sessions.");
                _version = 0;
                return;
            }

            _version = _version < 0 ? 0 : _version + 1;
        }

        private void RefreshEncirclement()
        {
            _encirclement = EncirclementSnapshot.Empty;
            if (!Player.IsValid || _enemies.Count == 0)
            {
                return;
            }

            var playerPosition = Player.Position;
            var playerRadius = Mathf.Max(Player.Radius, 0.1f);
            var enemyCount = _enemies.Count;
            if (enemyCount < 4)
            {
                return;
            }

            Span<int> binCounts = stackalloc int[EncirclementSnapshot.BinCount];
            Span<float> binNearest = stackalloc float[EncirclementSnapshot.BinCount];
            for (var i = 0; i < EncirclementSnapshot.BinCount; i++)
            {
                binCounts[i] = 0;
                binNearest[i] = float.PositiveInfinity;
            }

            var distances = new List<float>(enemyCount);
            for (var i = 0; i < enemyCount; i++)
            {
                var enemy = _enemies[i];
                var offset = enemy.Position - playerPosition;
                var distance = offset.magnitude;
                distances.Add(distance);

                var angle = Mathf.Atan2(offset.y, offset.x);
                var normalized = (angle + Mathf.PI) / (Mathf.PI * 2f);
                if (normalized < 0f)
                {
                    normalized += 1f;
                }
                else if (normalized >= 1f)
                {
                    normalized -= 1f;
                }

                var binIndex = Mathf.Clamp((int)(normalized * EncirclementSnapshot.BinCount), 0, EncirclementSnapshot.BinCount - 1);
                binCounts[binIndex]++;
                if (distance < binNearest[binIndex])
                {
                    binNearest[binIndex] = distance;
                }
            }

            distances.Sort();
            var medianDistance = ComputeMedian(distances);
            var radialDeviation = ComputeMedianAbsoluteDeviation(distances, medianDistance);
            var ringHalfWidth = Mathf.Max(playerRadius, radialDeviation * 1.5f);
            var exitRadius = medianDistance + ringHalfWidth;

            var occupiedBins = 0;
            var maxBinCount = 0;
            for (var i = 0; i < EncirclementSnapshot.BinCount; i++)
            {
                var count = binCounts[i];
                if (count > 0)
                {
                    maxBinCount = Mathf.Max(maxBinCount, count);
                    var nearest = binNearest[i];
                    if (nearest <= medianDistance + ringHalfWidth)
                    {
                        occupiedBins++;
                    }
                }
            }

            if (occupiedBins == 0)
            {
                return;
            }

            var coverage = occupiedBins / (float)EncirclementSnapshot.BinCount;
            var tightness = medianDistance > 0f
                ? Mathf.Clamp01(1f - (radialDeviation / (medianDistance + 0.0001f)))
                : 0f;
            var closeness = Mathf.Clamp01((playerRadius + ringHalfWidth) / (playerRadius + medianDistance + ringHalfWidth));
            var intensity = Mathf.Clamp01(coverage * tightness * closeness);
            if (intensity <= 0f)
            {
                return;
            }

            var bestGapIndex = -1;
            var bestGapScore = float.NegativeInfinity;
            for (var i = 0; i < EncirclementSnapshot.BinCount; i++)
            {
                var count = binCounts[i];
                var normalizedCount = maxBinCount > 0 ? 1f - (count / (float)maxBinCount) : 1f;
                var nearest = binNearest[i];
                if (float.IsPositiveInfinity(nearest))
                {
                    nearest = exitRadius;
                }

                var radialGap = Mathf.Clamp01((nearest - medianDistance) / (ringHalfWidth + 0.0001f));
                var gapScore = normalizedCount + radialGap;
                if (gapScore > bestGapScore)
                {
                    bestGapScore = gapScore;
                    bestGapIndex = i;
                }
            }

            if (bestGapIndex < 0)
            {
                return;
            }

            var gapAngle = ((bestGapIndex + 0.5f) / EncirclementSnapshot.BinCount) * Mathf.PI * 2f - Mathf.PI;
            var breakoutDirection = new Vector2(Mathf.Cos(gapAngle), Mathf.Sin(gapAngle));
            if (breakoutDirection.sqrMagnitude > 0.0001f)
            {
                breakoutDirection.Normalize();
            }
            else
            {
                breakoutDirection = Vector2.zero;
            }

            var gapOccupancy = bestGapScore > 0f ? Mathf.Clamp01(1f - (bestGapScore * 0.5f)) : 1f;

            _encirclement = new EncirclementSnapshot(
                true,
                coverage,
                tightness,
                closeness,
                intensity,
                breakoutDirection,
                gapOccupancy,
                medianDistance,
                ringHalfWidth,
                exitRadius);
        }

        private static float ComputeMedian(IReadOnlyList<float> samples)
        {
            var count = samples.Count;
            if (count == 0)
            {
                return 0f;
            }

            var mid = count / 2;
            if ((count & 1) == 0)
            {
                return 0.5f * (samples[mid - 1] + samples[mid]);
            }

            return samples[mid];
        }

        private static float ComputeMedianAbsoluteDeviation(IReadOnlyList<float> orderedSamples, float median)
        {
            var count = orderedSamples.Count;
            if (count == 0)
            {
                return 0f;
            }

            var deviations = new float[count];
            for (var i = 0; i < count; i++)
            {
                deviations[i] = Mathf.Abs(orderedSamples[i] - median);
            }

            Array.Sort(deviations);
            return ComputeMedian(deviations);
        }

        private PlayerSnapshot BuildPlayerSnapshot(CharacterController controller)
        {
            var position = TryGetVectorProperty(controller, nameof(controller.CurrentPos), out var pos)
                ? pos
                : ToVector2(controller.transform.position);

            var velocity = ExtractVelocity(controller, "Player");

            var moveSpeed = controller.PMoveSpeed();
            var radius = EstimateRadius(controller.gameObject, "Player");

            return new PlayerSnapshot(controller, position, velocity, radius, moveSpeed);
        }

        private Stage? EnsureStageReference()
        {
            if (_stage != null && !_stage.Equals(null))
            {
                var go = _stage.gameObject;
                if (go != null && go.activeInHierarchy)
                {
                    return _stage;
                }
            }

            _stage = null;

            var stages = UnityEngine.Object.FindObjectsOfType<Stage>(true);
            if (stages == null)
            {
                return null;
            }

            for (var i = 0; i < stages.Length; i++)
            {
                var candidate = stages[i];
                if (candidate == null || candidate.Equals(null))
                {
                    continue;
                }

                var go = candidate.gameObject;
                if (go == null || !go.activeInHierarchy)
                {
                    continue;
                }

                _stage = candidate;
                break;
            }

            return _stage;
        }

        private void RefreshEnemies()
        {
            _enemies.Clear();

            var stage = EnsureStageReference();
            if (stage == null || stage.Equals(null))
            {
                _fallbacks.WarnOnce("StageLookupFailed", "Stage component not found; enemy roster unavailable.");
                return;
            }

            var roster = stage.SpawnedEnemies;
            if (roster == null || roster.Equals(null))
            {
                _fallbacks.WarnOnce("StageSpawnedEnemiesMissing", "Stage.SpawnedEnemies unavailable; enemy roster unavailable.");
                return;
            }

            var count = roster.Count;
            for (var i = 0; i < count; i++)
            {
                var enemy = roster[i];
                AppendEnemyObstacle(enemy);
            }
        }

        private void AppendEnemyObstacle(EnemyController? enemy)
        {
            if (enemy == null || enemy.Equals(null))
            {
                return;
            }

            var go = enemy.gameObject;
            if (go == null || !go.activeInHierarchy)
            {
                return;
            }

            var position = enemy.transform.position;
            var velocity = ExtractVelocity(enemy, "Enemy");
            var radius = EstimateRadius(go, "Enemy");

            _enemies.Add(new DynamicObstacle(position, velocity, radius, ObstacleKind.Enemy));
        }

        private void RefreshBullets()
        {
            _bullets.Clear();

            if (!TryCollectBulletsFromPooler())
            {
                _fallbacks.WarnOnce("BulletPoolUnavailable", "Bullet pools unavailable; no bullets collected this frame.");
            }

            if (_bullets.Count == 0)
            {
                _fallbacks.InfoOnce("BulletScanEmpty", "No active bullets detected; planner continues without projectile avoidance.");
            }
        }
        private bool TryCollectBulletsFromPooler()
        {
            if (!EnsureBulletPoolBindings())
            {
                return false;
            }

            var success = true;
            for (var i = 0; i < _bulletPools.Count; i++)
            {
                var binding = _bulletPools[i];
                if (!binding.EnumerateInto(this))
                {
                    success = false;
                }
            }

            if (!success)
            {
                _bulletPoolsInitialized = false;
                _bulletPools.Clear();
            }

            return success;
        }
        private bool EnsureBulletPoolBindings()
        {
            if (_bulletPoolsInitialized)
            {
                for (var i = _bulletPools.Count - 1; i >= 0; i--)
                {
                    if (!_bulletPools[i].IsAlive)
                    {
                        _bulletPools.RemoveAt(i);
                    }
                }

                return _bulletPools.Count > 0;
            }

            _bulletPoolsInitialized = true;
            _bulletPools.Clear();

            MasterObjectPooler? pooler;
            try
            {
                pooler = MasterObjectPooler.Instance;
            }
            catch (TypeInitializationException ex)
            {
                _fallbacks.WarnOnce("BulletPoolSingletonFailure", $"MasterObjectPooler.Instance threw TypeInitializationException: {ex.Message}. Bullet data unavailable.");
                return false;
            }

            if (pooler == null || pooler.Equals(null))
            {
                return false;
            }

            var poolTable = pooler.PoolTable;
            if (poolTable == null || poolTable.Equals(null))
            {
                _fallbacks.WarnOnce("BulletPoolTableMissing", "MasterObjectPooler.PoolTable unavailable; bullet data unavailable.");
                return false;
            }

            foreach (var entry in poolTable)
            {
                var pool = entry.Value;
                if (pool == null || pool.Equals(null))
                {
                    continue;
                }

                var poolName = ExtractPoolName(entry.Key);
                if (string.IsNullOrEmpty(poolName))
                {
                    _fallbacks.WarnOnce("BulletPoolUnnamed", "Encountered unnamed bullet pool; skipping.");
                    continue;
                }

                if (!ProjectilePoolNames.Contains(poolName))
                {
                    _fallbacks.InfoOnce($"BulletPoolFiltered:{poolName}", $"Skipping non-projectile pool '{poolName}' when collecting bullets.");
                    continue;
                }

                var binding = BulletPoolBinding.TryCreate(this, poolName!, pool);
                if (binding != null)
                {
                    _bulletPools.Add(binding);
                }
            }

            if (_bulletPools.Count == 0)
            {
                _fallbacks.WarnOnce("BulletPoolListEmpty", "No bullet pools discovered in MasterObjectPooler; bullet data unavailable.");
                return false;
            }

            return true;
        }

        private static string? ExtractPoolName(object? rawKey)
        {
            if (rawKey == null)
            {
                return null;
            }

            switch (rawKey)
            {
                case string managed:
                    return managed;
                case Il2CppSystem.String il2cppString:
                    return il2cppString.ToString();
                default:
                    return rawKey.ToString();
            }
        }

        private bool TryEnumerateBulletPoolEntries(object aliveValue, BulletPoolBinding binding)
        {
            if (aliveValue is Il2CppSystem.Collections.Generic.Dictionary<int, GameObject> il2cppDict)
            {
                EnumerateIl2CppBulletDictionary(il2cppDict, binding);
                return true;
            }

            if (aliveValue is Il2CppSystem.Object il2cppObject)
            {
                var typed = il2cppObject.TryCast<Il2CppSystem.Collections.Generic.Dictionary<int, GameObject>>();
                if (typed != null && !typed.Equals(null))
                {
                    EnumerateIl2CppBulletDictionary(typed, binding);
                    return true;
                }

                var genericDict = il2cppObject.TryCast<Il2CppSystem.Collections.Generic.Dictionary<int, Il2CppSystem.Object>>();
                if (genericDict != null && !genericDict.Equals(null))
                {
                    var enumerator = genericDict.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        var entry = enumerator.Current;
                        var raw = entry.Value;
                        if (raw == null || raw.Equals(null))
                        {
                            continue;
                        }

                        var go = raw.TryCast<GameObject>();
                        if (go != null && !go.Equals(null))
                        {
                            binding.AppendBulletFromPoolObject(this, go);
                        }
                    }
                    enumerator.Dispose();
                    return true;
                }
            }

            if (aliveValue is Dictionary<int, GameObject> managedDict)
            {
                foreach (var entry in managedDict)
                {
                    binding.AppendBulletFromPoolObject(this, entry.Value);
                }
                return true;
            }

            if (aliveValue is IEnumerable<KeyValuePair<int, GameObject>> enumerable)
            {
                foreach (var entry in enumerable)
                {
                    binding.AppendBulletFromPoolObject(this, entry.Value);
                }
                return true;
            }

            if (aliveValue is IDictionary legacyDict)
            {
                foreach (DictionaryEntry entry in legacyDict)
                {
                    var value = entry.Value;
                    if (value is GameObject go && go != null && !go.Equals(null))
                    {
                        binding.AppendBulletFromPoolObject(this, go);
                        continue;
                    }

                    if (value is Il2CppSystem.Object il2Object)
                    {
                        var pooledGo = il2Object.TryCast<GameObject>();
                        if (pooledGo != null && !pooledGo.Equals(null))
                        {
                            binding.AppendBulletFromPoolObject(this, pooledGo);
                        }
                    }
                }
                return true;
            }

            return false;
        }

        private void EnumerateIl2CppBulletDictionary(Il2CppSystem.Collections.Generic.Dictionary<int, GameObject> dictionary, BulletPoolBinding binding)
        {
            var enumerator = dictionary.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var entry = enumerator.Current;
                binding.AppendBulletFromPoolObject(this, entry.Value);
            }
            enumerator.Dispose();
        }

        private sealed class BulletPoolBinding
        {
            private readonly string _poolName;
            private readonly ObjectPool _pool;
            private readonly Il2CppSystem.Reflection.FieldInfo _aliveField;
            private Il2CppSystem.Type? _cachedBehaviourType;
            private bool _behaviourFallbackLogged;

            private BulletPoolBinding(string poolName, ObjectPool pool, Il2CppSystem.Reflection.FieldInfo aliveField)
            {
                _poolName = poolName;
                _pool = pool;
                _aliveField = aliveField;
            }

            internal static BulletPoolBinding? TryCreate(AiWorldState owner, string poolName, ObjectPool pool)
            {
                try
                {
                    var poolType = pool.GetIl2CppType();
                    if (poolType == null)
                    {
                        owner._fallbacks.WarnOnce($"BulletPoolTypeMissing:{poolName}", $"Unable to resolve type metadata for bullet pool '{poolName}'; skipping pool.");
                        return null;
                    }

                    var aliveField = poolType.GetField(
                        "_aliveObjects",
                        Il2CppBindingFlags.Instance | Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic);
                    if (aliveField == null)
                    {
                        owner._fallbacks.WarnOnce($"BulletPoolAliveMissing:{poolName}", $"Bullet pool '{poolName}' missing _aliveObjects field; skipping pool.");
                        return null;
                    }

                    return new BulletPoolBinding(poolName, pool, aliveField);
                }
                catch (Exception ex)
                {
                    owner._fallbacks.WarnOnce($"BulletPoolBindingException:{poolName}", $"Encountered exception while preparing bullet pool '{poolName}': {ex.Message}. Skipping pool.");
                    return null;
                }
            }

            internal bool IsAlive => _pool != null && !_pool.Equals(null);

            internal bool EnumerateInto(AiWorldState owner)
            {
                if (_pool == null || _pool.Equals(null))
                {
                    owner._fallbacks.WarnOnce($"BulletPoolInvalid:{_poolName}", $"Bullet pool '{_poolName}' no longer valid; clearing binding.");
                    return false;
                }

                object? aliveObjects;
                try
                {
                    aliveObjects = _aliveField.GetValue(_pool);
                }
                catch (Exception ex)
                {
                    owner._fallbacks.WarnOnce($"BulletPoolAliveAccess:{_poolName}", $"Failed to access _aliveObjects for pool '{_poolName}': {ex.Message}. Bullet data omitted.");
                    return false;
                }

                if (aliveObjects == null)
                {
                    return true;
                }

                if (owner.TryEnumerateBulletPoolEntries(aliveObjects, this))
                {
                    return true;
                }

                owner._fallbacks.WarnOnce($"BulletPoolAliveUnexpected:{_poolName}", $"Bullet pool '{_poolName}' returned unsupported _aliveObjects type; skipping pool entries.");
                return false;
            }

            internal void AppendBulletFromPoolObject(AiWorldState owner, GameObject? go)
            {
                if (go == null || go.Equals(null))
                {
                    return;
                }

                var transform = go.transform;
                if (transform == null || transform.Equals(null))
                {
                    owner._fallbacks.WarnOnce($"BulletPoolTransformMissing:{_poolName}", $"Bullet pool '{_poolName}' entry missing transform; skipping.");
                    return;
                }

                var worldPosition = transform.position;
                var velocity = ResolveVelocity(owner, go);
                var radius = owner.EstimateRadius(go, "Bullet");

                owner._bullets.Add(new DynamicObstacle(
                    AiWorldState.ToVector2(worldPosition),
                    velocity,
                    radius,
                    ObstacleKind.Bullet));
            }

            private Vector2 ResolveVelocity(AiWorldState owner, GameObject go)
            {
                if (TryResolveBehaviour(owner, go, out var behaviour) && behaviour != null)
                {
                    return owner.ExtractVelocity(behaviour, $"Bullet[{_poolName}]");
                }

                if (!_behaviourFallbackLogged)
                {
                    owner._fallbacks.WarnOnce($"BulletPoolBehaviourMissing:{_poolName}", $"Bullet pool '{_poolName}' entries missing behaviour component; using zero velocity fallback.");
                    _behaviourFallbackLogged = true;
                }

                return Vector2.zero;
            }

            private bool TryResolveBehaviour(AiWorldState owner, GameObject go, out MonoBehaviour? behaviour)
            {
                behaviour = null;

                if (TryResolveCachedType(go, owner, out var component))
                {
                    behaviour = component?.TryCast<MonoBehaviour>();
                    if (behaviour != null && !behaviour.Equals(null))
                    {
                        return true;
                    }
                }

                var behaviours = go.GetComponents<MonoBehaviour>();
                if (behaviours == null)
                {
                    return false;
                }

                for (var i = 0; i < behaviours.Length; i++)
                {
                    var candidate = behaviours[i];
                    if (candidate == null || candidate.Equals(null))
                    {
                        continue;
                    }

                    behaviour = candidate;
                    CacheBehaviourType(candidate);
                    return true;
                }

                return false;
            }

            private bool TryResolveCachedType(GameObject go, AiWorldState owner, out Component? component)
            {
                component = null;

                if (_cachedBehaviourType == null)
                {
                    return false;
                }

                try
                {
                    component = go.GetComponent(_cachedBehaviourType);
                    if (component == null || component.Equals(null))
                    {
                        component = null;
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    owner._fallbacks.WarnOnce($"BulletPoolCachedTypeError:{_poolName}", $"Failed to use cached behaviour type for pool '{_poolName}': {ex.Message}. Clearing cache.");
                    _cachedBehaviourType = null;
                    component = null;
                    return false;
                }
            }

            private void CacheBehaviourType(Component behaviour)
            {
                if (behaviour == null || behaviour.Equals(null))
                {
                    return;
                }

                var il2Type = behaviour.GetIl2CppType();
                if (il2Type == null)
                {
                    return;
                }

                _cachedBehaviourType = il2Type;
            }
        }

        private void RefreshGems()
        {
            _gems.Clear();

            if (!TryCollectGemsFromPooler())
            {
                _fallbacks.WarnOnce("GemPoolUnavailable", "Gem pool unavailable; no gems collected this frame.");
            }
        }

        private bool TryCollectGemsFromPooler()
        {
            MasterObjectPooler? pooler;
            try
            {
                pooler = MasterObjectPooler.Instance;
            }
            catch (TypeInitializationException ex)
            {
                _fallbacks.WarnOnce("GemPoolSingletonFailure", $"MasterObjectPooler.Instance threw TypeInitializationException: {ex.Message}. Gem data unavailable.");
                return false;
            }

            if (pooler == null || pooler.Equals(null))
            {
                return false;
            }

            var poolTable = pooler.PoolTable;
            if (poolTable == null || poolTable.Equals(null))
            {
                _fallbacks.WarnOnce("GemPoolTableMissing", "MasterObjectPooler.PoolTable unavailable; gem data unavailable.");
                return false;
            }

            ObjectPool? gemsPool = null;
            foreach (var entry in poolTable)
            {
                var key = entry.Key;
                var managedKey = key == null ? null : (string?)key;
                if (!string.Equals(managedKey, "Gems", StringComparison.Ordinal))
                {
                    continue;
                }

                gemsPool = entry.Value;
                break;
            }

            if (gemsPool == null || gemsPool.Equals(null))
            {
                return false;
            }

            var poolType = gemsPool.GetIl2CppType();
            if (poolType == null)
            {
                _fallbacks.WarnOnce("GemPoolTypeLookupFailed", "Unable to resolve gem pool type metadata; skipping gem pool.");
                return false;
            }

            var aliveField = poolType.GetField(
                "_aliveObjects",
                Il2CppBindingFlags.Instance | Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic);
            if (aliveField == null)
            {
                _fallbacks.WarnOnce("GemPoolAliveFieldMissing", "Gem pool missing _aliveObjects field; skipping gem pool.");
                return false;
            }

            object? aliveValue;
            try
            {
                aliveValue = aliveField.GetValue(gemsPool);
            }
            catch (Exception ex)
            {
                _fallbacks.WarnOnce("GemPoolAliveAccessError", $"Exception while accessing gem pool _aliveObjects: {ex.Message}. Gem data unavailable.");
                return false;
            }

            if (aliveValue == null)
            {
                return true;
            }

            if (TryEnumerateGemPoolEntries(aliveValue))
            {
                return true;
            }

            var aliveIl2Type = (aliveValue as Il2CppSystem.Object)?.GetIl2CppType();
            var typeName = aliveIl2Type != null ? (string?)aliveIl2Type.FullName : "unknown";
            _fallbacks.WarnOnce("GemPoolAliveDictionaryUnexpected", $"Gem pool _aliveObjects had unexpected type '{typeName ?? "unknown"}'; gem data unavailable.");
            return false;
        }
        private void AppendGemFromPoolObject(GameObject? go)
        {
            if (go == null || go.Equals(null))
            {
                return;
            }

            var component = go.GetComponent(Il2CppType.Of<Gem>());
            var gem = component?.TryCast<Gem>();
            Transform? transform;
            if (gem != null && !gem.Equals(null))
            {
                transform = gem.transform;
            }
            else
            {
                _fallbacks.WarnOnce("GemPoolComponentMissing", "Gem pool entry missing Gem component; using GameObject transform fallback.");
                transform = go.transform;
            }

            if (transform == null || transform.Equals(null))
            {
                _fallbacks.WarnOnce("GemPoolTransformMissing", "Gem pool entry missing transform; skipping entry.");
                return;
            }

            AppendGemSnapshot(go, transform.position, go.activeInHierarchy);
        }

        private void AppendGemSnapshot(GameObject go, Vector3 worldPosition, bool collectible)
        {
            var position = ToVector2(worldPosition);
            var radius = EstimateRadius(go, "Gem");
            _gems.Add(new GemSnapshot(position, radius, collectible));
        }

        private bool TryEnumerateGemPoolEntries(object aliveValue)
        {
            if (aliveValue is Il2CppSystem.Collections.Generic.Dictionary<int, GameObject> il2cppDict)
            {
                EnumerateIl2CppDictionary(il2cppDict);
                return true;
            }

            if (aliveValue is Il2CppSystem.Object il2cppObject)
            {
                var typed = il2cppObject.TryCast<Il2CppSystem.Collections.Generic.Dictionary<int, GameObject>>();
                if (typed != null && !typed.Equals(null))
                {
                    EnumerateIl2CppDictionary(typed);
                    return true;
                }

                var genericDict = il2cppObject.TryCast<Il2CppSystem.Collections.Generic.Dictionary<int, Il2CppSystem.Object>>();
                if (genericDict != null && !genericDict.Equals(null))
                {
                    var enumerator = genericDict.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        var entry = enumerator.Current;
                        var rawValue = entry.Value;
                        if (rawValue == null || rawValue.Equals(null))
                        {
                            continue;
                        }

                        var gameObject = rawValue.TryCast<GameObject>();
                        if (gameObject != null && !gameObject.Equals(null))
                        {
                            AppendGemFromPoolObject(gameObject);
                        }
                    }
                    enumerator.Dispose();
                    return true;
                }
            }

            if (aliveValue is Dictionary<int, GameObject> managedDict)
            {
                foreach (var entry in managedDict)
                {
                    AppendGemFromPoolObject(entry.Value);
                }
                return true;
            }

            if (aliveValue is IEnumerable<KeyValuePair<int, GameObject>> enumerable)
            {
                foreach (var entry in enumerable)
                {
                    AppendGemFromPoolObject(entry.Value);
                }
                return true;
            }

            if (aliveValue is IDictionary legacyDict)
            {
                foreach (DictionaryEntry entry in legacyDict)
                {
                    var value = entry.Value;
                    if (value is GameObject go && go != null && !go.Equals(null))
                    {
                        AppendGemFromPoolObject(go);
                        continue;
                    }

                    if (value is Il2CppSystem.Object il2Object)
                    {
                        var pooledGo = il2Object.TryCast<GameObject>();
                        if (pooledGo != null && !pooledGo.Equals(null))
                        {
                            AppendGemFromPoolObject(pooledGo);
                        }
                    }
                }
                return true;
            }

            return false;
        }

        private void EnumerateIl2CppDictionary(Il2CppSystem.Collections.Generic.Dictionary<int, GameObject> dictionary)
        {
            var enumerator = dictionary.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var entry = enumerator.Current;
                AppendGemFromPoolObject(entry.Value);
            }
            enumerator.Dispose();
        }

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

            var grid = tilemap.layoutGrid;
            if (grid == null || grid.Equals(null))
            {
                var identifier = tilemap.gameObject != null ? tilemap.gameObject.name ?? "<unnamed>" : "<unknown>";
                _fallbacks.WarnOnce($"WallTilemapGridMissing:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' missing layout grid; skipping wall registration.");
                return false;
            }

            var cellSize = grid.cellSize;
            if (Mathf.Abs(cellSize.x) < 0.0001f || Mathf.Abs(cellSize.y) < 0.0001f)
            {
                var identifier = tilemap.gameObject != null ? tilemap.gameObject.name ?? "<unnamed>" : "<unknown>";
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
                var identifier = tilemap.gameObject != null ? tilemap.gameObject.name ?? "<unnamed>" : "<unknown>";
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
                var identifier = tilemap.gameObject != null ? tilemap.gameObject.name ?? "<unnamed>" : "<unknown>";
                _fallbacks.WarnOnce($"WallTilemapBoundsFallback:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' world bounds computation failed: {ex.Message}; skipping wall registration.");
                return false;
            }

            if (bounds.size.sqrMagnitude <= 0f)
            {
                var identifier = tilemap.gameObject != null ? tilemap.gameObject.name ?? "<unnamed>" : "<unknown>";
                _fallbacks.WarnOnce($"WallTilemapEmptyBounds:{tilemap.GetInstanceID()}", $"Tilemap '{identifier}' produced empty world bounds; skipping wall registration.");
                return false;
            }

            wallTilemap = new WallTilemap(tilemap, bounds, cellBounds, cellSize);
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

        private Vector2 ExtractVelocity(MonoBehaviour behaviour, string context)
        {
            if (behaviour == null || behaviour.Equals(null))
            {
                return Vector2.zero;
            }

            if (TryExtractEnemyVelocity(behaviour, out var enemyVelocity))
            {
                return enemyVelocity;
            }

            if (TryExtractCachedBehaviourVelocity(behaviour, out var cachedVelocity))
            {
                return cachedVelocity;
            }

            var go = behaviour.gameObject;
            if (go != null && !go.Equals(null))
            {
                if (TryResolveBody(go, context, out var body) && body != null && !body.Equals(null))
                {
                    if (TryExtractCachedBodyVelocity(body, out var bodyVelocity))
                    {
                        return bodyVelocity;
                    }

                    LogBodyVelocityDiagnostics(body, context);
                }
            }

            _fallbacks.WarnOnce($"{context}VelocityFallback", $"Falling back to zero velocity for {context}; BaseBody velocity unavailable.");
            return Vector2.zero;
        }

        private bool TryExtractEnemyVelocity(MonoBehaviour behaviour, out Vector2 velocity)
        {
            velocity = default;

            if (!_enemyVelocityPropertyAvailable)
            {
                return false;
            }

            var enemy = behaviour.TryCast<EnemyController>();
            if (enemy == null || enemy.Equals(null))
            {
                return false;
            }

            try
            {
                var raw = (object)enemy.Velocity;
                if (TryConvertVector(raw, out velocity))
                {
                    return true;
                }

                _enemyVelocityPropertyAvailable = false;
                _fallbacks.WarnOnce("EnemyVelocityPropertyUnsupported", "EnemyController.Velocity returned unsupported type; using fallback path.");
            }
            catch (Exception ex)
            {
                _enemyVelocityPropertyAvailable = false;
                _fallbacks.WarnOnce("EnemyVelocityAccessorDisabled", $"EnemyController.Velocity property unavailable: {ex.Message}. Falling back to cached reflection path.");
            }

            velocity = default;
            return false;
        }

        private bool TryExtractCachedBehaviourVelocity(MonoBehaviour behaviour, out Vector2 velocity)
        {
            velocity = default;

            var key = GetTypeCacheKey(behaviour);
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            if (!_velocityLookupCache.TryGetValue(key, out var lookup))
            {
                lookup = VelocityLookup.Create(behaviour);
                _velocityLookupCache[key] = lookup;
            }

            if (lookup.HasGetters && lookup.TryGet(behaviour, out velocity))
            {
                return true;
            }

            return false;
        }

        private bool TryExtractCachedBodyVelocity(BaseBody body, out Vector2 velocity)
        {
            velocity = default;

            var key = GetTypeCacheKey(body);
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            if (!_bodyVelocityLookupCache.TryGetValue(key, out var lookup))
            {
                lookup = BodyVelocityLookup.Create(body);
                _bodyVelocityLookupCache[key] = lookup;
            }

            if (lookup.HasGetters && lookup.TryGet(body, out velocity))
            {
                return true;
            }

            return false;
        }

        private static string GetTypeCacheKey(object instance)
        {
            if (instance == null)
            {
                return string.Empty;
            }

            if (instance is Il2CppSystem.Object il2Obj)
            {
                try
                {
                    var il2Type = il2Obj.GetIl2CppType();
                    if (il2Type != null)
                    {
                        var fullName = il2Type.FullName;
                        if (!string.IsNullOrEmpty(fullName))
                        {
                            return fullName;
                        }

                        var name = il2Type.Name;
                        if (!string.IsNullOrEmpty(name))
                        {
                            return name;
                        }
                    }
                }
                catch
                {
                }
            }

            var managedType = instance.GetType();
            if (managedType != null)
            {
                var fullName = managedType.FullName;
                if (!string.IsNullOrEmpty(fullName))
                {
                    return fullName;
                }

                var name = managedType.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }

                return managedType.ToString();
            }

            return instance.GetHashCode().ToString(CultureInfo.InvariantCulture);
        }

        private delegate bool Il2CppVelocityGetter(Il2CppSystem.Object instance, out Vector2 value);
        private delegate bool ManagedVelocityGetter(object instance, out Vector2 value);

        private static Il2CppVelocityGetter? BuildIl2CppVelocityGetter(Il2CppSystem.Object sample)
        {
            if (sample == null || sample.Equals(null))
            {
                return null;
            }

            var type = sample.GetIl2CppType();
            if (type == null)
            {
                return null;
            }

            if (TryLocateIl2CppVectorProperty(type, "Velocity", out var property) && property != null)
            {
                var nonNullProperty = property;
                return (Il2CppSystem.Object instance, out Vector2 value) =>
                {
                    try
                    {
                        var raw = nonNullProperty.GetValue(instance, null);
                        return TryConvertVector(raw, out value);
                    }
                    catch
                    {
                        value = default;
                        return false;
                    }
                };
            }

            if (TryLocateIl2CppVectorField(type, "_velocity", out var field) && field is Il2CppSystem.Reflection.FieldInfo nonNullField)
            {
                return (Il2CppSystem.Object instance, out Vector2 value) =>
                {
                    try
                    {
                        var raw = nonNullField.GetValue(instance);
                        return TryConvertVector(raw, out value);
                    }
                    catch
                    {
                        value = default;
                        return false;
                    }
                };
            }

            return null;
        }

        private static ManagedVelocityGetter? BuildManagedVelocityGetter(object sample)
        {
            if (sample == null)
            {
                return null;
            }

            var managedType = sample.GetType();
            if (managedType == null)
            {
                return null;
            }

            var flags = ClrBindingFlags.Instance | ClrBindingFlags.Public | ClrBindingFlags.NonPublic;

            for (Type? cursor = managedType; cursor != null; cursor = cursor.BaseType)
            {
                PropertyInfo? property = null;
                try
                {
                    property = cursor.GetProperty("Velocity", flags);
                }
                catch
                {
                    property = null;
                }

                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return (object instance, out Vector2 value) =>
                    {
                        try
                        {
                            var raw = property.GetValue(instance);
                            return TryConvertVector(raw, out value);
                        }
                        catch
                        {
                            value = default;
                            return false;
                        }
                    };
                }
            }

            for (Type? cursor = managedType; cursor != null; cursor = cursor.BaseType)
            {
                FieldInfo? field = null;
                try
                {
                    field = cursor.GetField("_velocity", flags);
                }
                catch
                {
                    field = null;
                }

                if (field != null)
                {
                    return (object instance, out Vector2 value) =>
                    {
                        try
                        {
                            var raw = field.GetValue(instance);
                            return TryConvertVector(raw, out value);
                        }
                        catch
                        {
                            value = default;
                            return false;
                        }
                    };
                }
            }

            return null;
        }

        private sealed class VelocityLookup
        {
            private readonly Il2CppVelocityGetter? _il2CppGetter;
            private readonly ManagedVelocityGetter? _managedGetter;

            private VelocityLookup(Il2CppVelocityGetter? il2CppGetter, ManagedVelocityGetter? managedGetter)
            {
                _il2CppGetter = il2CppGetter;
                _managedGetter = managedGetter;
            }

            internal bool HasGetters => _il2CppGetter != null || _managedGetter != null;

            internal bool TryGet(MonoBehaviour behaviour, out Vector2 value)
            {
                value = default;

                if (_il2CppGetter != null && behaviour is Il2CppSystem.Object il2Obj && !il2Obj.Equals(null))
                {
                    if (_il2CppGetter(il2Obj, out value))
                    {
                        return true;
                    }
                }

                if (_managedGetter != null)
                {
                    if (_managedGetter(behaviour, out value))
                    {
                        return true;
                    }
                }

                return false;
            }

            internal static VelocityLookup Create(MonoBehaviour sample)
            {
                Il2CppVelocityGetter? il2Getter = null;
                ManagedVelocityGetter? managedGetter = null;

                if (sample is Il2CppSystem.Object il2Obj && !il2Obj.Equals(null))
                {
                    il2Getter = BuildIl2CppVelocityGetter(il2Obj);
                }

                managedGetter = BuildManagedVelocityGetter(sample);

                return new VelocityLookup(il2Getter, managedGetter);
            }
        }

        private sealed class BodyVelocityLookup
        {
            private readonly Il2CppVelocityGetter? _il2CppGetter;
            private readonly ManagedVelocityGetter? _managedGetter;

            private BodyVelocityLookup(Il2CppVelocityGetter? il2CppGetter, ManagedVelocityGetter? managedGetter)
            {
                _il2CppGetter = il2CppGetter;
                _managedGetter = managedGetter;
            }

            internal bool HasGetters => _il2CppGetter != null || _managedGetter != null;

            internal bool TryGet(BaseBody body, out Vector2 value)
            {
                value = default;

                if (_il2CppGetter != null && body is Il2CppSystem.Object il2Obj && !il2Obj.Equals(null))
                {
                    if (_il2CppGetter(il2Obj, out value))
                    {
                        return true;
                    }
                }

                if (_managedGetter != null)
                {
                    if (_managedGetter(body, out value))
                    {
                        return true;
                    }
                }

                return false;
            }

            internal static BodyVelocityLookup Create(BaseBody sample)
            {
                Il2CppVelocityGetter? il2Getter = null;
                ManagedVelocityGetter? managedGetter = null;

                if (sample is Il2CppSystem.Object il2Obj && !il2Obj.Equals(null))
                {
                    il2Getter = BuildIl2CppVelocityGetter(il2Obj);
                }

                managedGetter = BuildManagedVelocityGetter(sample);

                return new BodyVelocityLookup(il2Getter, managedGetter);
            }
        }

        private static bool TryLocateIl2CppVectorProperty(Il2CppSystem.Type type, string propertyName, out Il2CppSystem.Reflection.PropertyInfo? property)
        {
            property = null;
            if (type == null)
            {
                return false;
            }

            var flags = Il2CppBindingFlags.Instance | Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic;
            var cursor = type;
            while (cursor != null)
            {
                Il2CppSystem.Reflection.PropertyInfo? candidate = null;
                try
                {
                    candidate = cursor.GetProperty(propertyName, flags);
                }
                catch
                {
                    candidate = null;
                }

                if (candidate != null && (candidate.GetIndexParameters()?.Length ?? 0) == 0)
                {
                    property = candidate;
                    return true;
                }

                cursor = cursor.BaseType;
            }

            return false;
        }

        private static bool TryLocateIl2CppVectorField(Il2CppSystem.Type type, string fieldName, out Il2CppSystem.Reflection.FieldInfo? field)
        {
            field = null;
            if (type == null)
            {
                return false;
            }

            var flags = Il2CppBindingFlags.Instance | Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic;
            var cursor = type;
            while (cursor != null)
            {
                Il2CppSystem.Reflection.FieldInfo? candidate = null;
                try
                {
                    candidate = cursor.GetField(fieldName, flags);
                }
                catch
                {
                    candidate = null;
                }

                if (candidate != null)
                {
                    field = candidate;
                    return true;
                }

                cursor = cursor.BaseType;
            }

            return false;
        }

        private bool TryResolveBody(GameObject go, string context, out BaseBody? body)
        {
            body = null;

            var component = go.GetComponent(Il2CppType.Of<PhaserGameObject>());
            var phaser = component?.TryCast<PhaserGameObject>();
            if (phaser == null || phaser.Equals(null))
            {
                _fallbacks.InfoOnce($"{context}BodyMissing", $"PhaserGameObject not found for {context}; using renderer bounds.");
                return false;
            }

            var direct = phaser.body;
            if (direct != null && !direct.Equals(null))
            {
                body = direct;
                return true;
            }

            _fallbacks.WarnOnce($"{context}BodyUnavailable", $"Phaser body unavailable for {context}; using renderer bounds.");
            return false;
        }

        private bool TryExtractBodyRadius(BaseBody body, out float radius)
        {
            radius = 0f;

            if (body == null || body.Equals(null))
            {
                return false;
            }

            var worldRadius = body.WorldRadius;
            if (worldRadius > 0f)
            {
                radius = worldRadius;
                return true;
            }

            var phaserRadius = body.PhaserRadius;
            if (phaserRadius > 0f)
            {
                radius = phaserRadius;
                return true;
            }

            var width = Math.Abs(body.right - body.left);
            var height = Math.Abs(body.top - body.bottom);
            var candidate = 0.5f * Mathf.Max(width, height);
            if (candidate > 0f)
            {
                radius = candidate;
                return true;
            }

            var rawRadius = body._radius;
            if (rawRadius > 0f)
            {
                radius = rawRadius;
                return true;
            }

            return false;
        }

        private float EstimateRadius(GameObject? go, string context)
        {
            if (go == null)
            {
                _fallbacks.WarnOnce($"{context}RadiusNullGO", $"GameObject missing for {context}; defaulting radius to 0.5.");
                return 0.5f;
            }

            if (TryResolveBody(go, context, out var body))
            {
                if (body != null && TryExtractBodyRadius(body, out var radiusFromBody))
                {
                    return radiusFromBody;
                }

                _fallbacks.WarnOnce($"{context}BodyRadiusFallback", $"Phaser body radius unavailable for {context}; using renderer bounds.");
            }

            var renderer = go.GetComponent<SpriteRenderer>() ?? go.GetComponentInChildren<SpriteRenderer>(true);
            if (renderer != null)
            {
                var extents = renderer.bounds.extents;
                var candidate = Mathf.Max(extents.x, extents.y);
                if (candidate > 0f)
                {
                    return candidate;
                }
            }

            _fallbacks.WarnOnce($"{context}RadiusFallback", $"Unable to derive radius for {context}; defaulting radius to 0.5.");
            return 0.5f;
        }

        private static bool TryGetVectorProperty(object source, string propertyName, out Vector2 value)
        {
            value = default;
            if (source == null) return false;

            if (source is UnityEngine.Object uobj)
            {
                var il2Type = uobj.GetIl2CppType();
                if (TryGetVectorPropertyFromIl2Cpp(uobj, il2Type, propertyName, out value))
                {
                    return true;
                }
            }
            else if (source is Il2CppSystem.Object il2Obj)
            {
                if (TryGetVectorPropertyFromIl2Cpp(il2Obj, il2Obj.GetIl2CppType(), propertyName, out value))
                {
                    return true;
                }
            }

            var flagsClr = ClrBindingFlags.Instance | ClrBindingFlags.Public | ClrBindingFlags.NonPublic;
            var mt = source.GetType();
            while (mt != null)
            {
                var mprop = mt.GetProperty(propertyName, flagsClr);
                if (mprop != null && mprop.GetIndexParameters().Length == 0)
                {
                    try { return TryConvertVector(mprop.GetValue(source), out value); }
                    catch { return false; }
                }

                mt = mt.BaseType;
            }
            return false;
        }

        private static bool TryGetVectorField(object source, string fieldName, out Vector2 value)
        {
            value = default;
            if (source == null) return false;

            if (source is UnityEngine.Object uobj)
            {
                if (TryGetVectorFieldFromIl2Cpp(uobj, uobj.GetIl2CppType(), fieldName, out value))
                {
                    return true;
                }
            }
            else if (source is Il2CppSystem.Object il2Obj)
            {
                if (TryGetVectorFieldFromIl2Cpp(il2Obj, il2Obj.GetIl2CppType(), fieldName, out value))
                {
                    return true;
                }
            }

            var flagsClr = ClrBindingFlags.Instance | ClrBindingFlags.Public | ClrBindingFlags.NonPublic;
            var mt = source.GetType();
            while (mt != null)
            {
                var mfield = mt.GetField(fieldName, flagsClr);
                if (mfield != null)
                {
                    try { return TryConvertVector(mfield.GetValue(source), out value); }
                    catch { return false; }
                }

                mt = mt.BaseType;
            }
            return false;
        }

        private static bool TryGetVectorPropertyFromIl2Cpp(object instance, Il2CppSystem.Type? type, string propertyName, out Vector2 value)
        {
            value = default;
            var il2Instance = instance as Il2CppSystem.Object;
            if (il2Instance == null || type == null)
            {
                return false;
            }

            var flags = Il2CppBindingFlags.Instance | Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic;
            while (type != null)
            {
                Il2CppSystem.Reflection.PropertyInfo? prop = null;
                try
                {
                    prop = type.GetProperty(propertyName, flags);
                }
                catch
                {
                    prop = null;
                }

                if (prop != null && (prop.GetIndexParameters()?.Length ?? 0) == 0)
                {
                    try
                    {
                        return TryConvertVector(prop.GetValue(il2Instance, null), out value);
                    }
                    catch
                    {
                        value = default;
                        return false;
                    }
                }

                type = type.BaseType;
            }

            return false;
        }

        private static bool TryGetVectorFieldFromIl2Cpp(object instance, Il2CppSystem.Type? type, string fieldName, out Vector2 value)
        {
            value = default;
            var il2Instance = instance as Il2CppSystem.Object;
            if (il2Instance == null || type == null)
            {
                return false;
            }

            var flags = Il2CppBindingFlags.Instance | Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic;
            while (type != null)
            {
                Il2CppSystem.Reflection.FieldInfo? field = null;
                try
                {
                    field = type.GetField(fieldName, flags);
                }
                catch
                {
                    field = null;
                }

                if (field != null)
                {
                    try
                    {
                        return TryConvertVector(field.GetValue(il2Instance), out value);
                    }
                    catch
                    {
                        value = default;
                        return false;
                    }
                }

                type = type.BaseType;
            }

            return false;
        }

        private static bool TryConvertVector(object? raw, out Vector2 value)
        {
            value = default;
            if (raw == null)
            {
                return false;
            }

            switch (raw)
            {
                case Vector2 vec2:
                    value = vec2;
                    return true;
                case Il2CppValueField<Vector2> vec2Field:
                    value = vec2Field.Value;
                    return true;
                case Vector3 vec3:
                    value = new Vector2(vec3.x, vec3.y);
                    return true;
                case Il2CppValueField<Vector3> vec3Field:
                    var v3 = vec3Field.Value;
                    value = new Vector2(v3.x, v3.y);
                    return true;
                case Unity.Mathematics.float2 float2:
                    value = new Vector2(float2.x, float2.y);
                    return true;
                case Il2CppValueField<Unity.Mathematics.float2> float2Field:
                    var v2 = float2Field.Value;
                    value = new Vector2(v2.x, v2.y);
                    return true;
            }

            if (raw is Il2CppSystem.Object il2Object && TryConvertIl2CppStructVector(il2Object, out value))
            {
                return true;
            }

            if (raw is Il2CppStructArray<Vector2> vectorArray && vectorArray.Length > 0)
            {
                value = vectorArray[0];
                return true;
            }

            if (raw is Il2CppStructArray<Vector3> vector3Array && vector3Array.Length > 0)
            {
                var item = vector3Array[0];
                value = new Vector2(item.x, item.y);
                return true;
            }

            return false;
        }

        private static bool TryConvertIl2CppStructVector(Il2CppSystem.Object il2Object, out Vector2 value)
        {
            value = default;
            if (il2Object == null)
            {
                return false;
            }

            try
            {
                var type = il2Object.GetIl2CppType();
                if (type == null)
                {
                    return false;
                }

                var fullName = type.FullName ?? string.Empty;
                if (!IsKnownVectorStruct(fullName))
                {
                    return false;
                }

                if (TryReadIl2CppVectorComponent(il2Object, type, "x", out var x) &&
                    TryReadIl2CppVectorComponent(il2Object, type, "y", out var y))
                {
                    value = new Vector2(x, y);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool IsKnownVectorStruct(string typeName)
        {
            return typeName == "Unity.Mathematics.float2" ||
                   typeName == "UnityEngine.Vector2" ||
                   typeName == "UnityEngine.Vector3";
        }

        private static bool TryReadIl2CppVectorComponent(Il2CppSystem.Object source, Il2CppSystem.Type type, string componentName, out float component)
        {
            component = 0f;
            if (source == null || type == null)
            {
                return false;
            }

            var flags = Il2CppBindingFlags.Instance | Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic;

            try
            {
                var property = type.GetProperty(componentName, flags);
                if (property != null)
                {
                    var raw = property.GetValue(source, null);
                    if (TryConvertFloat(raw, out component))
                    {
                        return true;
                    }
                }

                var field = type.GetField(componentName, flags);
                if (field != null)
                {
                    var raw = field.GetValue(source);
                    if (TryConvertFloat(raw, out component))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private void LogBodyVelocityDiagnostics(BaseBody body, string context)
        {
            try
            {
                var type = body.GetIl2CppType();
                var typeName = type != null ? type.FullName : "unknown";
                var propertyType = "unknown";
                var propertyValueType = "unknown";
                var propertyVector = "n/a";
                var fieldType = "unknown";
                var fieldValueType = "unknown";
                var fieldVector = "n/a";
                Type? managedType = null;

                if (type != null && TryDescribeIl2CppProperty(body, type, "Velocity", out var il2PropType, out var il2PropValueType, out var il2PropVector))
                {
                    propertyType = il2PropType;
                    propertyValueType = il2PropValueType;
                    propertyVector = il2PropVector;
                }
                else
                {
                    managedType = body.GetType();
                    if (managedType != null)
                    {
                        var flags = ClrBindingFlags.Instance | ClrBindingFlags.Public | ClrBindingFlags.NonPublic;
                        var mt = managedType;
                        while (mt != null)
                        {
                            var propertyInfo = mt.GetProperty("Velocity", flags);
                            if (propertyInfo != null)
                            {
                                propertyType = propertyInfo.PropertyType.FullName ?? "unknown";
                                try
                                {
                                    var value = propertyInfo.GetValue(body);
                                    propertyValueType = DescribeObject(value);
                                    if (TryConvertVector(value, out var converted))
                                    {
                                        propertyVector = $"{converted.x:F4},{converted.y:F4}";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    propertyValueType = $"error:{ex.Message}";
                                }

                                break;
                            }

                            mt = mt.BaseType;
                        }
                    }
                }

                if (body is Il2CppSystem.Object bodyIl2Field && TryDescribeIl2CppField(bodyIl2Field, bodyIl2Field.GetIl2CppType(), "_velocity", out var il2FieldType, out var il2FieldValueType, out var il2FieldVector))
                {
                    fieldType = il2FieldType;
                    fieldValueType = il2FieldValueType;
                    fieldVector = il2FieldVector;
                }
                else if (managedType != null)
                {
                    var flags = ClrBindingFlags.Instance | ClrBindingFlags.Public | ClrBindingFlags.NonPublic;
                    var mt = managedType;
                    while (mt != null)
                    {
                        var fieldInfo = mt.GetField("_velocity", flags);
                        if (fieldInfo != null)
                        {
                            fieldType = fieldInfo.FieldType.FullName ?? "unknown";
                            try
                            {
                                var value = fieldInfo.GetValue(body);
                                fieldValueType = DescribeObject(value);
                                if (TryConvertVector(value, out var converted))
                                {
                                    fieldVector = $"{converted.x:F4},{converted.y:F4}";
                                }
                            }
                            catch (Exception ex)
                            {
                                fieldValueType = $"error:{ex.Message}";
                            }

                            break;
                        }

                        mt = mt.BaseType;
                    }
                }

                _fallbacks.InfoOnce(
                    $"{context}VelocityDiagnostics",
                    $"Velocity diagnostics for {context}: BodyType={typeName}; VelocityPropertyType={propertyType}; VelocityPropertyValueType={propertyValueType}; VelocityPropertyVector={propertyVector}; _velocityFieldType={fieldType}; _velocityFieldValueType={fieldValueType}; _velocityFieldVector={fieldVector}");
            }
            catch (Exception ex)
            {
                _fallbacks.WarnOnce($"{context}VelocityDiagnosticsError", $"Velocity diagnostics failed: {ex.Message}");
            }
        }

        private static string DescribeObject(object? value)
        {
            if (value == null)
            {
                return "null";
            }

            try
            {
                if (value is Il2CppSystem.Object il2Obj)
                {
                    var type = il2Obj.GetIl2CppType();
                    if (type != null && !string.IsNullOrEmpty(type.FullName))
                    {
                        return type.FullName;
                    }
                }

                return value.GetType().FullName ?? value.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private static bool TryDescribeIl2CppProperty(Il2CppSystem.Object instance, Il2CppSystem.Type? type, string propertyName, out string propertyType, out string valueType, out string vectorValue)
        {
            propertyType = "unknown";
            valueType = "unknown";
            vectorValue = "n/a";

            if (instance == null || type == null)
            {
                return false;
            }

            var flags = Il2CppBindingFlags.Instance | Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic;

            while (type != null)
            {
                Il2CppSystem.Reflection.PropertyInfo? property = null;
                try
                {
                    property = type.GetProperty(propertyName, flags);
                }
                catch
                {
                    property = null;
                }

                if (property != null)
                {
                    propertyType = property.PropertyType?.FullName ?? "unknown";

                    try
                    {
                        var value = property.GetValue(instance, null);
                        valueType = DescribeObject(value);
                        if (TryConvertVector(value, out var converted))
                        {
                            vectorValue = $"{converted.x:F4},{converted.y:F4}";
                        }
                    }
                    catch (Exception ex)
                    {
                        valueType = $"error:{ex.Message}";
                    }

                    return true;
                }

                type = type.BaseType;
            }

            return false;
        }

        private static bool TryDescribeIl2CppField(Il2CppSystem.Object instance, Il2CppSystem.Type? type, string fieldName, out string fieldType, out string valueType, out string vectorValue)
        {
            fieldType = "unknown";
            valueType = "unknown";
            vectorValue = "n/a";

            if (instance == null || type == null)
            {
                return false;
            }

            var flags = Il2CppBindingFlags.Instance | Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic;
            var current = type;

            while (current != null)
            {
                Il2CppSystem.Reflection.FieldInfo? field = null;
                try
                {
                    field = current.GetField(fieldName, flags);
                }
                catch
                {
                    field = null;
                }

                if (field != null)
                {
                    fieldType = field.FieldType?.FullName ?? "unknown";
                    try
                    {
                        var value = field.GetValue(instance);
                        valueType = DescribeObject(value);
                        if (TryConvertVector(value, out var converted))
                        {
                            vectorValue = $"{converted.x:F4},{converted.y:F4}";
                        }
                    }
                    catch (Exception ex)
                    {
                        valueType = $"error:{ex.Message}";
                    }

                    return true;
                }

                current = current.BaseType;
            }

            return false;
        }

        private static bool TryConvertFloat(object? raw, out float value)
        {
            value = 0f;
            if (raw == null)
            {
                return false;
            }

            switch (raw)
            {
                case float f:
                    value = f;
                    return true;
                case double d:
                    value = (float)d;
                    return true;
                case int i:
                    value = i;
                    return true;
                case uint ui:
                    value = ui;
                    return true;
                case long l:
                    value = l;
                    return true;
                case ulong ul:
                    value = ul;
                    return true;
                case short s:
                    value = s;
                    return true;
                case ushort us:
                    value = us;
                    return true;
                case byte b:
                    value = b;
                    return true;
                case sbyte sb:
                    value = sb;
                    return true;
                case Il2CppValueField<float> floatField:
                    value = floatField.Value;
                    return true;
                case Il2CppValueField<double> doubleField:
                    value = (float)doubleField.Value;
                    return true;
            }

            if (raw is IConvertible convertible)
            {
                try
                {
                    value = convertible.ToSingle(CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                }
            }

            try
            {
                value = Convert.ToSingle(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                if (raw is string str && float.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    value = parsed;
                    return true;
                }
            }

            return false;
        }

        internal static Vector2 ToVector2(Vector3 value)
        {
            return new Vector2(value.x, value.y);
        }
    }

    internal readonly struct PlayerSnapshot
    {
        internal static readonly PlayerSnapshot Empty = new PlayerSnapshot(null, Vector2.zero, Vector2.zero, 0f, 0f);

        internal PlayerSnapshot(CharacterController? controller, Vector2 position, Vector2 velocity, float radius, float moveSpeed)
        {
            Controller = controller;
            Position = position;
            Velocity = velocity;
            Radius = radius;
            MoveSpeed = moveSpeed;
        }

        internal CharacterController? Controller { get; }
        internal Vector2 Position { get; }
        internal Vector2 Velocity { get; }
        internal float Radius { get; }
        internal float MoveSpeed { get; }
        internal bool IsValid => Controller != null && !Controller.Equals(null);
    }

    internal readonly struct GemSnapshot
    {
        internal GemSnapshot(Vector2 position, float radius, bool collectible)
        {
            Position = position;
            Radius = radius;
            IsCollectible = collectible;
        }

        internal Vector2 Position { get; }
        internal float Radius { get; }
        internal bool IsCollectible { get; }
    }

    internal readonly struct EncirclementSnapshot
    {
        internal const int BinCount = 12;

        internal static readonly EncirclementSnapshot Empty = new EncirclementSnapshot(
            false,
            0f,
            0f,
            0f,
            0f,
            Vector2.zero,
            1f,
            0f,
            0f,
            0f);

        internal EncirclementSnapshot(
            bool hasRing,
            float coverage,
            float tightness,
            float closeness,
            float intensity,
            Vector2 breakoutDirection,
            float gapOccupancy,
            float ringRadius,
            float ringHalfWidth,
            float exitRadius)
        {
            HasRing = hasRing;
            Coverage = coverage;
            Tightness = tightness;
            Closeness = closeness;
            Intensity = intensity;
            BreakoutDirection = breakoutDirection;
            GapOccupancy = Mathf.Clamp01(gapOccupancy);
            RingRadius = Mathf.Max(ringRadius, 0f);
            RingHalfWidth = Mathf.Max(ringHalfWidth, 0f);
            ExitRadius = Mathf.Max(exitRadius, 0f);
        }

        internal bool HasRing { get; }
        internal float Coverage { get; }
        internal float Tightness { get; }
        internal float Closeness { get; }
        internal float Intensity { get; }
        internal Vector2 BreakoutDirection { get; }
        internal float GapOccupancy { get; }
        internal float RingRadius { get; }
        internal float RingHalfWidth { get; }
        internal float ExitRadius { get; }
        internal bool HasBreakoutDirection => BreakoutDirection.sqrMagnitude > 0.0001f;
    }

    internal readonly struct DynamicObstacle
    {
        internal DynamicObstacle(Vector2 position, Vector2 velocity, float radius, ObstacleKind kind)
        {
            Position = position;
            Velocity = velocity;
            Radius = radius;
            Kind = kind;
        }

        internal Vector2 Position { get; }
        internal Vector2 Velocity { get; }
        internal float Radius { get; }
        internal ObstacleKind Kind { get; }
    }

    internal readonly struct WallTilemap
    {
        internal WallTilemap(Tilemap tilemap, Bounds worldBounds, BoundsInt cellBounds, Vector3 cellSize)
        {
            Tilemap = tilemap;
            WorldBounds = worldBounds;
            CellBounds = cellBounds;
            CellSize = cellSize;
        }

        internal Tilemap Tilemap { get; }
        internal Bounds WorldBounds { get; }
        internal BoundsInt CellBounds { get; }
        internal Vector3 CellSize { get; }
    }

    internal enum ObstacleKind
    {
        Enemy,
        Bullet
    }

    internal sealed class FallbackLogger
    {
        private readonly HashSet<string> _warned = new HashSet<string>();
        private readonly HashSet<string> _info = new HashSet<string>();

        internal void ResetTransient()
        {
            _info.Clear();
        }

        internal void WarnOnce(string key, string message)
        {
            if (_warned.Add(key))
            {
                MelonLogger.Warning(message);
            }
        }

        internal void InfoOnce(string key, string message)
        {
            if (_info.Add(key))
            {
                MelonLogger.Msg(message);
            }
        }
    }
}
