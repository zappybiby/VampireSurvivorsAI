using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: MelonInfo(typeof(AI_Mod.AiMod), "AI Mod", "0.1.0", "Codex Agent")]
[assembly: MelonGame("poncle", "Vampire Survivors")]

namespace AI_Mod
{
    public class AiMod : MelonMod
    {
        private static HarmonyLib.Harmony? _harmony;
        private static bool _il2CppTypesRegistered;

        // Use System.Action here
        private static Action<Scene, LoadSceneMode>? _sceneLoadedHandler;

        public override void OnInitializeMelon()
        {
            RegisterIl2CppTypes();
            _harmony = new HarmonyLib.Harmony("AI_Mod.Patches");
            _harmony.PatchAll();

            // Wire up SceneManager with System.Action
            _sceneLoadedHandler = new Action<Scene, LoadSceneMode>(OnSceneLoaded);
            SceneManager.add_sceneLoaded(_sceneLoadedHandler);

            Runtime.AiBootstrapper.EnsurePersistentController();
            MelonLogger.Msg("AI mod initialized.");
        }

        public override void OnDeinitializeMelon()
        {
            if (_sceneLoadedHandler != null)
            {
                SceneManager.remove_sceneLoaded(_sceneLoadedHandler);
                _sceneLoadedHandler = null;
            }

            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
                _harmony = null;
            }

            Runtime.AiBootstrapper.Cleanup();
            base.OnDeinitializeMelon();
        }

        private static void RegisterIl2CppTypes()
        {
            if (_il2CppTypesRegistered) return;
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<Runtime.AiController>();
                ClassInjector.RegisterTypeInIl2Cpp<Runtime.AiDebugOverlay>();
                _il2CppTypesRegistered = true;
                MelonLogger.Msg("Registered IL2CPP types.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Failed to register IL2CPP types: {ex}");
                throw;
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Runtime.AiBootstrapper.NotifySceneLoaded(scene);
        }
    }
}
