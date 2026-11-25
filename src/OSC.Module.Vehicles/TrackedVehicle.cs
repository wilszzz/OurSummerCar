using System;
using System.Collections.Generic;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Vehicles
{
    public sealed class ManagedRigidbodyEntry
    {
        public Rigidbody Rb;
        public bool DefaultKinematic;
        public bool DefaultUseGravity;
        public RigidbodyInterpolation DefaultInterpolation;
    }

    public sealed class PassengerSeatRuntime
    {
        public TrackedVehicle Vehicle;
        public PassengerSeatSpec Spec;
        public GameObject SeatRootObject;
        public Transform HeadPivotTransform;
        public PassengerTrigger TriggerComponent;
        public bool IsOccupiedLocally;
        public int RemoteOccupantId = -1;

        public Vector3 CalibratedHeadLocalPos;
        public Vector3 CalibratedPuppetLocalOffset;
        public Quaternion CalibratedPuppetLocalRotation;
    }

    public sealed class TrackedVehicle
    {
        public byte Id => (byte)Spec.Kind;
        public VehicleKind Kind => Spec.Kind;
        public VehicleSpec Spec { get; }

        public GameObject GameObject { get; private set; }
        public Transform Transform { get; private set; }
        public Rigidbody MainRigidbody { get; private set; }
        public AxisCarController CarController { get; private set; }
        public Drivetrain Drivetrain { get; private set; }
        public SteeringWheel SteeringWheelComponent { get; private set; }

        public PlayMakerFSM DrivingFsm { get; set; }
        public Transform DriverHeadPivot { get; private set; }
        public Transform SteeringWheelTransform { get; private set; }
        public Transform SteeringForkForPuppet { get; private set; }

        public Vector3 DriverHeadLocalPos { get; private set; }
        public Vector3 DriverPuppetLocalOffset { get; private set; }
        public Quaternion DriverPuppetLocalRotation { get; private set; }

        public readonly List<ManagedRigidbodyEntry> ManagedRigidbodies = new List<ManagedRigidbodyEntry>();
        public readonly List<Collider> CachedSolidColliders = new List<Collider>();
        public readonly List<PassengerSeatRuntime> PassengerSeats = new List<PassengerSeatRuntime>();

        // Сетевой суверенитет (Ownership)
        public int OwnerPlayerId { get; set; } // 0 = Host (по умолчанию), 1 = Client
        public int DriverPlayerId { get; set; } = -1;
        public bool IsDrivenLocally { get; set; }
        public bool IsDrivenRemotely => DriverPlayerId >= 0 && !IsDrivenLocally;

        public bool IsCarriedLocally { get; set; }
        public bool IsCarriedRemotely { get; set; }
        public int CarrierPlayerId { get; set; } = -1;

        public float LocalPushTimer { get; set; }
        public float RemotePushTimer { get; set; }
        public bool IsPushedRemotely => RemotePushTimer > 0f;

        // Удалённое целевое состояние физики и органов управления
        public bool HasRemoteState { get; set; }
        public Vector3 TargetPosition { get; set; }
        public Quaternion TargetRotation { get; set; }
        public Vector3 TargetVelocity { get; set; }
        public Vector3 TargetAngularVelocity { get; set; }
        public float LastRemotePhysicsTime { get; set; }

        public float RemoteSteerAngle { get; set; }
        public float RemoteSteerInput { get; set; }
        public float RemoteThrottleInput { get; set; }
        public float RemoteBrakeInput { get; set; }
        public float RemoteHandbrakeInput { get; set; }
        public float RemoteClutchInput { get; set; }
        public int RemoteGear { get; set; }
        public float RemoteRpm { get; set; }
        public bool HasRemoteControls { get; set; }

        public float LastPhysicsSendTime { get; set; }
        public float LastControlsSendTime { get; set; }
        public bool IsCurrentlyKinematic { get; private set; }

        private Transform steeringIkHelperRoot;
        private bool steeringWheelChecked;

        public TrackedVehicle(VehicleSpec spec)
        {
            Spec = spec;
            OwnerPlayerId = 0;
            DriverHeadLocalPos = spec.FallbackDriverHeadLocalPos;
            DriverPuppetLocalRotation = Quaternion.Euler(0f, spec.DriverSeatYaw, 0f);
            DriverPuppetLocalOffset = (spec.Kind == VehicleKind.Boat)
                ? new Vector3(-0.20f, -0.42f, 1.25f)
                : ComputePuppetOffsetFromHead(DriverHeadLocalPos, DriverPuppetLocalRotation);
        }

        public bool IsBound => GameObject != null && Transform != null;

        public void Bind(GameObject go, Action<string> log)
        {
            if (go == null) return;

            GameObject = go;
            Transform = go.transform;
            MainRigidbody = go.GetComponent<Rigidbody>();
            CarController = go.GetComponent<AxisCarController>();
            Drivetrain = go.GetComponent<Drivetrain>();

            CacheManagedRigidbodies();
            RefreshSolidColliders();
            CalibrateDriverSeatAndNeutralizeDeathFsm(log);
            ResolveSteeringTransform();
        }

        private void CacheManagedRigidbodies()
        {
            ManagedRigidbodies.Clear();
            if (MainRigidbody != null)
            {
                ManagedRigidbodies.Add(new ManagedRigidbodyEntry
                {
                    Rb = MainRigidbody,
                    DefaultKinematic = MainRigidbody.isKinematic,
                    DefaultUseGravity = MainRigidbody.useGravity,
                    DefaultInterpolation = MainRigidbody.interpolation
                });
            }

            // Для лодки BOAT физический корпус GFX может иметь собственный Rigidbody
            if (Kind == VehicleKind.Boat && Transform != null)
            {
                Transform gfx = Transform.Find("GFX");
                if (gfx != null)
                {
                    Rigidbody gfxRb = gfx.GetComponent<Rigidbody>();
                    if (gfxRb != null && gfxRb != MainRigidbody)
                    {
                        ManagedRigidbodies.Add(new ManagedRigidbodyEntry
                        {
                            Rb = gfxRb,
                            DefaultKinematic = gfxRb.isKinematic,
                            DefaultUseGravity = gfxRb.useGravity,
                            DefaultInterpolation = gfxRb.interpolation
                        });
                    }
                }
            }
        }

        public void RefreshSolidColliders()
        {
            CachedSolidColliders.Clear();
            if (GameObject == null) return;

            var all = GameObject.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Collider col = all[i];
                if (col == null || col.isTrigger) continue;
                CachedSolidColliders.Add(col);
            }
        }

        private void CalibrateDriverSeatAndNeutralizeDeathFsm(Action<string> log)
        {
            if (Transform == null) return;

            DriverHeadPivot = PlayMakerBridge.FindChildRecursive(Transform, "DriverHeadPivot");
            if (DriverHeadPivot != null)
            {
                // Калибруем точную позу глаз ДО изменения суставов/физики DriverHeadPivot
                Vector3 candidateHeadLocal = Transform.InverseTransformPoint(DriverHeadPivot.position);
                if (Vector3.Distance(candidateHeadLocal, Spec.FallbackDriverHeadLocalPos) <= 1.25f)
                {
                    DriverHeadLocalPos = candidateHeadLocal;
                }
                else
                {
                    DriverHeadLocalPos = Spec.FallbackDriverHeadLocalPos;
                }

                // Нейтрализуем Death FSM и ломкий сустав головы водителя для безопасной сетевой физики
                var fsms = DriverHeadPivot.GetComponents<PlayMakerFSM>();
                for (int i = 0; i < fsms.Length; i++)
                {
                    if (fsms[i] != null)
                    {
                        fsms[i].enabled = false;
                        UnityEngine.Object.Destroy(fsms[i]);
                    }
                }

                var dhpRb = DriverHeadPivot.GetComponent<Rigidbody>();
                if (dhpRb != null)
                {
                    dhpRb.isKinematic = true;
                }

                ConfigurableJoint joint = DriverHeadPivot.GetComponent<ConfigurableJoint>()
                    ?? (DriverHeadPivot.parent != null ? DriverHeadPivot.parent.GetComponentInChildren<ConfigurableJoint>() : null);
                if (joint != null)
                {
                    joint.breakForce = float.PositiveInfinity;
                    joint.breakTorque = float.PositiveInfinity;
                }

                log?.Invoke($"[Транспорт] Калибровка DriverHeadPivot для {GameObject.name}: headLocal={DriverHeadLocalPos}");
            }
            else
            {
                DriverHeadLocalPos = Spec.FallbackDriverHeadLocalPos;
            }

            DriverPuppetLocalRotation = Quaternion.Euler(0f, Spec.DriverSeatYaw, 0f);
            DriverPuppetLocalOffset = (Kind == VehicleKind.Boat)
                ? new Vector3(-0.20f, -0.42f, 1.25f)
                : ComputePuppetOffsetFromHead(DriverHeadLocalPos, DriverPuppetLocalRotation);
        }

        public static Vector3 ComputePuppetOffsetFromHead(Vector3 headLocalPos, Quaternion seatLocalRotation)
        {
            // При UsePassengerAnimation = true в OSC.Module.Players (charTransform.y = -0.38f)
            // поза PassengerMode располагает голову на +1.21м по Y и -0.04м по Z от локального нуля RootObject
            // (так Hayosiko при высоте глаз ~1.29м даёт посадку куклы на ~0.08м на подушке кресла).
            return headLocalPos - (seatLocalRotation * new Vector3(0f, 1.21f, -0.04f));
        }

        public void ResolveSteeringTransform()
        {
            if (Transform == null || steeringWheelChecked) return;
            steeringWheelChecked = true;

            if (Kind == VehicleKind.Jonnez)
            {
                SteeringWheelTransform = Transform.Find("LOD/Suspension/Steering")
                    ?? Transform.Find("Suspension/Steering")
                    ?? PlayMakerBridge.FindChildRecursive(Transform, "Steering");
                SteeringForkForPuppet = SteeringWheelTransform;
                return;
            }

            if (!string.IsNullOrEmpty(Spec.SteeringWheelPath))
            {
                SteeringWheelTransform = Transform.Find(Spec.SteeringWheelPath);
            }

            if (SteeringWheelTransform == null && Spec.SteeringWheelNameFallbacks != null)
            {
                for (int i = 0; i < Spec.SteeringWheelNameFallbacks.Length; i++)
                {
                    SteeringWheelTransform = PlayMakerBridge.FindChildRecursive(Transform, Spec.SteeringWheelNameFallbacks[i]);
                    if (SteeringWheelTransform != null) break;
                }
            }

            if (SteeringWheelTransform == null)
            {
                var wheels = Transform.GetComponentsInChildren<SteeringWheel>(true);
                if (wheels.Length > 0 && wheels[0] != null)
                {
                    SteeringWheelTransform = wheels[0].transform;
                }
            }

            if (SteeringWheelTransform != null)
            {
                SteeringWheelComponent = SteeringWheelTransform.GetComponent<SteeringWheel>()
                    ?? SteeringWheelTransform.GetComponentInParent<SteeringWheel>();
                BuildSteeringIkHelper();
            }
        }

        private void BuildSteeringIkHelper()
        {
            if (SteeringWheelTransform == null || steeringIkHelperRoot != null) return;

            Transform existing = SteeringWheelTransform.Find("OSC_SteeringIKHelper");
            if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);

            var rootGo = new GameObject("OSC_SteeringIKHelper");
            rootGo.transform.SetParent(SteeringWheelTransform, false);
            rootGo.transform.localPosition = Vector3.zero;
            rootGo.transform.localRotation = Quaternion.identity;
            rootGo.transform.localScale = Vector3.one;
            steeringIkHelperRoot = rootGo.transform;

            var pivotGo = new GameObject("SteeringPivot");
            pivotGo.transform.SetParent(steeringIkHelperRoot, false);
            pivotGo.transform.localPosition = Vector3.zero;
            pivotGo.transform.localRotation = Quaternion.identity;
            pivotGo.transform.localScale = Vector3.one;

            // Определяем центр обода руля через активный меш (если есть) или fallback-смещение
            Vector3 rimCenterLocal = Spec.FallbackSteeringGripLocalPos;
            var renderers = SteeringWheelTransform.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].enabled)
                {
                    Vector3 boundsLocal = SteeringWheelTransform.InverseTransformPoint(renderers[i].bounds.center);
                    if (boundsLocal.sqrMagnitude < 4.0f)
                    {
                        rimCenterLocal = boundsLocal;
                        break;
                    }
                }
            }

            Vector3 leftGripLocal = rimCenterLocal + Spec.SteeringRimLocalOffsetLeft;
            Vector3 rightGripLocal = rimCenterLocal + Spec.SteeringRimLocalOffsetRight;

            // RemotePuppet.UpdateForkSteeringIK применяет фиксированные локальные оффсеты для "Column" и "Throttle".
            // Размещаем их дочерние узлы так, чтобы руки куклы ложились точно на левый и правый край обода руля.
            var colGo = new GameObject("Column");
            colGo.transform.SetParent(pivotGo.transform, false);
            Quaternion qLeftTarget = Quaternion.Euler(0f, 320f, 80f);
            colGo.transform.localRotation = Quaternion.identity * Quaternion.Inverse(qLeftTarget);
            colGo.transform.localPosition = leftGripLocal - (colGo.transform.localRotation * new Vector3(0.03f, -0.3f, 0.41f));

            var thrGo = new GameObject("Throttle");
            thrGo.transform.SetParent(pivotGo.transform, false);
            Quaternion qRightTarget = Quaternion.Euler(0f, 10f, 340f);
            thrGo.transform.localRotation = Quaternion.identity * Quaternion.Inverse(qRightTarget);
            thrGo.transform.localPosition = rightGripLocal - (thrGo.transform.localRotation * new Vector3(0.07f, 0.02f, 0f));

            SteeringForkForPuppet = steeringIkHelperRoot;
        }

        public float GetSteeringVisualAngle()
        {
            if (SteeringWheelTransform == null) return 0f;
            if (Kind == VehicleKind.Jonnez)
            {
                return SteeringWheelTransform.localEulerAngles.y;
            }
            if (SteeringWheelComponent != null && SteeringWheelComponent.rotateAroundY)
            {
                return SteeringWheelTransform.localEulerAngles.y;
            }
            return SteeringWheelTransform.localEulerAngles.z;
        }

        public void ApplyRemoteSteeringAngle(float angle, float dt)
        {
            if (SteeringWheelTransform == null) return;
            if (SteeringWheelComponent != null && IsDrivenRemotely)
            {
                // Не конфликтуем со SteeringWheel.Update на удалённой машине, если контроллер перезаписывает угол
                SteeringWheelComponent.enabled = false;
            }

            Vector3 e = SteeringWheelTransform.localEulerAngles;
            if (Kind == VehicleKind.Jonnez || (SteeringWheelComponent != null && SteeringWheelComponent.rotateAroundY))
            {
                float nextY = Mathf.LerpAngle(e.y, angle, Mathf.Clamp01(dt * 18f));
                SteeringWheelTransform.localEulerAngles = new Vector3(e.x, nextY, e.z);
            }
            else
            {
                float nextZ = Mathf.LerpAngle(e.z, angle, Mathf.Clamp01(dt * 18f));
                SteeringWheelTransform.localEulerAngles = new Vector3(e.x, e.y, nextZ);
            }
        }

        public void RestoreLocalSteeringControl()
        {
            if (SteeringWheelComponent != null && !SteeringWheelComponent.enabled)
            {
                SteeringWheelComponent.enabled = true;
            }
        }

        public void SetKinematic(bool kinematic)
        {
            IsCurrentlyKinematic = kinematic;
            for (int i = 0; i < ManagedRigidbodies.Count; i++)
            {
                ManagedRigidbodyEntry entry = ManagedRigidbodies[i];
                if (entry == null || entry.Rb == null) continue;

                if (kinematic)
                {
                    if (!entry.Rb.isKinematic)
                    {
                        entry.Rb.velocity = Vector3.zero;
                        entry.Rb.angularVelocity = Vector3.zero;
                    }
                    entry.Rb.isKinematic = true;
                    entry.Rb.useGravity = false;
                }
                else
                {
                    entry.Rb.isKinematic = entry.DefaultKinematic;
                    entry.Rb.useGravity = entry.DefaultUseGravity;
                }
            }
        }

        public void SetInterpolationActive(bool active)
        {
            for (int i = 0; i < ManagedRigidbodies.Count; i++)
            {
                ManagedRigidbodyEntry entry = ManagedRigidbodies[i];
                if (entry == null || entry.Rb == null) continue;
                entry.Rb.interpolation = active ? RigidbodyInterpolation.Interpolate : entry.DefaultInterpolation;
            }
        }

        public void WakeUp()
        {
            for (int i = 0; i < ManagedRigidbodies.Count; i++)
            {
                ManagedRigidbodyEntry entry = ManagedRigidbodies[i];
                if (entry != null && entry.Rb != null && !entry.Rb.isKinematic)
                {
                    entry.Rb.WakeUp();
                }
            }
        }

        public void ApplyLinearAndAngularVelocity(Vector3 vel, Vector3 angVel)
        {
            for (int i = 0; i < ManagedRigidbodies.Count; i++)
            {
                ManagedRigidbodyEntry entry = ManagedRigidbodies[i];
                if (entry != null && entry.Rb != null && !entry.Rb.isKinematic)
                {
                    entry.Rb.velocity = vel;
                    entry.Rb.angularVelocity = angVel;
                }
            }
        }

        public void StepRemoteTransform(float dt)
        {
            if (Transform == null || !HasRemoteState) return;
            if (IsDrivenLocally || LocalPushTimer > 0f || IsCarriedLocally || IsCarriedRemotely) return;

            // Мягкая экстраполяция по последней скорости с затуханием, чтобы камера пассажира не дрожала между пакетами 25 Гц
            float packetAge = Mathf.Clamp(Time.time - LastRemotePhysicsTime, 0f, 0.12f);
            Vector3 predictedPos = TargetPosition + TargetVelocity * (packetAge * 0.65f);
            Quaternion predictedRot = TargetRotation;

            float distSq = (Transform.position - predictedPos).sqrMagnitude;
            if (distSq > 36f)
            {
                Transform.position = TargetPosition;
                Transform.rotation = TargetRotation;
                return;
            }

            float posT = Mathf.Clamp01(dt * 20f);
            float rotT = Mathf.Clamp01(dt * 20f);
            Transform.position = Vector3.Lerp(Transform.position, predictedPos, posT);
            Transform.rotation = Quaternion.Slerp(Transform.rotation, predictedRot, rotT);
        }

        public void StepRemoteControls(float dt)
        {
            if (IsDrivenLocally)
            {
                RestoreLocalSteeringControl();
                return;
            }

            if (IsDrivenRemotely && HasRemoteControls)
            {
                ApplyRemoteSteeringAngle(RemoteSteerAngle, dt);
                if (CarController != null)
                {
                    CarController.steerInput = RemoteSteerInput;
                    CarController.throttleInput = RemoteThrottleInput;
                    CarController.brakeInput = RemoteBrakeInput;
                    CarController.handbrakeInput = RemoteHandbrakeInput;
                    CarController.clutchInput = RemoteClutchInput;
                }
                if (Drivetrain != null)
                {
                    Drivetrain.gear = RemoteGear;
                }
            }
            else if (CarController != null && IsCurrentlyKinematic)
            {
                // Блокируем локальные педали/руль на клиенте, если машиной не управляют локально
                CarController.steerInput = 0f;
                CarController.throttleInput = 0f;
                CarController.brakeInput = 0f;
                CarController.clutchInput = 0f;
            }
        }
    }
}
