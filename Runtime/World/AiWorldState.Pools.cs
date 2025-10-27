using System;
using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppQFSW.MOP2;
using UnityEngine;
using ClrBindingFlags = System.Reflection.BindingFlags;
using Il2CppBindingFlags = Il2CppSystem.Reflection.BindingFlags;

namespace AI_Mod.Runtime
{
    internal sealed partial class AiWorldState
    {
        private readonly List<BulletPoolBinding> _bulletPools = new List<BulletPoolBinding>();
        private bool _bulletPoolsInitialized;

        private static readonly HashSet<string> ProjectilePoolNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "BULLET_1",
            "BULLET_W"
        };

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
                var key = entry.Key;
                var managedKey = key == null ? null : (string?)key;
                if (managedKey == null || !ProjectilePoolNames.Contains(managedKey))
                {
                    continue;
                }

                var pool = entry.Value;
                if (pool == null || pool.Equals(null))
                {
                    continue;
                }

                var binding = BulletPoolBinding.TryCreate(this, managedKey, pool);
                if (binding != null)
                {
                    _bulletPools.Add(binding);
                }
            }

            return _bulletPools.Count > 0;
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
                    if (component != null && !component.Equals(null))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    owner._fallbacks.WarnOnce($"BulletPoolCachedLookupError:{_poolName}", $"Component lookup failed for cached type on '{_poolName}': {ex.Message}; clearing cached type.");
                    _cachedBehaviourType = null;
                }

                return false;
            }

            private void CacheBehaviourType(Component component)
            {
                if (_cachedBehaviourType != null)
                {
                    return;
                }

                if (component is Il2CppSystem.Object il2Object && !il2Object.Equals(null))
                {
                    _cachedBehaviourType = il2Object.GetIl2CppType();
                }
            }
        }

        private void RefreshGems()
        {
            _gems.Clear();

            if (!TryCollectGemsFromPooler())
            {
                _fallbacks.WarnOnce("GemPoolUnavailable", "Gem pools unavailable; gem data skipped this frame.");
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

        private void AppendGemFromPoolObject(GameObject? go)
        {
            if (go == null || go.Equals(null))
            {
                return;
            }

            var component = go.GetComponent(Il2CppType.Of<Il2CppVampireSurvivors.Objects.Items.Gem>());
            var gem = component?.TryCast<Il2CppVampireSurvivors.Objects.Items.Gem>();
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
    }
}
