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
    internal sealed partial class AiController : MonoBehaviour
    {
        private const float WorldRefreshIntervalSeconds = 0.2f;
        private const float DebugLogIntervalSeconds = 1f;

        private readonly AiWorldState _world = new AiWorldState();
        private readonly VelocityObstaclePlanner _planner = new VelocityObstaclePlanner();
        private readonly AiGameStateMonitor _stateMonitor = new AiGameStateMonitor();
        private readonly KitingPlanner _kitingPlanner = new KitingPlanner();
        private readonly List<WallDistanceInfo> _wallDistanceBuffer = new List<WallDistanceInfo>(8);
        private readonly List<WallDistanceInfo> _playerWallDistanceBuffer = new List<WallDistanceInfo>(4);

        private CharacterController? _player;
        private Vector2 _desiredDirection = Vector2.zero;
        private PlannerResult _lastPlan = PlannerResult.Zero;
        private KitingDirective _lastKitingDirective = KitingDirective.None;
        private float _lastWorldSyncTime;
        private float _lastDebugLogTime;
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
                _lastKitingDirective = KitingDirective.None;
                return;
            }

            EnsurePlayerReference();
            if (_player == null)
            {
                _lastKitingDirective = KitingDirective.None;
                MaybeLogDebugInfo();
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
                _lastKitingDirective = kitingDirective;
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

            MaybeLogDebugInfo();
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
        internal KitingDirective LastKitingDirective => _lastKitingDirective;
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
}
