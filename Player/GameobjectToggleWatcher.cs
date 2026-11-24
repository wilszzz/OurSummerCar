using System;
using UnityEngine;

namespace OurSummerCar.Player
{
    /// <summary>
    /// Легковесный наблюдатель переключения видимости игровых объектов MSC (модель WreckMP).
    /// Срабатывает мгновенно при активации/деактивации жестов камеры FPSCamera без периодического опроса.
    /// </summary>
    public class GameobjectToggleWatcher : MonoBehaviour
    {
        public Action<bool> Toggled;

        private void OnEnable()
        {
            Toggled?.Invoke(true);
        }

        private void OnDisable()
        {
            Toggled?.Invoke(false);
        }
    }
}
