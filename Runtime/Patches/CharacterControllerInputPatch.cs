using HarmonyLib;
using CharacterController = Il2CppVampireSurvivors.Objects.Characters.CharacterController;

namespace AI_Mod.Runtime.Patches
{
    [HarmonyPatch(typeof(CharacterController), nameof(CharacterController.HandlePlayerInput))]
    internal static class CharacterControllerHandlePlayerInputPatch
    {
        private static bool Prefix(CharacterController __instance)
        {
            return !AiRuntime.ShouldBlockInput(__instance);
        }
    }
}

