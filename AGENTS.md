You are making an AI mod for MelonLoader for an IL2CPP unity game called "Vampire Survivors"

MelonLoader IL2CPP basics:
```
Always use Il2CppInterop for type access, casting, and registration.

Custom classes must be registered before use via [RegisterTypeInIl2Cpp] or ClassInjector.RegisterTypeInIl2Cpp<T>().

Every injected class requires an IntPtr constructor and optionally a Mono-side constructor if instantiated manually.

Classes must inherit from a non-abstract Il2CPP base (e.g., MonoBehaviour).

Use Il2CppValueField<T> for fields exposed to Il2CPP and set values through .Value.

Obtain Il2CPP types using Il2CppType.Of<T>(), not GetType().

Cast Il2CPP objects using TryCast<T>() or Cast<T>() — never direct casts.

Use MelonCoroutines.Start() and Stop() for coroutines (Mono’s StartCoroutine won’t work).

Convert strings explicitly to Il2CppSystem.String when required.

Wrap delegates with System.Action<T>, which automatically converts to Il2CppSystem.Action<T>.

Attach or remove event handlers using add_ / remove_ methods, or CombineImpl if stripped.

Do not override virtual methods — unsupported in Il2CPP interop.

Avoid exposing properties, events, or static fields to Il2CPP reflection.

Watch for TypeInitializationException errors from nested generic-enum types — refactor signatures.

Verify all registration and constructor requirements before use to prevent runtime errors.
```

AI Mod Basics
- You are creating an AI that plays the game Vampire Survivors
- It avoids enemies, enemy projectiles (bullets), and walls. It also tries to collect Gems (which are XP)
- Target Framework net6.0

Implementation Notes
- Avoid using assumed values. Always ask for clarification or leave a TODO instead.
- If using fallbacks of any kind, make a log that outputs that a fallback is being used

Melonloader Assembly
[assembly: MelonGame("poncle", "Vampire Survivors")]
