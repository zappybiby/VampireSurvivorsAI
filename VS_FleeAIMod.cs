using HarmonyLib;
using MelonLoader;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

// Game-specific
using Il2CppVampireSurvivors.Objects.Characters;
using Il2CppVampireSurvivors.Data;

// Tilemap + IL2CPP collections
using UnityEngine.Tilemaps;
using Il2CppSystem.Collections;
using Il2CppInterop.Runtime.Injection;

// Aliases
using SysGen    = System.Collections.Generic;
using SysCol    = System.Collections;
using Il2CppGen = Il2CppSystem.Collections.Generic;
using VSCharController = Il2CppVampireSurvivors.Objects.Characters.CharacterController;
using EnemyController = Il2CppVampireSurvivors.Objects.Characters.EnemyController;

using PTB  = Il2Cpp.PhaserTilemapBoundingBoxes;
using PTBA = Il2Cpp.PhaserTilemapBoundingBoxesAsset;

[assembly: MelonInfo(typeof(AutoEvade.AutoEvadeMod), "AutoEvade", "2.1.1", "you")]
[assembly: MelonGame("poncle", "Vampire Survivors")]

namespace AutoEvade
{
    // ---------------------------- SCENE UTIL -----------------------------------
    internal static class SceneUtil
    {
        public static bool IsGameplayScene()
        {
            var s = SceneManager.GetActiveScene();
            if (!s.IsValid()) return false;

            return s.name.Equals("Gameplay", StringComparison.OrdinalIgnoreCase)
                || s.name.StartsWith("Gameplay", StringComparison.OrdinalIgnoreCase);
        }
    }

    public class AutoEvadeMod : MelonMod
    {
        private static UnityAction<Scene, LoadSceneMode> _sceneLoadedHandler;

        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("AutoEvade v2.1.1 loaded: hijack HandlePlayerInput + walls + TtC.");

            // Register our MonoBehaviour with Il2Cpp before adding it.
            try { ClassInjector.RegisterTypeInIl2Cpp<DebugDrawer>(); }
            catch (Exception e) { MelonLogger.Warning($"Type already registered or failed: {e.Message}"); }

            // Create overlay
            var go = new GameObject("AutoEvadeDebugDrawer");
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<DebugDrawer>();
            UnityEngine.Object.DontDestroyOnLoad(go);

            // Rebuild walls only when we actually enter a gameplay scene (IL2CPP-safe)
            if (_sceneLoadedHandler == null)
            {
                _sceneLoadedHandler =
                    Il2CppInterop.Runtime.DelegateSupport
                        .ConvertDelegate<UnityAction<Scene, LoadSceneMode>>(OnSceneLoaded);
                SceneManager.add_sceneLoaded(_sceneLoadedHandler);
            }

            // Ensure Harmony patches are applied
            try
            {
                HarmonyInstance.PatchAll(typeof(AutoEvadeMod).Assembly);
                MelonLogger.Msg("[AutoEvade] Harmony patches applied.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[AutoEvade] Harmony patching failed: {e}");
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (SceneUtil.IsGameplayScene())
            {
                WallCache.Invalidate();
                MelonLogger.Msg($"[AutoEvade] Gameplay scene '{scene.name}' detected. Wall cache will rebuild on demand.");
            }
        }

        public override void OnDeinitializeMelon()
        {
            if (_sceneLoadedHandler != null)
                SceneManager.remove_sceneLoaded(_sceneLoadedHandler);
        }
    }

    // ---------------------------- WALL CACHE ----------------------------------

    internal static class WallCache
    {
        private static SysGen.List<Rect> _wallRects = new SysGen.List<Rect>();
        private static string _sceneNameBuilt = null;
        private static bool _builtOnce = false;

        private static float _nextRetryTime = 0f;
        private const float RETRY_SECONDS = 1.5f;

        public static SysGen.IReadOnlyList<Rect> WallRects
        {
            get
            {
                TryBuildIfNeeded();
                return _wallRects;
            }
        }

        public static void Invalidate()
        {
            _wallRects.Clear();
            _sceneNameBuilt = null;
            _builtOnce = false;
            _nextRetryTime = 0f;
            MelonLogger.Msg("AutoEvade: wall cache invalidated.");
        }

        public static void Pump()
        {
            if (!SceneUtil.IsGameplayScene()) return;

            if (_builtOnce && _wallRects.Count == 0 && Time.unscaledTime >= _nextRetryTime)
            {
                MelonLogger.Msg("AutoEvade: retrying wall build (0 rects so far)...");
                _builtOnce = false; // allow TryBuildIfNeeded to run again
                _nextRetryTime = Time.unscaledTime + RETRY_SECONDS;
            }
        }

