using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.InteropTypes.Fields;
using Unity.Mathematics;
using UnityEngine;
using CharacterController = Il2CppVampireSurvivors.Objects.Characters.CharacterController;
using EnemyController = Il2CppVampireSurvivors.Objects.Characters.EnemyController;
using ClrBindingFlags = System.Reflection.BindingFlags;
using Il2CppBindingFlags = Il2CppSystem.Reflection.BindingFlags;

namespace AI_Mod.Runtime
{
    internal sealed partial class AiWorldState
    {
        private readonly Dictionary<string, VelocityLookup> _velocityLookupCache = new Dictionary<string, VelocityLookup>(StringComparer.Ordinal);
        private readonly Dictionary<string, BodyVelocityLookup> _bodyVelocityLookupCache = new Dictionary<string, BodyVelocityLookup>(StringComparer.Ordinal);
        private static bool _enemyVelocityPropertyAvailable = true;

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

            if (TryLocateIl2CppVectorProperty(type, "Velocity", out var property))
            {
                return (Il2CppSystem.Object instance, out Vector2 value) =>
                {
                    value = default;
                    if (instance == null || instance.Equals(null))
                    {
                        return false;
                    }

                    try
                    {
                        var raw = property!.GetValue(instance, null);
                        return TryConvertVector(raw, out value);
                    }
                    catch
                    {
                        value = default;
                        return false;
                    }
                };
            }

            if (TryLocateIl2CppVectorField(type, "_velocity", out var field))
            {
                return (Il2CppSystem.Object instance, out Vector2 value) =>
                {
                    value = default;
                    if (instance == null || instance.Equals(null))
                    {
                        return false;
                    }

                    try
                    {
                        var raw = field!.GetValue(instance);
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
            if (source == null)
            {
                return false;
            }

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
                    try
                    {
                        return TryConvertVector(mprop.GetValue(source), out value);
                    }
                    catch
                    {
                        return false;
                    }
                }

                mt = mt.BaseType;
            }

            return false;
        }

        private static bool TryGetVectorField(object source, string fieldName, out Vector2 value)
        {
            value = default;
            if (source == null)
            {
                return false;
            }

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
                    try
                    {
                        return TryConvertVector(mfield.GetValue(source), out value);
                    }
                    catch
                    {
                        return false;
                    }
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
                case float2 float2:
                    value = new Vector2(float2.x, float2.y);
                    return true;
                case Il2CppValueField<float2> float2Field:
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
            }

            if (raw is Il2CppValueField<float> floatField)
            {
                value = floatField.Value;
                return true;
            }

            if (raw is Il2CppValueField<double> doubleField)
            {
                value = (float)doubleField.Value;
                return true;
            }

            return false;
        }

        internal static Vector2 ToVector2(Vector3 value)
        {
            return new Vector2(value.x, value.y);
        }
    }
}
