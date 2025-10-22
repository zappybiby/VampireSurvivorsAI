using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AI_Mod.Runtime
{
    internal static class AiBootstrapper
    {
        private static GameObject? _root;
        private static AiController? _controller;
        private static AiDebugOverlay? _overlay;
        private static Scene? _lastScene;

        internal static void EnsurePersistentController()
        {
            if (_root != null)
            {
                return;
            }

            _root = new GameObject("AI_Controller_Root");
            Object.DontDestroyOnLoad(_root);
            _controller = _root.AddComponent<AiController>();
            _overlay = _root.AddComponent<AiDebugOverlay>();
            if (_lastScene.HasValue && _controller != null)
            {
                _controller.HandleSceneChanged(_lastScene.Value);
            }
            MelonLogger.Msg("AI Controller instantiated.");
        }

        internal static void NotifySceneLoaded(Scene scene)
        {
            _lastScene = scene;
            if (_controller == null)
            {
                MelonLogger.Msg($"Scene {scene.name} loaded before controller existed, ensuring controller now.");
                EnsurePersistentController();
            }

            _controller?.HandleSceneChanged(scene);
        }

        internal static void Cleanup()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
            }

            _controller = null;
            _overlay = null;
            _lastScene = null;
            AiRuntime.Reset();
            MelonLogger.Msg("AI bootstrapper cleaned up.");
        }
    }
}
