using System.Collections.Generic;
using MelonLoader;

namespace AI_Mod.Runtime
{
    internal sealed class FallbackLogger
    {
        private readonly HashSet<string> _warned = new HashSet<string>();
        private readonly HashSet<string> _info = new HashSet<string>();

        internal void ResetTransient()
        {
            _info.Clear();
        }

        internal void WarnOnce(string key, string message)
        {
            if (_warned.Add(key))
            {
                MelonLogger.Warning(message);
            }
        }

        internal void InfoOnce(string key, string message)
        {
            if (_info.Add(key))
            {
                MelonLogger.Msg(message);
            }
        }
    }
}
