using System;
using UnityEngine;

namespace OSC.Module.Vehicles.Controls
{
    /// <summary>
    /// Стабилизатор параметров PhysX 3.3 для Unity 5.0.0f4.
    /// Уменьшает контактный зазор (0.003f), повышает точность солвера (solverIterationCount = 18)
    /// и включает Interpolate строго для активных (не кинематических и не спящих) тел.
    /// </summary>
    internal sealed class PhysicsOptimizerService
    {
        private bool applied;
        private float originalContactOffset = 0.01f;
        private float originalSleepThreshold = 0.015f;

        public bool IsEnabled { get; private set; }

        public void ApplyOnLevelLoaded()
        {
            if (!applied)
            {
                originalContactOffset = Physics.defaultContactOffset;
                originalSleepThreshold = Physics.sleepThreshold;
                applied = true;
            }

            Physics.defaultContactOffset = 0.003f;
            Physics.sleepThreshold = 0.010f;
            IsEnabled = true;

            Rigidbody[] allRbs = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
            if (allRbs == null) return;

            for (int i = 0; i < allRbs.Length; i++)
            {
                Rigidbody rb = allRbs[i];
                if (rb == null || rb.isKinematic) continue;

                // Не будим спящие тела: solverIterationCount безопасен, а interpolation трогаем только у бодрствующих
                rb.solverIterationCount = 18;
                if (!rb.IsSleeping() && rb.interpolation == RigidbodyInterpolation.None)
                {
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                }
            }
        }

        public void RestoreVanilla()
        {
            if (!applied) return;
            Physics.defaultContactOffset = originalContactOffset;
            Physics.sleepThreshold = originalSleepThreshold;
            IsEnabled = false;
        }
    }
}