        private static bool IsWallName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.EndsWith("_PlayerWall", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Walls", StringComparison.OrdinalIgnoreCase)
                || name.Contains(".tmx_PlayerWall", StringComparison.OrdinalIgnoreCase)
                || name.Contains(".tmx_Walls", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "PlayerWall", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Walls", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryBuildIfNeeded()
        {
            if (!SceneUtil.IsGameplayScene()) return;

            var scene = SceneManager.GetActiveScene();
            if (_builtOnce && _sceneNameBuilt == scene.name) return;

            _sceneNameBuilt = scene.name;
            _builtOnce = true;
            _wallRects.Clear();

            int hitsFromMono = 0;
            int hitsFromAsset = 0; // kept for logging parity

            try
            {
                PTB[] boxers = null;
                try
                {
                    // Use FindObjectsOfTypeAll so we catch inactive objects too; filter to current scene
                    boxers = Resources.FindObjectsOfTypeAll<PTB>();
                }
                catch { }

                if (boxers != null && boxers.Length > 0)
                {
                    foreach (var pb in boxers)
                    {
                        if (pb == null) continue;
                        var go = pb.gameObject;
                        if (go == null) continue;
                        if (!go.scene.IsValid() || go.scene.name != scene.name) continue; // ignore prefabs/other scenes

                        // The "Walls" nodes can be named a few ways; check this object and a couple of parents
                        var n0 = go.name;
                        var n1 = go.transform?.parent?.name;
                        var n2 = go.transform?.parent?.parent?.name;

                        if (!(IsWallName(n0) || IsWallName(n1) || IsWallName(n2)))
                            continue;

                        // Try to get a Tilemap on this object; if none, a parent Grid can still convert cells to world
                        var tilemap = go.GetComponent<Tilemap>() ?? go.GetComponentInChildren<Tilemap>();
                        var grid    = go.GetComponentInParent<Grid>();

                        // Grab the asset that carries the baked boxes (wrapper exposes _asset, not asset)
                        PTBA asset = null;
                        try { asset = pb._asset; } catch { }
                        if (asset == null)
                            continue;

                        Il2CppGen.List<BoundsInt> boundsList = null;
                        try { boundsList = asset.allBounds; } catch { }
                        if (boundsList == null || boundsList.Count == 0)
                            continue;

                        // Determine cell size from Tilemap/Grid
                        Vector2 cell = Vector2.zero;
                        try
                        {
                            if (tilemap != null)
                            {
                                var cs = tilemap.cellSize;
                                if (cs.x > 0.0001f && cs.y > 0.0001f)
                                    cell = new Vector2(cs.x, cs.y);
                            }
                            else if (grid != null)
                            {
                                var cs = grid.cellSize;
                                if (cs.x > 0.0001f && cs.y > 0.0001f)
                                    cell = new Vector2(cs.x, cs.y);
                            }
                        }
                        catch { }

                        if (cell == Vector2.zero)
                            continue;

                        // Local converter: prefer Tilemap/Grid CellToWorld; otherwise multiply by cell size
                        Func<Vector3Int, Vector3> cellToWorld = v =>
                        {
                            try
                            {
                                if (tilemap != null) return tilemap.CellToWorld(v);
                                if (grid != null)    return grid.CellToWorld(v);
                            }
                            catch { }
                            return new Vector3(v.x * cell.x, v.y * cell.y, 0f);
                        };

                        foreach (var bi in boundsList)
                        {
                            if (bi.size.x <= 0 || bi.size.y <= 0) continue;

                            Vector3 worldMin3 = cellToWorld(new Vector3Int(bi.xMin, bi.yMin, 0));
                            Vector2 sizeWorld = new Vector2(
                                Mathf.Max(0.0001f, bi.size.x * cell.x),
                                Mathf.Max(0.0001f, bi.size.y * cell.y));

                            var rect = new Rect(worldMin3.x, worldMin3.y, sizeWorld.x, sizeWorld.y);
                            if (rect.width > 0.001f && rect.height > 0.001f)
                            {
                                _wallRects.Add(rect);
                                hitsFromMono++;
                            }
                        }
                    }
                }

                if (_wallRects.Count == 0)
                    _nextRetryTime = Time.unscaledTime + RETRY_SECONDS;

                MelonLogger.Msg($"AutoEvade: built wall cache with {_wallRects.Count} rects (scene='{_sceneNameBuilt}', mono:{hitsFromMono}, asset:{hitsFromAsset}).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"AutoEvade: wall cache build failed (safe-ignored): {e.Message}");
            }
        }

        // Segment-vs-rect intersection (AABB). Returns true if [p0,p1] crosses rect.
        public static bool SegmentIntersectsRect(Vector2 p0, Vector2 p1, Rect rect)
        {
            Vector2 d = p1 - p0;
            float tmin = 0f, tmax = 1f;

            if (Mathf.Abs(d.x) < 1e-6f)
            {
                if (p0.x < rect.xMin || p0.x > rect.xMax) return false;
            }
            else
            {
                float invDx = 1f / d.x;
                float t1 = (rect.xMin - p0.x) * invDx;
                float t2 = (rect.xMax - p0.x) * invDx;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = Mathf.Max(tmin, t1);
                tmax = Mathf.Min(tmax, t2);
                if (tmin > tmax) return false;
            }

            if (Mathf.Abs(d.y) < 1e-6f)
            {
                if (p0.y < rect.yMin || p0.y > rect.yMax) return false;
            }
            else
            {
                float invDy = 1f / d.y;
                float t1 = (rect.yMin - p0.y) * invDy;
                float t2 = (rect.yMax - p0.y) * invDy;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = Mathf.Max(tmin, t1);
                tmax = Mathf.Min(tmax, t2);
                if (tmin > tmax) return false;
            }

            return true; // overlap exists within [0,1]
        }
    }

    internal static class ThreatTracker
    {
        private static readonly SysGen.Dictionary<int, Vector2> _lastPos = new SysGen.Dictionary<int, Vector2>();

        public static Vector2 EstimateVelocity(EnemyController e, Vector2 currentPos, float dt)
        {
            if (dt <= 0f) return Vector2.zero;

            try { return e.Velocity; } catch { }

            int id = e.GetInstanceID();
            if (_lastPos.TryGetValue(id, out var prev))
                return (currentPos - prev) / dt;

            return Vector2.zero;
        }

        public static void UpdateLastPositions(SysGen.IEnumerable<EnemyController> enemies)
        {
            if (enemies == null) return;
            foreach (var e in enemies)
            {
                if (e == null) continue;
                var p3 = e._cachedTransform?.position ?? Vector3.zero;
                _lastPos[e.GetInstanceID()] = new Vector2(p3.x, p3.y);
            }
        }
    }

    [HarmonyPatch]
    public static class CharacterController_Tick_Patch
    {
        private static MethodBase _target;
        private static bool _loggedAttach;

        static MethodBase TargetMethod()
        {
            var t = typeof(VSCharController);
            _target = AccessTools.Method(t, "OnUpdate") ?? AccessTools.Method(t, "Update");
            return _target;
        }

        private const int DIR_SAMPLES = 16;
        private const float EPS = 1e-4f;
        private const float WALL_LOOKAHEAD_FACTOR = 0.10f;
        private const float MIN_LOOKAHEAD = 0.75f;

        [HarmonyPrefix]
        public static void Prefix(VSCharController __instance)
        {
            if (!_loggedAttach && __instance != null)
            {
                _loggedAttach = true;
                MelonLogger.Msg($"AutoEvade: patch attached to {__instance.GetType().Name}.{_target?.Name ?? "??"} (Prefix/Postfix).");
            }

            if (!SceneUtil.IsGameplayScene() || __instance == null || __instance.IsDead)
                return;
			
            WallCache.Pump();

            // --------- SENSE ----------
            Vector2 playerPos = __instance.CachedTransform != null ? (Vector2)__instance.CachedTransform.position : Vector2.zero;

            float playerSpeed = 3f;
            try { playerSpeed = __instance.PMoveSpeed(); } catch { }

            float playerSizeMetric = 1f;

            var wallRects = WallCache.WallRects;

            EnemyController[] threats = null;
            try { threats = UnityEngine.Object.FindObjectsOfType<EnemyController>(); } catch { }
            if (threats == null) threats = Array.Empty<EnemyController>();

            // --------- THINK ----------
            float lookahead = Mathf.Max(MIN_LOOKAHEAD, playerSpeed * WALL_LOOKAHEAD_FACTOR);
            Vector2 bestDir = Vector2.zero;
            float bestScore = -1f;
            float dt = Time.deltaTime <= 0f ? 0.0167f : Time.deltaTime;


            playerSizeMetric = Mathf.Max(playerSizeMetric, 0.75f);

            for (int i = 0; i < DIR_SAMPLES; i++)
            {
                float ang = (i / (float)DIR_SAMPLES) * Mathf.PI * 2f;
                Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                if (dir.sqrMagnitude <= EPS) continue;

                // Walls
                Vector2 p0 = playerPos;
                Vector2 p1 = playerPos + dir.normalized * lookahead;

                bool blocked = false;
                for (int r = 0; r < wallRects.Count; r++)
                {
                    if (WallCache.SegmentIntersectsRect(p0, p1, wallRects[r]))
                    {
                        blocked = true;
                        break;
                    }
                }
                if (blocked) continue;

                // Threats
                float minT = float.PositiveInfinity;

                foreach (var th in threats)
                {
                    if (th == null || th.IsDead) continue;

                    Vector2 tPos = th._cachedTransform != null ? (Vector2)th._cachedTransform.position : Vector2.zero;

                    Vector2 vP = dir * playerSpeed;
                    Vector2 vT = ThreatTracker.EstimateVelocity(th, tPos, dt);

                    float dangerR = playerSizeMetric * 1.25f;
                    try
                    {
                        if (!th.IsBullet())
                            dangerR = Mathf.Max(playerSizeMetric * 1.75f, playerSizeMetric + 0.5f);
                    }
                    catch { }

                    float tHit = CalculateIntersectionTime(playerPos, vP, tPos, vT, dangerR);
                    if (tHit >= 0f && tHit < minT) minT = tHit;
                    if (minT <= 0f) break;
                }

                float score = float.IsInfinity(minT) ? float.MaxValue : minT;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestDir = dir;
                }
            }

            if (bestDir.sqrMagnitude <= EPS)
                bestDir = Vector2.right; // visible fallback so we can confirm movement

			DebugDrawState.Update(playerPos, bestDir, playerSpeed, lookahead);

            if (AutoEvadeState.Enabled)
            {
                TrySetMovement(__instance, bestDir);
            }

            ThreatTracker.UpdateLastPositions(threats);
        }

        [HarmonyPostfix]
        public static void Postfix(VSCharController __instance)
        {
            if (!SceneUtil.IsGameplayScene() || __instance == null) return;
        }

        private static void TrySetMovement(VSCharController inst, Vector2 dir)
        {
            try { inst.CurrentDirection = dir; } catch { }
            try { inst.LastFacingDirection = dir; } catch { }
            try { inst._currentDirection = dir; } catch { }
            try { inst._currentDirectionRaw = dir; } catch { }
            try { inst._lastMovementDirection = dir; } catch { }
        }

        private static float CalculateIntersectionTime(Vector2 p, Vector2 vP, Vector2 q, Vector2 vQ, float R)
        {
            Vector2 r = p - q;
            Vector2 v = vP - vQ;

            float a = Vector2.Dot(v, v);
            float b = 2f * Vector2.Dot(r, v);
            float c = Vector2.Dot(r, r) - R * R;

            if (a < 1e-8f)
            {
                if (c <= 0f) return 0f;      // currently intersecting
                return float.PositiveInfinity;
            }

            float disc = b * b - 4f * a * c;
            if (disc < 0f) return float.PositiveInfinity;

            float sqrt = Mathf.Sqrt(disc);
            float t1 = (-b - sqrt) / (2f * a);
            float t2 = (-b + sqrt) / (2f * a);

            if (t1 >= 0f) return t1;
            if (t2 >= 0f) return t2;
            return float.PositiveInfinity;
        }
    }

    [HarmonyPatch(typeof(VSCharController), "HandlePlayerInput")]
    public static class CharacterController_HandlePlayerInput_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(VSCharController __instance)
        {
            // Let the game do its thing unless we are in a live gameplay scene and autopilot is ON.
            if (!SceneUtil.IsGameplayScene() || __instance == null || __instance.IsDead)
                return true;

            if (!AutoEvadeState.Enabled)
                return true;

            // Reinforce our direction (computed in the OnUpdate Prefix).
            var dir = Vector2.zero;
            try { dir = __instance.CurrentDirection; } catch { }

            // If we didn't manage to read it (or it’s zero), fall back to the last planned dir from overlay.
            if (dir.sqrMagnitude < 1e-6f)
                dir = DebugDrawState.BestDir.sqrMagnitude > 1e-6f ? DebugDrawState.BestDir : Vector2.right;

            try { __instance.CurrentDirection       = dir; } catch { }
            try { __instance.LastFacingDirection    = dir; } catch { }
            try { __instance._currentDirection      = dir; } catch { }
            try { __instance._currentDirectionRaw   = dir; } catch { }
            try { __instance._lastMovementDirection = dir; } catch { }

            // IMPORTANT: return false to SKIP the original input routine
            // (this prevents the game from reading Rewired and resetting to (0,0))
            return false;
        }
    }
}
