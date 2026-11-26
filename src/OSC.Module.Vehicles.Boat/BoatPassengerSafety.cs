using System.Collections.Generic;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using UnityEngine;

namespace OSC.Module.Vehicles.Boat
{
    /// <summary>
    /// R5: Безопасная обработка выхода водителя лодки или глушения мотора,
    /// пока другой игрок сидит пассажиром, с единой синхронизацией BOAT и BOAT/GFX.
    /// </summary>
    internal sealed class BoatPassengerSafety
    {
        private readonly OscHostContext ctx;
        private readonly string moduleId;
        private readonly BoatMotorSync motorSync;

        private bool localIsDriver;
        private bool localIsPassenger;
        private int remoteDriverId = -1;
        private readonly HashSet<int> remotePassengerIds = new HashSet<int>();

        private bool driverExitedWithPassengerActive;
        private bool shutOffGlideActive;
        private float restTimer;
        private bool authorityOverridePublished;
        private int activeAuthorityId = -1;
        private bool activeForceKinematic;
        private Vector3 lastObservedBoatPos;
        private bool hasObservedBoatPos;
        private float kinematicEstimatedSpeed;

        public bool IsLocalDriver => localIsDriver;
        public bool IsLocalSeatedOnBoat => localIsDriver || localIsPassenger;
        public bool IsRemoteDriver => remoteDriverId >= 0 && !localIsDriver;
        public bool HasAnyPassenger => localIsPassenger || remotePassengerIds.Count > 0;
        public bool IsLocalPhysicsOwner
        {
            get
            {
                if (localIsDriver) return true;
                if (remoteDriverId >= 0) return false;
                return ctx?.Transport != null && ctx.Transport.IsServer;
            }
        }

        public BoatPassengerSafety(OscHostContext context, string ownerModuleId, BoatMotorSync motorSync)
        {
            ctx = context;
            moduleId = ownerModuleId;
            this.motorSync = motorSync;
            this.motorSync.OnEngineShutOff += HandleEngineShutOff;
        }

        public void Reset()
        {
            if (authorityOverridePublished)
            {
                PublishAuthority(overrideActive: false, authorityPlayerId: 0, forceKinematic: false, forceDynamic: false);
            }

            localIsDriver = false;
            localIsPassenger = false;
            remoteDriverId = -1;
            remotePassengerIds.Clear();
            driverExitedWithPassengerActive = false;
            shutOffGlideActive = false;
            restTimer = 0f;
            authorityOverridePublished = false;
            activeAuthorityId = -1;
            activeForceKinematic = false;
            lastObservedBoatPos = Vector3.zero;
            hasObservedBoatPos = false;
            kinematicEstimatedSpeed = 0f;
        }

        public void OnLocalOccupancyChanged(LocalPlayerOccupancyEvent evt)
        {
            if (!motorSync.IsBoatEvent(evt.VehicleId, evt.VehicleTransform)) return;

            if (evt.IsSeated)
            {
                if (evt.IsDriver)
                {
                    localIsDriver = true;
                    localIsPassenger = false;
                    remoteDriverId = -1;
                    driverExitedWithPassengerActive = false;
                    shutOffGlideActive = false;
                    restTimer = 0f;
                    ReleaseOverrideIfActive();
                }
                else
                {
                    bool wasDriver = localIsDriver;
                    localIsPassenger = true;
                    localIsDriver = false;
                    if (wasDriver)
                    {
                        TriggerDriverExitWithPassengerSafety(wasLocalDriver: true);
                    }
                    else if (remoteDriverId < 0)
                    {
                        ApplyPassengerContinuityAuthority();
                    }
                }
            }
            else
            {
                bool wasDriver = localIsDriver;
                localIsDriver = false;
                localIsPassenger = false;

                if (wasDriver && HasAnyPassenger)
                {
                    TriggerDriverExitWithPassengerSafety(wasLocalDriver: true);
                }
                else if (HasAnyPassenger)
                {
                    if (remoteDriverId < 0)
                    {
                        ApplyPassengerContinuityAuthority();
                    }
                }
                else
                {
                    driverExitedWithPassengerActive = false;
                    shutOffGlideActive = false;
                    ReleaseOverrideIfActive();
                }
            }
        }

