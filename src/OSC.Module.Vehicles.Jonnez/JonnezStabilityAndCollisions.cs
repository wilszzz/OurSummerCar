using System;
using System.Collections.Generic;
using OSC.Core.Bridges;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Vehicles.Jonnez
{
    internal sealed class JonnezStabilityAndCollisions
    {
        private readonly Action<string> log;

        private GameObject jonnezGo;
        private Transform jonnezTransform;
        private Rigidbody jonnezRb;
        private Transform steeringFork;
        private GameObject collidersCollObj;

        private readonly List<Collider> jonnezColliders = new List<Collider>();
        private readonly List<AudioSource> crashAudioSources = new List<AudioSource>();
        private readonly HashSet<int> ignoredExternalColliderIds = new HashSet<int>();
        private readonly HashSet<int> hookedCrashFsmIds = new HashSet<int>();

        private Vector3 defaultCenterOfMass;
        private bool hasDefaultCenterOfMass;
        private Vector3 activeTargetCom;
        private bool hasNetworkCom;
        private Vector3 networkCom;

        private float mountStabilizeUntil;
        private float teleportGuardUntil;
        private float nextCollisionScanTime;
        private int lastJonnezColliderCount = -1;

        private float remoteSteerTarget;
        private bool hasRemoteSteerTarget;

        public GameObject JonnezObject => jonnezGo;
        public Transform JonnezTransform => jonnezTransform;
        public Rigidbody JonnezRigidbody => jonnezRb;
        public Transform SteeringFork => steeringFork;
        public Vector3 DefaultCenterOfMass => defaultCenterOfMass;
        public Vector3 ActiveCenterOfMass => jonnezRb != null ? jonnezRb.centerOfMass : activeTargetCom;

        public JonnezStabilityAndCollisions(Action<string> log)
        {
            this.log = log;
        }

        public void Reset()
        {
            jonnezGo = null;
            jonnezTransform = null;
            jonnezRb = null;
            steeringFork = null;
            collidersCollObj = null;

            jonnezColliders.Clear();
            crashAudioSources.Clear();
            ignoredExternalColliderIds.Clear();
            hookedCrashFsmIds.Clear();

            defaultCenterOfMass = Vector3.zero;
            hasDefaultCenterOfMass = false;
            activeTargetCom = Vector3.zero;
            hasNetworkCom = false;
            networkCom = Vector3.zero;

            mountStabilizeUntil = 0f;
            teleportGuardUntil = 0f;
            nextCollisionScanTime = 0f;
            lastJonnezColliderCount = -1;
            remoteSteerTarget = 0f;
            hasRemoteSteerTarget = false;
        }

        public bool BindIfNeeded(out bool newlyBound)
        {
            newlyBound = false;
            if (jonnezGo != null && jonnezTransform != null)
            {
                return true;
            }

            GameObject found = JonnezHelpers.FindByNames(JonnezConstants.JonnezNames);
            if (found == null) return false;

            jonnezGo = found;
            jonnezTransform = found.transform;
            jonnezRb = found.GetComponent<Rigidbody>();
            steeringFork = JonnezHelpers.FindFirstPath(jonnezTransform, JonnezConstants.ForkPaths)
                ?? PlayMakerBridge.FindChildRecursive(jonnezTransform, "Steering");

            Transform collTr = jonnezTransform.Find("Colliders/Coll");
            if (collTr != null)
            {
                collidersCollObj = collTr.gameObject;
            }

            if (jonnezRb != null && !hasDefaultCenterOfMass)
            {
                defaultCenterOfMass = jonnezRb.centerOfMass;
                hasDefaultCenterOfMass = true;
                activeTargetCom = defaultCenterOfMass;
            }

            RefreshJonnezColliders();
            NeutralizeDriverHeadPivot();
            HookCrashFsmsAndCacheAudio();
            ScanAndIgnoreExternalCollisions(forceRescan: true);

            newlyBound = true;
            log?.Invoke("[Jonnez] Мопед JONNEZ ES обнаружен и стабилизирован");
            return true;
        }

        public bool IsJonnezEvent(int vehicleId, Transform vehicleTransform)
        {
            if (vehicleId == JonnezConstants.JonnezVehicleId) return true;
            return jonnezTransform != null && vehicleTransform == jonnezTransform;
        }

        public void OnMounted(bool isLocalDriver, bool kickstandDown)
        {
            if (jonnezTransform == null || jonnezRb == null) return;
            if (isLocalDriver) hasNetworkCom = false;

            NeutralizeDriverHeadPivot();
            mountStabilizeUntil = Time.time + JonnezConstants.MountStabilizeDuration;

            if (!jonnezRb.isKinematic)
            {
                // Гасим боковое вращение и рывок при посадке, чтобы мопед не заваливался и не улетал
                Vector3 angVel = jonnezRb.angularVelocity;
                jonnezRb.angularVelocity = new Vector3(angVel.x * 0.25f, angVel.y * 0.4f, 0f);

                Vector3 vel = jonnezRb.velocity;
                if (vel.sqrMagnitude > 2.25f && isLocalDriver)
                {
                    jonnezRb.velocity = vel * 0.35f;
                }
            }

            // Выравниваем крен при посадке, если мопед лежал на боку или наклонился слишком сильно
            Vector3 euler = jonnezTransform.eulerAngles;
            float roll = JonnezHelpers.NormalizeAngle(euler.z);
            float pitch = JonnezHelpers.NormalizeAngle(euler.x);
            bool needsUpright = Mathf.Abs(roll) > JonnezConstants.MountUprightClampDegrees || Mathf.Abs(pitch) > 45f;
            if (needsUpright && (isLocalDriver || !jonnezRb.isKinematic))
            {
                float clampedRoll = Mathf.Clamp(roll, -JonnezConstants.MountUprightClampDegrees, JonnezConstants.MountUprightClampDegrees);
                float clampedPitch = Mathf.Clamp(pitch, -20f, 20f);
                jonnezTransform.rotation = Quaternion.Euler(clampedPitch, euler.y, clampedRoll);
                if (Mathf.Abs(roll) > 45f)
                {
                    jonnezTransform.position += Vector3.up * 0.12f;
                }
            }

            ApplyCenterOfMassTuning(isMounted: true, kickstandDown: kickstandDown);
        }

        public void OnDismounted(bool kickstandDown)
        {
            hasNetworkCom = false;
            ApplyCenterOfMassTuning(isMounted: false, kickstandDown: kickstandDown);
        }

        public void ClearNetworkCenterOfMass() => hasNetworkCom = false;

        public void ApplyCenterOfMassTuning(bool isMounted, bool kickstandDown)
        {
            if (jonnezRb == null || !hasDefaultCenterOfMass) return;

            Vector3 targetCom;
            if (hasNetworkCom && !isMounted)
            {
                targetCom = networkCom;
            }
            else if (isMounted || !kickstandDown)
            {
                // Пониженный центр тяжести для устойчивости мопеда при посадке и езде без подножки
                float lowY = Mathf.Min(defaultCenterOfMass.y - 0.14f, -0.22f);
                targetCom = new Vector3(0f, lowY, defaultCenterOfMass.z);
            }
            else
            {
                float parkY = Mathf.Min(defaultCenterOfMass.y - 0.05f, -0.10f);
                targetCom = new Vector3(0f, parkY, defaultCenterOfMass.z);
            }

            activeTargetCom = targetCom;
            if ((jonnezRb.centerOfMass - targetCom).sqrMagnitude > 0.0001f)
            {
                jonnezRb.centerOfMass = targetCom;
            }
        }

        public void ApplyRemoteCenterOfMass(Vector3 com)
        {
            hasNetworkCom = true;
            networkCom = com;
            activeTargetCom = com;
            if (jonnezRb != null)
            {
                jonnezRb.centerOfMass = com;
            }
        }

        public void HandleTeleport(VehicleTeleportEvent evt)
        {
            if (!IsJonnezEvent(evt.VehicleId, evt.VehicleTransform)) return;

            teleportGuardUntil = Time.time + JonnezConstants.TeleportGuardDuration;
            hasRemoteSteerTarget = false;

            if (jonnezTransform != null)
            {
                Vector3 e = jonnezTransform.eulerAngles;
                float roll = Mathf.Clamp(JonnezHelpers.NormalizeAngle(e.z), -15f, 15f);
                float pitch = Mathf.Clamp(JonnezHelpers.NormalizeAngle(e.x), -15f, 15f);
                jonnezTransform.rotation = Quaternion.Euler(pitch, e.y, roll);
            }

            if (steeringFork != null)
            {
                Vector3 fe = steeringFork.localEulerAngles;
                steeringFork.localEulerAngles = new Vector3(fe.x, 0f, fe.z);
            }

            if (jonnezRb != null && !jonnezRb.isKinematic)
            {
                jonnezRb.velocity = Vector3.zero;
                jonnezRb.angularVelocity = Vector3.zero;
            }
        }

        public void HandleRemoteTelemetry(VehicleRemoteTelemetryEvent evt)
        {
            if (!IsJonnezEvent(evt.VehicleId, evt.VehicleTransform)) return;
            if (Time.time < teleportGuardUntil) return;

            remoteSteerTarget = Mathf.Clamp(JonnezHelpers.NormalizeAngle(evt.SteerAngle), -42f, 42f);
            hasRemoteSteerTarget = evt.IsDrivenRemotely;
        }

        public void HandleSpecialCarry(SpecialObjectCarryEvent evt)
        {
            if (evt.TargetObject == null || jonnezGo == null) return;
            if (evt.TargetObject != jonnezGo && evt.TargetObject.transform != jonnezTransform) return;

            if (collidersCollObj == null && jonnezTransform != null)
            {
                Transform collTr = jonnezTransform.Find("Colliders/Coll");
                if (collTr != null) collidersCollObj = collTr.gameObject;
            }

            if (collidersCollObj != null)
            {
                collidersCollObj.SetActive(!evt.IsCarried);
            }

            if (!evt.IsCarried)
            {
                // При включении Colliders/Coll после сброса Unity сбрасывает ignore-пары для заново активированных коллайдеров
                RefreshJonnezColliders();
                ignoredExternalColliderIds.Clear();
                ScanAndIgnoreExternalCollisions(forceRescan: true);

                if (jonnezRb != null && !jonnezRb.isKinematic)
                {
                    Vector3 ang = jonnezRb.angularVelocity;
                    jonnezRb.angularVelocity = new Vector3(ang.x * 0.3f, ang.y * 0.3f, 0f);
                }
            }
        }

        public void TickUpdate(bool isMountedLocally, bool isDrivenRemotely, bool kickstandDown)
        {
            if (jonnezGo == null || jonnezTransform == null) return;

            UpdateCrashAudioSuppression();

            if (Time.time >= nextCollisionScanTime)
            {
                nextCollisionScanTime = Time.time + 0.5f;
                ScanAndIgnoreExternalCollisions(forceRescan: false);
            }

            if (isMountedLocally && Time.time < mountStabilizeUntil && jonnezRb != null && !jonnezRb.isKinematic)
            {
                Vector3 ang = jonnezRb.angularVelocity;
                jonnezRb.angularVelocity = new Vector3(ang.x * 0.7f, ang.y * 0.85f, ang.z * 0.35f);
            }

            if (isDrivenRemotely && hasRemoteSteerTarget && steeringFork != null && Time.time >= teleportGuardUntil)
            {
                Vector3 fe = steeringFork.localEulerAngles;
                float nextY = Mathf.LerpAngle(fe.y, remoteSteerTarget, Mathf.Clamp01(Time.deltaTime * 16f));
                steeringFork.localEulerAngles = new Vector3(fe.x, nextY, fe.z);
            }

            if (isMountedLocally || !kickstandDown)
            {
                if (jonnezRb != null && (jonnezRb.centerOfMass - activeTargetCom).sqrMagnitude > 0.0004f)
                {
                    jonnezRb.centerOfMass = activeTargetCom;
                }
            }
        }

        public void TickFixedUpdate(bool isMountedLocally, bool isDrivenRemotely)
        {
            if (jonnezTransform == null || jonnezRb == null) return;

            // В первые мгновения после посадки мягко удерживаем раму от завала на бок
            if (isMountedLocally && Time.time < mountStabilizeUntil && !jonnezRb.isKinematic)
            {
                Vector3 euler = jonnezTransform.eulerAngles;
                float roll = JonnezHelpers.NormalizeAngle(euler.z);
                if (Mathf.Abs(roll) > JonnezConstants.MountUprightClampDegrees)
                {
                    float targetRoll = Mathf.Clamp(roll, -JonnezConstants.MountUprightClampDegrees, JonnezConstants.MountUprightClampDegrees);
                    float nextRoll = Mathf.LerpAngle(roll, targetRoll, Time.fixedDeltaTime * 10f);
                    jonnezTransform.rotation = Quaternion.Euler(euler.x, euler.y, nextRoll);
                }
            }

            if (!isDrivenRemotely && Time.time < teleportGuardUntil && !jonnezRb.isKinematic)
            {
                Vector3 euler = jonnezTransform.eulerAngles;
                float roll = JonnezHelpers.NormalizeAngle(euler.z);
                if (Mathf.Abs(roll) > 20f)
                {
                    jonnezTransform.rotation = Quaternion.Euler(euler.x, euler.y, Mathf.Clamp(roll, -20f, 20f));
                }
            }
        }

        public void TickLateUpdate(bool isDrivenRemotely)
        {
            if (jonnezTransform == null) return;

            if (isDrivenRemotely)
            {
                Vector3 euler = jonnezTransform.eulerAngles;
                float roll = JonnezHelpers.NormalizeAngle(euler.z);
                if (Mathf.Abs(roll) > JonnezConstants.MaxRemoteRollDegrees)
                {
                    float clamped = Mathf.Clamp(roll, -JonnezConstants.MaxRemoteRollDegrees, JonnezConstants.MaxRemoteRollDegrees);
                    jonnezTransform.rotation = Quaternion.Euler(euler.x, euler.y, clamped);
                }
            }
        }

        private void NeutralizeDriverHeadPivot()
        {
            if (jonnezTransform == null) return;

            Transform dhp = PlayMakerBridge.FindChildRecursive(jonnezTransform, "DriverHeadPivot");
            if (dhp != null)
            {
                var fsms = dhp.GetComponents<PlayMakerFSM>();
                for (int i = 0; i < fsms.Length; i++)
                {
                    if (fsms[i] != null)
                    {
                        fsms[i].enabled = false;
                        UnityEngine.Object.Destroy(fsms[i]);
                    }
                }

                Rigidbody dhpRb = dhp.GetComponent<Rigidbody>();
                if (dhpRb != null)
                {
                    dhpRb.isKinematic = true;
                }

                ConfigurableJoint joint = dhp.GetComponent<ConfigurableJoint>()
                    ?? (dhp.parent != null ? dhp.parent.GetComponentInChildren<ConfigurableJoint>() : null);
                if (joint != null)
                {
                    joint.breakForce = float.PositiveInfinity;
                    joint.breakTorque = float.PositiveInfinity;
                }
            }

            // Защита от любых оставшихся Death FSM под корнем Jonnez
            var allFsms = jonnezGo.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < allFsms.Length; i++)
            {
                PlayMakerFSM fsm = allFsms[i];
                if (fsm == null) continue;
                if (fsm.FsmName == "Death" || fsm.gameObject.name == "DriverHeadPivot")
                {
                    fsm.enabled = false;
                    UnityEngine.Object.Destroy(fsm);
                }
            }
        }

        private void HookCrashFsmsAndCacheAudio()
        {
            if (jonnezGo == null) return;

            crashAudioSources.Clear();
            var sources = jonnezGo.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource src = sources[i];
                if (src == null) continue;

                string clipName = src.clip != null ? src.clip.name : string.Empty;
                string goName = src.gameObject.name;

                if (JonnezHelpers.ContainsIgnoreCase(clipName, "hit") ||
                    JonnezHelpers.ContainsIgnoreCase(clipName, "crash") ||
                    JonnezHelpers.ContainsIgnoreCase(clipName, "carhit") ||
                    JonnezHelpers.ContainsIgnoreCase(goName, "hit") ||
                    JonnezHelpers.ContainsIgnoreCase(goName, "crash") ||
                    JonnezHelpers.ContainsIgnoreCase(goName, "carhit"))
                {
                    crashAudioSources.Add(src);
                }
            }

            var fsms = jonnezGo.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null) continue;

                int fsmId = fsm.GetInstanceID();
                if (hookedCrashFsmIds.Contains(fsmId)) continue;

                bool hooked = false;
                if (fsm.HasState("Crash"))
                {
                    PlayMakerFSM captured = fsm;
                    captured.InsertAction("Crash", () =>
                    {
                        if (ShouldSuppressCrashEffect())
                        {
                            captured.SendEvent("FINISHED");
                        }
                    }, 0, true);
                    hooked = true;
                }

                if (fsm.HasState("Car hit"))
                {
                    PlayMakerFSM captured = fsm;
                    captured.InsertAction("Car hit", () =>
                    {
                        if (ShouldSuppressCrashEffect())
                        {
                            captured.SendEvent("FINISHED");
                        }
                    }, 0, true);
                    hooked = true;
                }

                if (hooked)
                {
                    hookedCrashFsmIds.Add(fsmId);
                }
            }
        }

        private bool ShouldSuppressCrashEffect()
        {
            if (Time.time < mountStabilizeUntil || Time.time < teleportGuardUntil) return true;
            if (jonnezRb == null) return true;
            return jonnezRb.velocity.magnitude < JonnezConstants.LowSpeedCrashThreshold;
        }

        private void UpdateCrashAudioSuppression()
        {
            if (crashAudioSources.Count == 0) return;
            bool shouldMute = ShouldSuppressCrashEffect();

            for (int i = 0; i < crashAudioSources.Count; i++)
            {
                AudioSource src = crashAudioSources[i];
                if (src == null) continue;

                if (shouldMute)
                {
                    if (src.isPlaying) src.Stop();
                    src.mute = true;
                }
                else
                {
                    src.mute = false;
                }
            }
        }

        private void RefreshJonnezColliders()
        {
            jonnezColliders.Clear();
            if (jonnezGo == null) return;

            var cols = jonnezGo.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null)
                {
                    jonnezColliders.Add(cols[i]);
                }
            }
            lastJonnezColliderCount = jonnezColliders.Count;
        }

        private void ScanAndIgnoreExternalCollisions(bool forceRescan)
        {
            if (jonnezGo == null) return;

            Collider[] currentCols = jonnezGo.GetComponentsInChildren<Collider>(true);
            if (forceRescan || currentCols.Length != lastJonnezColliderCount)
            {
                jonnezColliders.Clear();
                for (int i = 0; i < currentCols.Length; i++)
                {
                    if (currentCols[i] != null) jonnezColliders.Add(currentCols[i]);
                }
                lastJonnezColliderCount = jonnezColliders.Count;
                if (forceRescan) ignoredExternalColliderIds.Clear();
            }

            if (jonnezColliders.Count == 0) return;

            // 1. Локальный PLAYER
            GameObject player = GameObject.Find("PLAYER") ?? GameObject.FindWithTag("Player");
            if (player != null)
            {
                IgnoreHierarchyAgainstJonnez(player.transform);
            }

            // 2. Удалённые куклы RemotePuppet_* и любые заспавненные рэгдоллы / DeadBody
            var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
            for (int i = 0; i < allTransforms.Length; i++)
            {
                Transform tr = allTransforms[i];
                if (tr == null || tr.parent != null) continue;

                string n = tr.name;
                if (n.StartsWith("RemotePuppet_", StringComparison.OrdinalIgnoreCase) ||
                    JonnezHelpers.ContainsIgnoreCase(n, "ragdoll") ||
                    JonnezHelpers.ContainsIgnoreCase(n, "deadbody"))
                {
                    IgnoreHierarchyAgainstJonnez(tr);
                }
            }

            // 3. Также проверяем дочерние рэгдоллы, если они не в корне сцены
            if (player != null)
            {
                var pTransforms = player.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < pTransforms.Length; i++)
                {
                    Transform tr = pTransforms[i];
                    if (tr == null) continue;
                    if (JonnezHelpers.ContainsIgnoreCase(tr.name, "ragdoll") ||
                        JonnezHelpers.ContainsIgnoreCase(tr.name, "deadbody"))
                    {
                        IgnoreHierarchyAgainstJonnez(tr);
                    }
                }
            }
        }

        private void IgnoreHierarchyAgainstJonnez(Transform root)
        {
            if (root == null) return;
            Collider[] extCols = root.GetComponentsInChildren<Collider>(true);
            for (int e = 0; e < extCols.Length; e++)
            {
                Collider ext = extCols[e];
                if (ext == null || !ext.enabled || !ext.gameObject.activeInHierarchy) continue;

                int cid = ext.GetInstanceID();
                if (ignoredExternalColliderIds.Contains(cid)) continue;

                bool anyIgnored = false;
                for (int j = 0; j < jonnezColliders.Count; j++)
                {
                    if (JonnezHelpers.SetIgnoreCollisionSafe(ext, jonnezColliders[j], true))
                    {
                        anyIgnored = true;
                    }
                }

                if (anyIgnored)
                {
                    ignoredExternalColliderIds.Add(cid);
                }
            }
        }
    }
}
