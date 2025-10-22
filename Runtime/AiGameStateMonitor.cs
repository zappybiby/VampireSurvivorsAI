using Il2CppInterop.Runtime;
using Il2CppVampireSurvivors;
using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI_Mod.Runtime
{
    internal sealed class AiGameStateMonitor
    {
        private const string GameStateMachinePath = "Core/GameStateMachine";
        private const string GameplaySceneName = "Gameplay";

        private static readonly string[] GameplayStateTokens =
        {
            "GameStatePlaying",
            "GameStateTreasure"
        };

        private static readonly string[] BlockedStateTokens =
        {
            "GameStatePaused",
            "GameStatePlayerDied",
            "GameStateGameOver",
            "GameStateQuitGame",
            "GameStateOpenTreasure",
            "GameStateLevelUp",
            "GameStateItemFound",
            "GameStateRelicFound",
            "GameStateCharacterFound",
            "GameStateOpenWeaponSelection",
            "GameStateOpenSkillSelection",
            "GameStateOpenHealer",
            "GameStateOpenDirector",
            "GameStateOpenPiano",
            "GameStateOpenShop",
            "GameStateOpenLevelBonusSelection",
            "GameStateOpenTpWeaponSelection",
            "GameStateSelectArcana",
            "GameStateRevive",
            "GameStateRecap",
            "GameStateDirectToRecap",
            "GameStatePlayFinalCredits",
            "GameStateShowGameoverino",
            "GameStateShowFinalFireworks",
            "GameStateReturnToLanding"
        };

        private static readonly string[] NeutralStateTokens =
        {
            "GameStateInitializing",
            "GameStateInitialize",
            "GameStateReady",
            "GameStateFinalize",
            "GameStateReturnToMenu",
            "GameStateReturnToGame",
            "GameStateInitializeGame"
        };

        private Scene _currentScene;
        private GameStateMachine? _machineComponent;

        private bool _loggedMissingStateMachine;
        private bool _loggedMissingComponent;
        private bool _loggedStateNameFailure;

        private string? _currentStateName;
        private bool _gameplayActive;
        private readonly HashSet<string> _unknownStatesLogged = new HashSet<string>(StringComparer.Ordinal);

        internal bool IsGameplayScene => _currentScene.name.Equals(GameplaySceneName, StringComparison.Ordinal);
        internal bool IsGameplayActive => IsGameplayScene && _gameplayActive;
        internal string? CurrentStateName => _currentStateName;

        internal void OnSceneChanged(Scene scene)
        {
            _currentScene = scene;

            if (!IsGameplayScene)
            {
                _machineComponent = null;
                _loggedMissingStateMachine = false;
                _loggedMissingComponent = false;
                _loggedStateNameFailure = false;
                _currentStateName = null;
                _gameplayActive = false;
                _unknownStatesLogged.Clear();
            }
        }

        internal void Refresh()
        {
            if (!IsGameplayScene)
            {
                return;
            }

            EnsureStateMachine();
            UpdateStateSnapshot();
        }

        private void EnsureStateMachine()
        {
            if (_machineComponent != null)
            {
                return;
            }

            var machineObject = GameObject.Find(GameStateMachinePath);
            if (machineObject == null)
            {
                if (!_loggedMissingStateMachine)
                {
                    MelonLogger.Warning($"Game state machine '{GameStateMachinePath}' not found. AI will stay idle until it appears.");
                    _loggedMissingStateMachine = true;
                }

                return;
            }

            var component = machineObject.GetComponent(Il2CppType.Of<GameStateMachine>());
            if (component == null)
            {
                if (!_loggedMissingComponent)
                {
                    MelonLogger.Warning("GameStateMachine component missing on located state machine object. TODO: validate object path.");
                    _loggedMissingComponent = true;
                }
                _machineComponent = null;
                return;
            }

            var machine = component.TryCast<GameStateMachine>();
            if (machine == null)
            {
                if (!_loggedMissingComponent)
                {
                    MelonLogger.Warning("Failed to cast GameStateMachine component from IL2CPP object. TODO: inspect state machine binding.");
                    _loggedMissingComponent = true;
                }
                _machineComponent = null;
                return;
            }

            _machineComponent = machine;
            _loggedMissingStateMachine = false;
            _loggedMissingComponent = false;
            MelonLogger.Msg("Game state monitor attached to GameStateMachine.");
        }

        private void UpdateStateSnapshot()
        {
            if (_machineComponent == null || _machineComponent.gameObject == null)
            {
                _gameplayActive = false;
                _currentStateName = null;
                return;
            }

            string? stateName = null;
            try
            {
                stateName = _machineComponent.CurrentStateName;
            }
            catch (Exception ex)
            {
                if (!_loggedStateNameFailure)
                {
                    MelonLogger.Warning($"Failed to read GameStateMachine.CurrentStateName: {ex.Message}. TODO: inspect state access.");
                    _loggedStateNameFailure = true;
                }
            }

            if (!string.Equals(_currentStateName, stateName, StringComparison.Ordinal))
            {
                var previous = _currentStateName ?? "<none>";
                var next = stateName ?? "<unknown>";
                MelonLogger.Msg($"Game state changed: {previous} -> {next}");
                _currentStateName = stateName;
            }

            _gameplayActive = DetermineGameplayActive(stateName);
        }

        private bool DetermineGameplayActive(string? stateName)
        {
            if (string.IsNullOrEmpty(stateName))
            {
                return false;
            }

            if (MatchesToken(stateName, GameplayStateTokens))
            {
                return true;
            }

            if (MatchesToken(stateName, BlockedStateTokens))
            {
                return false;
            }

            if (MatchesToken(stateName, NeutralStateTokens) || stateName.Equals("No Active State", StringComparison.Ordinal))
            {
                return false;
            }

            if (_unknownStatesLogged.Add(stateName))
            {
                MelonLogger.Warning($"Encountered unclassified game state '{stateName}'. TODO: revisit gameplay classification.");
            }

            return false;
        }

        private static bool MatchesToken(string stateName, IReadOnlyList<string> tokens)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }

                if (stateName.Equals(token, StringComparison.Ordinal) ||
                    stateName.EndsWith(token, StringComparison.Ordinal) ||
                    stateName.IndexOf(token, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
