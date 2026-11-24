using System;
using UnityEngine;

namespace OSC.Module.Players
{
    public sealed class GameobjectToggleWatcher : MonoBehaviour
    {
        public Action<bool> Toggled;

        private void OnEnable() => Toggled?.Invoke(true);
        private void OnDisable() => Toggled?.Invoke(false);
    }
}
