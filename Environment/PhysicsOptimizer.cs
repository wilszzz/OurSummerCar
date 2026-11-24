using System;
using System.Collections.Generic;
using MSCLoader;
using UnityEngine;

namespace OurSummerCar.Environment
{
    /// <summary>
    /// Оптимизатор и стабилизатор движка PhysX 3.3 в My Summer Car.
    /// Переключает параметры физики без необходимости перезапуска игры.
    /// Повышает жесткость и точность солвера Rigidbody (джойнты, сцепки, подвеска перестают взрываться)
    /// и уменьшает контактный зазор. Не выполняет циклических опросов памяти и не будит спящие тела.
    /// </summary>
    public class PhysicsOptimizer : MonoBehaviour
    {
        public static PhysicsOptimizer Instance { get; private set; }

        public bool IsOptimized { get; private set; } = true;

        private float originalContactOffset = 0.01f;
        private float originalSleepThreshold = 0.015f;

        private void Awake()
        {
            Instance = this;
            originalContactOffset = Physics.defaultContactOffset;
            originalSleepThreshold = Physics.sleepThreshold;
        }

        private void Start()
        {
            ApplySettings(true, true);
        }

        public void SetEnabled(bool enabled)
        {
            if (IsOptimized == enabled) return;
            ApplySettings(enabled, false);
        }

        private void ApplySettings(bool enabled, bool isInitialLoad)
        {
            IsOptimized = enabled;

            try
            {
                if (enabled)
                {
                    // Оптимальные параметры PhysX 3.3:
                    // 3 мм вместо 10 мм (предметы не парят в воздухе над столом/полом)
                    Physics.defaultContactOffset = 0.003f;
                    Physics.sleepThreshold = 0.010f;
                }
                else
                {
                    // Возврат к ванильным значениям MSC
                    Physics.defaultContactOffset = originalContactOffset;
                    Physics.sleepThreshold = originalSleepThreshold;
                }

                // Сканирование сцены допустимо ТОЛЬКО 1 раз при загрузке мира (в Start)
                // либо при ручном переключении кнопки в F10 пользователем.
                ScanAndApplyRigidbodies(enabled, isInitialLoad);

                ModConsole.Print($"<color=cyan>[OurSummerCar]</color> Оптимизация PhysX 3.3: {(enabled ? "<color=lime>ВКЛЮЧЕНА (Solver=18, Offset=3mm)</color>" : "<color=yellow>ОТКЛЮЧЕНА (Ваниль MSC)</color>")}");
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[PhysicsOptimizer Error] {ex.Message}");
            }
        }

        /// <summary>
        /// Точечная оптимизация конкретного Rigidbody в момент спавна или взятия в руки.
        /// Не нагружает CPU, работает по событиям.
        /// </summary>
        public void OptimizeRigidbody(Rigidbody rb, bool isDynamicAction = false)
        {
            if (rb == null) return;

            try
            {
                if (IsOptimized)
                {
                    // Увеличиваем точность солвера для жесткости соединений
                    rb.solverIterationCount = 18;

                    // Интерполяция только если объект не статический и не кинематический
                    if (!rb.isKinematic && rb.interpolation == RigidbodyInterpolation.None)
                    {
                        rb.interpolation = RigidbodyInterpolation.Interpolate;
                    }

                    // Continuous (CCD) включается ТОЛЬКО точечно при броске/падении динамического предмета,
                    // и никогда не вешается на неподвижные лежащие предметы на полках
                    if (isDynamicAction && !rb.isKinematic && rb.mass < 50f)
                    {
                        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
                    }
                }
                else
                {
                    rb.solverIterationCount = 6;
                    rb.interpolation = RigidbodyInterpolation.None;
                    if (rb.collisionDetectionMode == CollisionDetectionMode.Continuous)
                    {
                        rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Однократная настройка тел сцены при загрузке мира.
        /// КРИТИЧЕСКИ ВАЖНО: не будит спящие тела (IsSleeping) и не трогает кинематические (isKinematic)!
        /// </summary>
        private void ScanAndApplyRigidbodies(bool enabled, bool isInitialLoad)
        {
            try
            {
                var allRbs = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
                for (int i = 0; i < allRbs.Length; i++)
                {
                    var rb = allRbs[i];
                    if (rb == null) continue;

                    // Кинематические тела физикой не обсчитываются — пропускаем
                    if (rb.isKinematic) continue;

                    // Не будим спящие тела изменением параметров движения/коллизий!
                    bool isSleeping = rb.IsSleeping();

                    if (enabled)
                    {
                        // 18 итераций солвера — не меняет состояние покоя (не вызывает WakeUp)
                        rb.solverIterationCount = 18;

                        // Если тело спит, НЕ ТРОГАЕМ interpolation и collisionDetectionMode,
                        // иначе PhysX разбудит тело и создаст взрывной лаг в кадре
                        if (!isSleeping)
                        {
                            if (rb.interpolation == RigidbodyInterpolation.None)
                            {
                                rb.interpolation = RigidbodyInterpolation.Interpolate;
                            }
                        }
                    }
                    else
                    {
                        rb.solverIterationCount = 6;
                        if (!isSleeping)
                        {
                            rb.interpolation = RigidbodyInterpolation.None;
                            if (rb.collisionDetectionMode == CollisionDetectionMode.Continuous)
                            {
                                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public void Reset()
        {
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
