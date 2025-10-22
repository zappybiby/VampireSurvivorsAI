using Il2CppInterop.Runtime;
using MelonLoader;
using System;
using UnityEngine;
using CharacterController = Il2CppVampireSurvivors.Objects.Characters.CharacterController;

namespace AI_Mod.Runtime
{
    internal static class AiRuntime
    {
        private static AiController? _controller;

        internal static AiController? Controller => _controller;

        internal static void Attach(AiController controller)
        {
            _controller = controller;
            MelonLogger.Msg("AI runtime attached to controller.");
        }

        internal static void Detach(AiController controller)
        {
            if (_controller == controller)
            {
                _controller = null;
                MelonLogger.Msg("AI runtime detached from controller.");
            }
        }

        internal static bool ShouldBlockInput(CharacterController subject)
        {
            return _controller != null && _controller.ShouldOverrideInputFor(subject);
        }

        internal static void Reset()
        {
            _controller = null;
        }
    }
}