        public void OnRemoteSeatChanged(RemotePlayerSeatEvent evt)
        {
            if (!motorSync.IsBoatEvent(evt.VehicleId, evt.VehicleTransform)) return;

            if (evt.IsSeated)
            {
                if (evt.IsDriver)
                {
                    remoteDriverId = evt.PlayerId;
                    remotePassengerIds.Remove(evt.PlayerId);
                    driverExitedWithPassengerActive = false;
                    shutOffGlideActive = false;
                    restTimer = 0f;
                    ReleaseOverrideIfActive();
                }
                else
                {
                    bool wasRemoteDriver = remoteDriverId == evt.PlayerId;
                    remotePassengerIds.Add(evt.PlayerId);
                    if (wasRemoteDriver)
                    {
                        remoteDriverId = -1;
                        TriggerDriverExitWithPassengerSafety(wasLocalDriver: false);
                    }
                    else if (remoteDriverId < 0 && localIsPassenger)
                    {
                        ApplyPassengerContinuityAuthority();
                    }
                }
            }
            else
            {
                bool wasRemoteDriver = remoteDriverId == evt.PlayerId;
                if (wasRemoteDriver) remoteDriverId = -1;
                remotePassengerIds.Remove(evt.PlayerId);

                if (wasRemoteDriver && HasAnyPassenger)
                {
                    TriggerDriverExitWithPassengerSafety(wasLocalDriver: false);
                }
                else if (!HasAnyPassenger)
                {
                    driverExitedWithPassengerActive = false;
                    shutOffGlideActive = false;
                    ReleaseOverrideIfActive();
                }
            }
        }

        public void OnRemoteDisconnected(int playerId)
        {
            bool wasHost = playerId == 0;
            bool wasRemoteDriver = remoteDriverId == playerId;

            if (wasRemoteDriver) remoteDriverId = -1;
            remotePassengerIds.Remove(playerId);

            if (wasHost && ctx?.Transport != null && ctx.Transport.IsClient)
            {
                // При потере связи с Хостом немедленно снимаем блокировку авторитета, чтобы клиент не застрял в кинематике
                driverExitedWithPassengerActive = false;
                shutOffGlideActive = false;
                ReleaseOverrideIfActive();
                return;
            }

            if (wasRemoteDriver && HasAnyPassenger)
            {
                TriggerDriverExitWithPassengerSafety(wasLocalDriver: false);
            }
            else if (!HasAnyPassenger)
            {
                ReleaseOverrideIfActive();
            }
        }

        private void HandleEngineShutOff()
        {
            if (motorSync.BoatTransform == null) return;
            if (!HasAnyPassenger) return;

            float speed = GetEffectiveBoatSpeed();
            if (speed > 0.25f)
            {
                shutOffGlideActive = true;
                restTimer = 0f;
                ctx?.Log?.Invoke("[Boat/R5] Мотор заглушен на ходу с пассажиром — удерживается плавное скольжение до остановки");
            }
        }

        private void TriggerDriverExitWithPassengerSafety(bool wasLocalDriver)
        {
            driverExitedWithPassengerActive = true;
            restTimer = 0f;

            // Нейтрализуем убегающий газ/передачу, если водитель вышел, а пассажир остался в лодке
            if (ctx?.Transport == null || ctx.Transport.IsServer || wasLocalDriver)
            {
                if (motorSync.CurrentGear != (byte)BoatGearState.Neutral || motorSync.CurrentThrottle > 0.05f)
                {
                    motorSync.NeutralizeUnmannedThrottleAndGear();
                }
            }

            ApplyPassengerContinuityAuthority();
            ctx?.Log?.Invoke("[Boat/R5] Водитель покинул лодку с пассажиром — включено непрерывное удержание авторитета без кинематического рывка");
        }

        public void TickUpdate()
        {
            UpdateObservedBoatSpeed();
            SynchronizeDualRigidbodies();

            if (!driverExitedWithPassengerActive && !shutOffGlideActive)
            {
                if (HasAnyPassenger && !localIsDriver && remoteDriverId < 0)
                {
                    ApplyPassengerContinuityAuthority();
                }
                else if (!HasAnyPassenger && authorityOverridePublished)
                {
                    ReleaseOverrideIfActive();
                }
                return;
            }

            if (!HasAnyPassenger)
            {
                driverExitedWithPassengerActive = false;
                shutOffGlideActive = false;
                ReleaseOverrideIfActive();
                return;
            }

            ApplyPassengerContinuityAuthority();

            bool isStopped = IsBoatAtRest();
            bool hasDriver = localIsDriver || remoteDriverId >= 0;
            bool engineActive = motorSync.IsEngineRunning && motorSync.CurrentGear != (byte)BoatGearState.Neutral;

            // Пока пассажир сидит в лодке без локального водителя, удерживаем непрерывный авторитет (исключаем 0.65с поп при 1Гц heartbeat Хоста)
            if (isStopped && !engineActive && !HasAnyPassenger && (!shutOffGlideActive || !hasDriver))
            {
                restTimer += Time.deltaTime;
                if (restTimer >= 0.85f)
                {
                    driverExitedWithPassengerActive = false;
                    shutOffGlideActive = false;
                    ReleaseOverrideIfActive();
                    ctx?.Log?.Invoke("[Boat/R5] Лодка остановилась — авторитет физики возвращён в штатный режим");
                }
            }
            else
            {
                restTimer = 0f;
            }
        }

