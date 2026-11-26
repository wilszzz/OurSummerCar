using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Vehicles.Jonnez
{
    internal sealed class JonnezEquipmentAndCargo
    {
        private readonly OscHostContext ctx;
        private readonly string moduleId;
        private readonly JonnezStabilityAndCollisions stability;

        // Подножка (Kickstand)
        private PlayMakerFSM kickstandFsm;
        private FsmBool kickstandDownVar;
        private string kickstandDownState;
        private string kickstandUpState;
        private Transform kickstandTransform;
        private Quaternion kickstandInitialLocalRot;
        private bool kickstandHasInitialRot;
        private bool suppressKickstandEcho;
        private bool isKickstandDown = true;

        // Шлем (Helmet)
        private GameObject helmetObject;
        private PlayMakerFSM helmetFsm;
        private FsmBool helmetWearVar;
        private FsmBool globalHelmetVar;
        private Transform playerHeadHelmetAnchor;
        private bool suppressHelmetEcho;
        private bool localHelmetEquipped;
        private int helmetItemHash;
        private float nextHelmetPollTime;
        private readonly Dictionary<int, bool> remoteHelmetStates = new Dictionary<int, bool>();
        private readonly Dictionary<int, GameObject> puppetHelmetVisuals = new Dictionary<int, GameObject>();

        // R5: Jonnez в кузове Hayosiko
        private Transform hayosikoTransform;
        private Rigidbody hayosikoRb;
        private int hayosikoDriverId = -1;
        private bool hayosikoDrivenLocally;
        private bool isStowedInHayosiko;
        private bool remoteSaysStowedInHayosiko;
        private int cargoAuthorityPlayerId;
        private Vector3 cargoLocalPosition;
        private Quaternion cargoLocalRotation = Quaternion.identity;
        private float lastCargoSyncSentTime;
        private float nextCargoSearchTime;

        public bool IsKickstandDown => isKickstandDown;
        public bool LocalHelmetEquipped => localHelmetEquipped;
        public int HelmetItemHash => helmetItemHash;
        public bool IsStowedInHayosiko => isStowedInHayosiko;
        public int CargoAuthorityPlayerId => cargoAuthorityPlayerId;
        public Vector3 CargoLocalPosition => cargoLocalPosition;
        public Quaternion CargoLocalRotation => cargoLocalRotation;

        public JonnezEquipmentAndCargo(OscHostContext context, string ownerModuleId, JonnezStabilityAndCollisions stability)
        {
            ctx = context;
            moduleId = ownerModuleId;
            this.stability = stability;
        }

        public void Reset()
        {
            if (isStowedInHayosiko)
            {
                ReleaseCargoAllowanceAndAuthority(notifyRemote: false);
            }

            kickstandFsm = null;
            kickstandDownVar = null;
            kickstandDownState = null;
            kickstandUpState = null;
            kickstandTransform = null;
            kickstandHasInitialRot = false;
            suppressKickstandEcho = false;
            isKickstandDown = true;

            helmetObject = null;
            helmetFsm = null;
            helmetWearVar = null;
            globalHelmetVar = null;
            playerHeadHelmetAnchor = null;
            suppressHelmetEcho = false;
            localHelmetEquipped = false;
            helmetItemHash = 0;
            nextHelmetPollTime = 0f;
            remoteHelmetStates.Clear();

            foreach (var kv in puppetHelmetVisuals)
            {
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            }
            puppetHelmetVisuals.Clear();

            hayosikoTransform = null;
            hayosikoRb = null;
            hayosikoDriverId = -1;
            hayosikoDrivenLocally = false;
            isStowedInHayosiko = false;
            remoteSaysStowedInHayosiko = false;
            cargoAuthorityPlayerId = 0;
            cargoLocalPosition = Vector3.zero;
            cargoLocalRotation = Quaternion.identity;
            lastCargoSyncSentTime = 0f;
            nextCargoSearchTime = 0f;
        }

        public void DiscoverEquipment()
        {
            DiscoverKickstand();
            DiscoverHelmet();
            DiscoverHayosiko();
        }

        public void OnLocalOccupancyChanged(LocalPlayerOccupancyEvent evt)
        {
            bool isHayosiko = evt.VehicleId == JonnezConstants.HayosikoVehicleId
                || (hayosikoTransform != null && evt.VehicleTransform == hayosikoTransform);

            if (isHayosiko)
            {
                if (evt.IsDriver && evt.IsSeated)
                {
                    hayosikoDrivenLocally = true;
                    hayosikoDriverId = ctx?.Transport != null ? ctx.Transport.LocalPlayerId : 0;
                }
                else if (!evt.IsSeated && hayosikoDrivenLocally)
                {
                    hayosikoDrivenLocally = false;
                    hayosikoDriverId = -1;
                }

                if (isStowedInHayosiko)
                {
                    RefreshCargoAuthority();
                }
                return;
            }

            if (stability.IsJonnezEvent(evt.VehicleId, evt.VehicleTransform) && evt.IsSeated)
            {
                if (isStowedInHayosiko)
                {
                    ReleaseCargoAllowanceAndAuthority(notifyRemote: true);
                }
            }
        }

        public void OnRemoteSeatChanged(RemotePlayerSeatEvent evt)
        {
            bool isHayosiko = evt.VehicleId == JonnezConstants.HayosikoVehicleId
                || (hayosikoTransform != null && evt.VehicleTransform == hayosikoTransform);

            if (isHayosiko)
            {
                if (evt.IsDriver && evt.IsSeated)
                {
                    hayosikoDriverId = evt.PlayerId;
                    hayosikoDrivenLocally = false;
                }
                else if (!evt.IsSeated && hayosikoDriverId == evt.PlayerId)
                {
                    hayosikoDriverId = -1;
                }

                if (isStowedInHayosiko)
                {
                    RefreshCargoAuthority();
                }
                return;
            }

            if (stability.IsJonnezEvent(evt.VehicleId, evt.VehicleTransform) && evt.IsSeated)
            {
                if (isStowedInHayosiko)
                {
                    ReleaseCargoAllowanceAndAuthority(notifyRemote: true);
                }
            }
        }

        public void OnSpecialObjectCarry(SpecialObjectCarryEvent evt)
        {
            if (evt.TargetObject == null || stability.JonnezObject == null) return;
            if (evt.TargetObject != stability.JonnezObject && evt.TargetObject.transform != stability.JonnezTransform) return;

            if (evt.IsCarried && isStowedInHayosiko)
            {
                ReleaseCargoAllowanceAndAuthority(notifyRemote: true);
            }
        }

        public void OnVehicleTeleport(VehicleTeleportEvent evt)
        {
            bool isHayosiko = evt.VehicleId == JonnezConstants.HayosikoVehicleId
                || (hayosikoTransform != null && evt.VehicleTransform == hayosikoTransform);

            if (isHayosiko && isStowedInHayosiko && hayosikoTransform != null && stability.JonnezTransform != null)
            {
                stability.JonnezTransform.position = hayosikoTransform.TransformPoint(cargoLocalPosition);
                stability.JonnezTransform.rotation = hayosikoTransform.rotation * cargoLocalRotation;
                if (stability.JonnezRigidbody != null && !stability.JonnezRigidbody.isKinematic)
                {
                    stability.JonnezRigidbody.velocity = Vector3.zero;
                    stability.JonnezRigidbody.angularVelocity = Vector3.zero;
                }
            }
        }

        public void OnRemoteDisconnected(int playerId)
        {
            remoteHelmetStates.Remove(playerId);
            if (puppetHelmetVisuals.TryGetValue(playerId, out GameObject vis))
            {
                if (vis != null) UnityEngine.Object.Destroy(vis);
                puppetHelmetVisuals.Remove(playerId);
            }

            if (hayosikoDriverId == playerId)
            {
                hayosikoDriverId = -1;
                if (isStowedInHayosiko) RefreshCargoAuthority();
            }
        }

        public void TickUpdate(bool isDrivenLocally, bool isDrivenRemotely, bool isCarried)
        {
            if (Time.time >= nextCargoSearchTime)
            {
                nextCargoSearchTime = Time.time + 2.0f;
                if (hayosikoTransform == null) DiscoverHayosiko();
                if (kickstandFsm == null && kickstandTransform == null) DiscoverKickstand();
                if (helmetObject == null && helmetFsm == null) DiscoverHelmet();
            }

            if (Time.time >= nextHelmetPollTime)
            {
                nextHelmetPollTime = Time.time + 0.25f;
                PollLocalHelmetState();
            }

            UpdatePuppetHelmetVisuals();
            UpdateHayosikoCargoStowage(isDrivenLocally, isDrivenRemotely, isCarried);
        }

        public void TickLateUpdate(bool isDrivenLocally, bool isDrivenRemotely, bool isCarried)
        {
            if (!isStowedInHayosiko || isDrivenLocally || isDrivenRemotely || isCarried) return;
            if (hayosikoTransform == null || stability.JonnezTransform == null) return;

            int localId = ctx?.Transport != null ? ctx.Transport.LocalPlayerId : 0;
            bool isLocalAuthority = localId == cargoAuthorityPlayerId;

            if (!isLocalAuthority)
            {
                // Не-владелец Hayosiko кинематически удерживает Jonnez в локальных координатах кузова
                // hayosikoTransform уже сглажен в VehiclesModule.OnLateUpdate, поэтому применяем локальные координаты без мирового отставания
                Vector3 targetWorldPos = hayosikoTransform.TransformPoint(cargoLocalPosition);
                Quaternion targetWorldRot = hayosikoTransform.rotation * cargoLocalRotation;
                stability.JonnezTransform.position = targetWorldPos;
                stability.JonnezTransform.rotation = targetWorldRot;
            }
        }

        #region Kickstand

        private void DiscoverKickstand()
        {
            Transform jTr = stability.JonnezTransform;
            if (jTr == null) return;

            var allTransforms = jTr.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < allTransforms.Length; i++)
            {
                Transform t = allTransforms[i];
                if (t == null) continue;
                if (JonnezHelpers.ContainsIgnoreCase(t.name, "kickstand") ||
                    JonnezHelpers.ContainsIgnoreCase(t.name, "sidestand") ||
                    JonnezHelpers.ContainsIgnoreCase(t.name, "stand") ||
                    JonnezHelpers.ContainsIgnoreCase(t.name, "jalka"))
                {
                    if (kickstandTransform == null)
                    {
                        kickstandTransform = t;
                        kickstandInitialLocalRot = t.localRotation;
                        kickstandHasInitialRot = true;
                    }

                    PlayMakerFSM[] fsms = t.GetComponents<PlayMakerFSM>();
                    for (int f = 0; f < fsms.Length; f++)
                    {
                        if (TryHookKickstandFsm(fsms[f])) return;
                    }
                }
            }

            // Поиск по всем FSM мопеда на случай нестандартного имени GameObject
            var allFsms = jTr.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < allFsms.Length; i++)
            {
                PlayMakerFSM fsm = allFsms[i];
                if (fsm == null) continue;
                if (JonnezHelpers.ContainsIgnoreCase(fsm.FsmName, "kickstand") ||
                    JonnezHelpers.ContainsIgnoreCase(fsm.FsmName, "stand"))
                {
                    if (TryHookKickstandFsm(fsm)) return;
                }
            }
        }

        private bool TryHookKickstandFsm(PlayMakerFSM fsm)
        {
            if (fsm == null) return false;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, "Jonnez_Kickstand")) return false;

            fsm.InitializeSafe();
            kickstandFsm = fsm;
            kickstandDownVar = fsm.FsmVariables.FindFsmBool("Down")
                ?? fsm.FsmVariables.FindFsmBool("Stand")
                ?? fsm.FsmVariables.FindFsmBool("Kickstand")
                ?? fsm.FsmVariables.FindFsmBool("On");

            kickstandDownState = null;
            kickstandUpState = null;
            string downState = FindMatchingState(fsm, new[] { "Down", "Stand", "On", "Extend", "State 1" });
            string upState = FindMatchingState(fsm, new[] { "Up", "Off", "Retract", "State 2" });

            if (!string.IsNullOrEmpty(downState))
            {
                kickstandDownState = downState;
                fsm.AddSafeGlobalTransition("MP_KICKSTAND_DOWN", downState);
                fsm.InsertAction(downState, () => OnLocalKickstandChanged(true), 0, false);
            }
            if (!string.IsNullOrEmpty(upState))
            {
                kickstandUpState = upState;
                fsm.AddSafeGlobalTransition("MP_KICKSTAND_UP", upState);
                fsm.InsertAction(upState, () => OnLocalKickstandChanged(false), 0, false);
            }

            if (kickstandDownVar != null)
            {
                isKickstandDown = kickstandDownVar.Value;
            }
            return true;
        }

        private void OnLocalKickstandChanged(bool down)
        {
            if (suppressKickstandEcho)
            {
                suppressKickstandEcho = false;
                return;
            }

            stability.ClearNetworkCenterOfMass();
            isKickstandDown = down;
            stability.ApplyCenterOfMassTuning(isMounted: false, kickstandDown: down);
            SendKickstandPacket(down);
            SendCenterOfMassPacket(stability.ActiveCenterOfMass);
        }

        public void ApplyRemoteKickstand(bool down)
        {
            stability.ClearNetworkCenterOfMass();
            isKickstandDown = down;
            if (kickstandDownVar != null) kickstandDownVar.Value = down;

            string targetState = down ? kickstandDownState : kickstandUpState;
            if (kickstandFsm != null && !string.IsNullOrEmpty(targetState) && kickstandFsm.gameObject.activeInHierarchy && kickstandFsm.enabled)
            {
                string ev = down ? "MP_KICKSTAND_DOWN" : "MP_KICKSTAND_UP";
                if (kickstandFsm.ActiveStateName != targetState)
                {
                    suppressKickstandEcho = true;
                    try
                    {
                        kickstandFsm.SendEvent(ev);
                    }
                    finally
                    {
                        suppressKickstandEcho = false;
                    }
                }
            }
            else if (kickstandTransform != null && kickstandHasInitialRot)
            {
                kickstandTransform.localRotation = down
                    ? kickstandInitialLocalRot
                    : kickstandInitialLocalRot * Quaternion.Euler(0f, 0f, -65f);
            }

            stability.ApplyCenterOfMassTuning(isMounted: false, kickstandDown: down);
        }

        #endregion

        #region Helmet

        private void DiscoverHelmet()
        {
            if (helmetObject == null)
            {
                helmetObject = GameObject.Find("helmet(itemx)") ?? GameObject.Find("ITEMS/helmet(itemx)");
                if (helmetObject == null)
                {
                    var allGos = Resources.FindObjectsOfTypeAll<GameObject>();
                    for (int i = 0; i < allGos.Length; i++)
                    {
                        GameObject g = allGos[i];
                        if (g != null && g.name == "helmet(itemx)")
                        {
                            helmetObject = g;
                            break;
                        }
                    }
                }
            }

            if (helmetObject != null && helmetItemHash == 0)
            {
                helmetItemHash = SceneHasher.GetHash(helmetObject);
            }

            if (helmetFsm == null && helmetObject != null)
            {
                PlayMakerFSM[] childFsms = helmetObject.GetComponentsInChildren<PlayMakerFSM>(true);
                PlayMakerFSM candidate = PlayMakerBridge.FindFsm(helmetObject, "Use")
                    ?? PlayMakerBridge.FindFsm(helmetObject, "Wear")
                    ?? PlayMakerBridge.FindFsm(helmetObject, "Equip")
                    ?? (childFsms.Length > 0 ? childFsms[0] : null);

                if (candidate != null && ctx.Ownership.TryClaim(candidate, moduleId, "Jonnez_Helmet"))
                {
                    helmetFsm = candidate;
                    helmetFsm.InitializeSafe();
                    helmetWearVar = helmetFsm.FsmVariables.FindFsmBool("Wear")
                        ?? helmetFsm.FsmVariables.FindFsmBool("Equipped")
                        ?? helmetFsm.FsmVariables.FindFsmBool("On");

                    string wearState = FindMatchingState(helmetFsm, new[] { "Wear", "Equip", "Put on", "On", "State 1" });
                    string removeState = FindMatchingState(helmetFsm, new[] { "Remove", "Take off", "Drop", "Off", "State 2" });

                    if (!string.IsNullOrEmpty(wearState))
                    {
                        helmetFsm.AddSafeGlobalTransition("MP_HELMET_WEAR", wearState);
                        helmetFsm.InsertAction(wearState, () => SetLocalHelmetState(true, fromFsm: true), 0, false);
                    }
                    if (!string.IsNullOrEmpty(removeState))
                    {
                        helmetFsm.AddSafeGlobalTransition("MP_HELMET_REMOVE", removeState);
                        helmetFsm.InsertAction(removeState, () => SetLocalHelmetState(false, fromFsm: true), 0, false);
                    }
                }
            }

            if (globalHelmetVar == null && PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
            {
                globalHelmetVar = PlayMakerGlobals.Instance.Variables.FindFsmBool("PlayerHelmet")
                    ?? PlayMakerGlobals.Instance.Variables.FindFsmBool("Helmet");
            }

            if (playerHeadHelmetAnchor == null)
            {
                GameObject player = GameObject.Find("PLAYER");
                if (player != null)
                {
                    playerHeadHelmetAnchor = PlayMakerBridge.FindChildRecursive(player.transform, "Helmet")
                        ?? PlayMakerBridge.FindChildRecursive(player.transform, "helmet");
                }
            }
        }

        private void PollLocalHelmetState()
        {
            bool detected = false;
            if (globalHelmetVar != null)
            {
                detected = globalHelmetVar.Value;
            }
            else if (helmetWearVar != null)
            {
                detected = helmetWearVar.Value;
            }
            else if (playerHeadHelmetAnchor != null && playerHeadHelmetAnchor.gameObject.activeInHierarchy)
            {
                detected = true;
            }
            else if (helmetObject != null && helmetObject.transform.parent != null)
            {
                string rootName = helmetObject.transform.root != null ? helmetObject.transform.root.name : string.Empty;
                if (rootName.StartsWith("PLAYER", StringComparison.OrdinalIgnoreCase))
                {
                    detected = true;
                }
            }

            if (detected != localHelmetEquipped)
            {
                SetLocalHelmetState(detected, fromFsm: false);
            }
        }

        private void SetLocalHelmetState(bool equipped, bool fromFsm)
        {
            if (fromFsm && suppressHelmetEcho)
            {
                suppressHelmetEcho = false;
                return;
            }

            if (localHelmetEquipped == equipped) return;
            localHelmetEquipped = equipped;

            int myId = ctx?.Transport != null ? ctx.Transport.LocalPlayerId : 0;
            SendHelmetPacket(myId, equipped, helmetItemHash);
        }

        public void ApplyRemoteHelmetState(int playerId, bool equipped, int itemHash)
        {
            int myId = ctx?.Transport != null ? ctx.Transport.LocalPlayerId : 0;
            if (playerId == myId) return;

            remoteHelmetStates[playerId] = equipped;
            if (itemHash != 0 && helmetItemHash == 0) helmetItemHash = itemHash;

            if (puppetHelmetVisuals.TryGetValue(playerId, out GameObject vis) && vis != null)
            {
                vis.SetActive(equipped);
            }
        }

        private void UpdatePuppetHelmetVisuals()
        {
            foreach (var kv in remoteHelmetStates)
            {
                int playerId = kv.Key;
                bool equipped = kv.Value;

                string puppetName = playerId == 0 ? "RemotePuppet_Host" : $"RemotePuppet_Client_{playerId}";
                GameObject puppetRoot = GameObject.Find(puppetName);
                if (puppetRoot == null) continue;

                if (!puppetHelmetVisuals.TryGetValue(playerId, out GameObject vis) || vis == null)
                {
                    if (!equipped) continue;
                    vis = CreatePuppetHelmetVisual(puppetRoot.transform);
                    puppetHelmetVisuals[playerId] = vis;
                }

                if (vis != null && vis.activeSelf != equipped)
                {
                    vis.SetActive(equipped);
                }
            }
        }

        private GameObject CreatePuppetHelmetVisual(Transform puppetRoot)
        {
            Transform headBone = puppetRoot.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/headPivot/HeadRotationPivot")
                ?? PlayMakerBridge.FindChildRecursive(puppetRoot, "HeadRotationPivot")
                ?? PlayMakerBridge.FindChildRecursive(puppetRoot, "Sphere");

            Transform parent = headBone != null ? headBone : puppetRoot;
            GameObject visual = null;

            if (helmetObject != null)
            {
                MeshFilter[] mfs = helmetObject.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < mfs.Length; i++)
                {
                    MeshFilter mf = mfs[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    MeshRenderer mr = mf.gameObject.GetComponent<MeshRenderer>();
                    if (mr == null) continue;

                    visual = new GameObject("OSC_PuppetHelmetVisual");
                    var newMf = visual.AddComponent<MeshFilter>();
                    newMf.sharedMesh = mf.sharedMesh;
                    var newMr = visual.AddComponent<MeshRenderer>();
                    newMr.sharedMaterials = mr.sharedMaterials;
                    break;
                }
            }

            if (visual == null)
            {
                visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                visual.name = "OSC_PuppetHelmetVisual";
                Collider c = visual.GetComponent<Collider>();
                if (c != null) UnityEngine.Object.Destroy(c);
                visual.transform.localScale = new Vector3(0.32f, 0.30f, 0.34f);
            }

            visual.transform.SetParent(parent, false);
            visual.transform.localPosition = headBone != null ? new Vector3(0f, 0.09f, 0.01f) : new Vector3(0f, 1.05f, 0f);
            visual.transform.localRotation = Quaternion.identity;
            return visual;
        }

        #endregion

        #region R5: Jonnez in Hayosiko Cargo Bed

        private void DiscoverHayosiko()
        {
            if (hayosikoTransform != null) return;
            GameObject hayo = JonnezHelpers.FindByNames(JonnezConstants.HayosikoNames);
            if (hayo != null)
            {
                hayosikoTransform = hayo.transform;
                hayosikoRb = hayo.GetComponent<Rigidbody>();
            }
        }

        private void UpdateHayosikoCargoStowage(bool isDrivenLocally, bool isDrivenRemotely, bool isCarried)
        {
            if (hayosikoTransform == null || stability.JonnezTransform == null || stability.JonnezObject == null) return;

            if (isDrivenLocally || isDrivenRemotely || isCarried)
            {
                if (isStowedInHayosiko)
                {
                    ReleaseCargoAllowanceAndAuthority(notifyRemote: true);
                }
                return;
            }

            Vector3 localInHayo = hayosikoTransform.InverseTransformPoint(stability.JonnezTransform.position);
            bool insideEnter = JonnezHelpers.IsInsideBounds(localInHayo, JonnezConstants.HayosikoBayEnterMin, JonnezConstants.HayosikoBayEnterMax);
            bool insideExit = JonnezHelpers.IsInsideBounds(localInHayo, JonnezConstants.HayosikoBayExitMin, JonnezConstants.HayosikoBayExitMax);

            int localId = ctx?.Transport != null ? ctx.Transport.LocalPlayerId : 0;
            int expectedAuthority = GetHayosikoCurrentOwnerId();

            if (!isStowedInHayosiko)
            {
                bool canStowLocally = insideEnter && IsRelativeSpeedLow();
                bool acceptRemoteStow = remoteSaysStowedInHayosiko && insideExit;

                if (canStowLocally || acceptRemoteStow)
                {
                    isStowedInHayosiko = true;
                    cargoAuthorityPlayerId = expectedAuthority;
                    cargoLocalPosition = localInHayo;
                    cargoLocalRotation = Quaternion.Inverse(hayosikoTransform.rotation) * stability.JonnezTransform.rotation;

                    PublishCargoAuthority(active: true);
                    ctx.Events.Publish(new VehicleCargoCarryAllowanceEvent
                    {
                        TargetObject = stability.JonnezObject,
                        AllowCarryWhenParented = true
                    });

                    if (localId == cargoAuthorityPlayerId)
                    {
                        SendCargoHayosikoPacket(stowed: true);
                    }
                    ctx.Log?.Invoke($"[Jonnez/R5] Мопед закреплён в кузове Hayosiko (владелец физикой=#{cargoAuthorityPlayerId})");
                }
            }
            else
            {
                if (!insideExit && localId == cargoAuthorityPlayerId)
                {
                    ReleaseCargoAllowanceAndAuthority(notifyRemote: true);
                    return;
                }

                if (cargoAuthorityPlayerId != expectedAuthority)
                {
                    cargoAuthorityPlayerId = expectedAuthority;
                    PublishCargoAuthority(active: true);
                }

                if (localId == cargoAuthorityPlayerId)
                {
                    cargoLocalPosition = localInHayo;
                    cargoLocalRotation = Quaternion.Inverse(hayosikoTransform.rotation) * stability.JonnezTransform.rotation;

                    if (Time.time - lastCargoSyncSentTime >= 0.05f)
                    {
                        lastCargoSyncSentTime = Time.time;
                        SendCargoHayosikoPacket(stowed: true);
                    }
                }
            }
        }

        private bool IsRelativeSpeedLow()
        {
            if (stability.JonnezRigidbody == null || stability.JonnezRigidbody.isKinematic) return true;
            Vector3 hayoVel = hayosikoRb != null ? hayosikoRb.velocity : Vector3.zero;
            return (stability.JonnezRigidbody.velocity - hayoVel).sqrMagnitude <= 6.25f; // <= 2.5 m/s
        }

        private int GetHayosikoCurrentOwnerId()
        {
            if (hayosikoDrivenLocally)
            {
                return ctx?.Transport != null ? ctx.Transport.LocalPlayerId : 0;
            }
            if (hayosikoDriverId >= 0) return hayosikoDriverId;
            return 0; // По умолчанию владеет Хост
        }

        private void RefreshCargoAuthority()
        {
            cargoAuthorityPlayerId = GetHayosikoCurrentOwnerId();
            PublishCargoAuthority(active: true);

            int localId = ctx?.Transport != null ? ctx.Transport.LocalPlayerId : 0;
            if (localId == cargoAuthorityPlayerId)
            {
                SendCargoHayosikoPacket(stowed: true);
            }
        }

        private void ReleaseCargoAllowanceAndAuthority(bool notifyRemote)
        {
            isStowedInHayosiko = false;
            remoteSaysStowedInHayosiko = false;

            PublishCargoAuthority(active: false);
            if (stability.JonnezObject != null && ctx?.Events != null)
            {
                ctx.Events.Publish(new VehicleCargoCarryAllowanceEvent
                {
                    TargetObject = stability.JonnezObject,
                    AllowCarryWhenParented = false
                });
            }

            if (notifyRemote)
            {
                SendCargoHayosikoPacket(stowed: false);
            }
            ctx?.Log?.Invoke("[Jonnez/R5] Мопед освобождён из кузова Hayosiko");
        }

        private void PublishCargoAuthority(bool active)
        {
            if (ctx?.Events == null || stability.JonnezTransform == null) return;
            int localId = ctx?.Transport != null ? ctx.Transport.LocalPlayerId : 0;
            bool isLocalAuthority = localId == cargoAuthorityPlayerId;

            ctx.Events.Publish(new VehiclePhysicsAuthorityEvent
            {
                VehicleId = JonnezConstants.JonnezVehicleId,
                VehicleTransform = stability.JonnezTransform,
                SourceModuleId = moduleId,
                OverrideActive = active,
                AuthorityPlayerId = cargoAuthorityPlayerId,
                ForceKinematic = active && !isLocalAuthority,
                ForceDynamic = active && isLocalAuthority,
                SuppressWorldTransformSync = active
            });
        }

        public void ApplyRemoteCargoState(bool stowed, int authorityId, Vector3 localPos, Quaternion localRot)
        {
            remoteSaysStowedInHayosiko = stowed;
            if (!stowed)
            {
                if (isStowedInHayosiko)
                {
                    ReleaseCargoAllowanceAndAuthority(notifyRemote: false);
                }
                return;
            }

            cargoAuthorityPlayerId = authorityId;
            cargoLocalPosition = localPos;
            cargoLocalRotation = localRot;

            if (!isStowedInHayosiko)
            {
                isStowedInHayosiko = true;
                PublishCargoAuthority(active: true);
                if (stability.JonnezObject != null)
                {
                    ctx.Events.Publish(new VehicleCargoCarryAllowanceEvent
                    {
                        TargetObject = stability.JonnezObject,
                        AllowCarryWhenParented = true
                    });
                }
            }
        }

        #endregion

        #region Network Helpers

        private void SendKickstandPacket(bool down)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.JonnezStateSync))
            {
                p.WriteByte((byte)JonnezSubPacket.KickstandState);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteBoolean(down);
                ctx.Transport.Send(p);
            }
        }

        public void SendCenterOfMassPacket(Vector3 com)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.JonnezStateSync))
            {
                p.WriteByte((byte)JonnezSubPacket.CenterOfMassState);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteVector3(com);
                ctx.Transport.Send(p);
            }
        }

        private void SendHelmetPacket(int playerId, bool equipped, int itemHash)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.JonnezStateSync))
            {
                p.WriteByte((byte)JonnezSubPacket.HelmetState);
                p.WriteInt32(playerId);
                p.WriteBoolean(equipped);
                p.WriteInt32(itemHash);
                ctx.Transport.Send(p);
            }
        }

        private void SendCargoHayosikoPacket(bool stowed)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.JonnezStateSync))
            {
                p.WriteByte((byte)JonnezSubPacket.CargoHayosikoState);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteBoolean(stowed);
                p.WriteInt32(cargoAuthorityPlayerId);
                p.WriteVector3(cargoLocalPosition);
                p.WriteQuaternion(cargoLocalRotation);
                ctx.Transport.Send(p);
            }
        }

        private static string FindMatchingState(PlayMakerFSM fsm, string[] candidates)
        {
            if (fsm == null || candidates == null) return null;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (fsm.HasState(candidates[i])) return candidates[i];
            }
            return null;
        }

        #endregion
    }
}