        private void SynchronizeDualRigidbodies()
        {
            Rigidbody boatRb = motorSync.BoatRigidbody;
            Rigidbody gfxRb = motorSync.GfxRigidbody;
            if (boatRb == null || gfxRb == null || gfxRb == boatRb) return;

            // Гарантируем, что BOAT и BOAT/GFX всегда имеют согласованное кинематическое состояние
            if (boatRb.isKinematic)
            {
                if (!gfxRb.isKinematic)
                {
                    gfxRb.velocity = Vector3.zero;
                    gfxRb.angularVelocity = Vector3.zero;
                    gfxRb.isKinematic = true;
                    gfxRb.useGravity = false;
                }
            }
        }

        private void UpdateObservedBoatSpeed()
        {
            Transform bt = motorSync.BoatTransform;
            if (bt == null)
            {
                hasObservedBoatPos = false;
                kinematicEstimatedSpeed = 0f;
                return;
            }

            if (!hasObservedBoatPos)
            {
                lastObservedBoatPos = bt.position;
                hasObservedBoatPos = true;
                kinematicEstimatedSpeed = 0f;
                return;
            }

            float dt = Time.deltaTime;
            if (dt > 0.0001f)
            {
                float inst = Vector3.Distance(bt.position, lastObservedBoatPos) / dt;
                kinematicEstimatedSpeed = Mathf.Lerp(kinematicEstimatedSpeed, inst, Mathf.Clamp01(dt * 10f));
                lastObservedBoatPos = bt.position;
            }
        }

        private float GetEffectiveBoatSpeed()
        {
            Rigidbody rb = motorSync.BoatRigidbody;
            if (rb != null && !rb.isKinematic)
            {
                return rb.velocity.magnitude;
            }
            return kinematicEstimatedSpeed;
        }

        private bool IsBoatAtRest()
        {
            Rigidbody rb = motorSync.BoatRigidbody;
            if (rb == null) return true;
            if (rb.isKinematic)
            {
                return kinematicEstimatedSpeed < 0.15f;
            }
            return rb.velocity.sqrMagnitude < 0.0225f && rb.angularVelocity.sqrMagnitude < 0.01f; // < 0.15 m/s
        }

        private void ApplyPassengerContinuityAuthority()
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;

            int authorityId;
            if (localIsDriver) authorityId = ctx.Transport.LocalPlayerId;
            else if (remoteDriverId >= 0) authorityId = remoteDriverId;
            else authorityId = 0; // Беспилотная лодка с пассажиром: авторитет у Хоста (0), клиент остаётся плавно кинематическим

            int localId = ctx.Transport.LocalPlayerId;
            bool isLocalAuth = localId == authorityId;
            bool forceKin = !isLocalAuth;
            bool forceDyn = isLocalAuth;

            if (!authorityOverridePublished || activeAuthorityId != authorityId || activeForceKinematic != forceKin)
            {
                PublishAuthority(overrideActive: true, authorityPlayerId: authorityId, forceKinematic: forceKin, forceDynamic: forceDyn);
            }
        }

        private void ReleaseOverrideIfActive()
        {
            if (!authorityOverridePublished) return;
            PublishAuthority(overrideActive: false, authorityPlayerId: 0, forceKinematic: false, forceDynamic: false);
        }

        private void PublishAuthority(bool overrideActive, int authorityPlayerId, bool forceKinematic, bool forceDynamic)
        {
            if (ctx?.Events == null || motorSync.BoatTransform == null) return;

            authorityOverridePublished = overrideActive;
            activeAuthorityId = authorityPlayerId;
            activeForceKinematic = forceKinematic;

            ctx.Events.Publish(new VehiclePhysicsAuthorityEvent
            {
                VehicleId = BoatConstants.BoatVehicleId,
                VehicleTransform = motorSync.BoatTransform,
                SourceModuleId = moduleId,
                OverrideActive = overrideActive,
                AuthorityPlayerId = authorityPlayerId,
                ForceKinematic = forceKinematic,
                ForceDynamic = forceDynamic,
                SuppressWorldTransformSync = false
            });
        }
    }
}
