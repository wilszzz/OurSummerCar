using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Vehicles.Controls
{
    /// <summary>
    /// Синхронизация буксировочного троса (ConfigurableJoint + изолированные удалённые тросы),
    /// R5-арбитраж владения физикой буксируемого автопоезда (VehiclePhysicsAuthorityEvent),
    /// лебёдки/шланга Gifu, а также сцепки прицепов Caravan/Flatbed (PacketType.VehicleTowingSync = 73).
    /// </summary>
    internal sealed class TowingAndWinchSync
    {
        private sealed class TowLinkEntry
        {
            public int LinkId;
            public int OwnerPlayerId;
            public string Hook1Path;
            public string Hook2Path;
            public GameObject Hook1Obj;
            public GameObject Hook2Obj;
            public VehicleCatalogEntry LeadVehicle;
            public VehicleCatalogEntry TowedVehicle;
            public bool IsLocalFsmOwned;
            public bool VanillaJointCleared;
            public bool JointCreated;
            public ConfigurableJoint RemoteJoint;
            public GameObject VisualRopeObj;
            public LineRenderer VisualLine;
        }

        private sealed class AuthoritySnapshot
        {
            public byte VehicleId;
            public Transform VehicleTransform;
            public int AuthorityPlayerId;
            public bool ForceKinematic;
            public bool ForceDynamic;
        }

        private readonly OscHostContext ctx;
        private readonly string moduleId;
        private readonly VehicleCatalogEntry[] catalog;

        private PlayMakerFSM localRopeFsm;
        private bool isSuppressingRope;
        private int activeLocalLinkId;
        private int nextLinkSeq = 1;

        private readonly Dictionary<int, TowLinkEntry> activeLinks = new Dictionary<int, TowLinkEntry>();
        private readonly Dictionary<byte, AuthoritySnapshot> publishedAuthorities = new Dictionary<byte, AuthoritySnapshot>();

        // Caravan & Flatbed
        private PlayMakerFSM caravanDetachFsm;
        private bool isSuppressingCaravan;
        private bool isCaravanDetached;
        private PlayMakerFSM flatbedHitchFsm;
        private PlayMakerFSM flatbedTipFsm;
        private bool isSuppressingFlatbed;
        private bool? flatbedHitchState;
        private bool? flatbedTipState;

        // Gifu шланг и лебёдка (defensive discovery)
        private PlayMakerFSM activateHoseFsm;
        private PlayMakerFSM hoseCouplerFsm;
        private bool isSuppressingHose;
        private byte lastGifuHoseActivateAction;
        private byte lastGifuHoseCouplerAction;
        private readonly List<PlayMakerFSM> gifuWinchFsms = new List<PlayMakerFSM>();
        private bool isSuppressingWinch;
        private bool winchDiscoveryLogged;

        private float nextScanTime;

        public int ActiveTowLinksCount => activeLinks.Count;
        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public TowingAndWinchSync(OscHostContext context, string ownerModuleId, VehicleCatalogEntry[] vehicleCatalog)
        {
            ctx = context;
            moduleId = ownerModuleId;
            catalog = vehicleCatalog;
        }

        public void Reset()
        {
            ReleaseAllPhysicsAuthorities();
            ClearAllTowLinks();

            localRopeFsm = null;
            isSuppressingRope = false;
            activeLocalLinkId = 0;

            caravanDetachFsm = null;
            isSuppressingCaravan = false;
            isCaravanDetached = false;
            flatbedHitchFsm = null;
            flatbedTipFsm = null;
            isSuppressingFlatbed = false;
            flatbedHitchState = null;
            flatbedTipState = null;

            activateHoseFsm = null;
            hoseCouplerFsm = null;
            isSuppressingHose = false;
            lastGifuHoseActivateAction = 0;
            lastGifuHoseCouplerAction = 0;
            gifuWinchFsms.Clear();
            isSuppressingWinch = false;
            winchDiscoveryLogged = false;
            nextScanTime = 0f;
        }

        public void OnLevelLoaded()
        {
            ScanTowingAndWinchObjects();
        }

        public void OnUpdate()
        {
            if (Time.time >= nextScanTime)
            {
                nextScanTime = Time.time + 6f;
                if (localRopeFsm == null || activateHoseFsm == null || caravanDetachFsm == null)
                {
                    ScanTowingAndWinchObjects();
                }
            }

            UpdateVisualRopesAndCheckSnaps();
        }

        public void OnOccupancyChanged()
        {
            RecomputeTowTrainAuthorities();
        }

        public void OnClientDisconnected(int connectionId)
        {
            var toRemove = new List<int>();
            foreach (var kv in activeLinks)
            {
                if (kv.Value.OwnerPlayerId == connectionId)
                {
                    toRemove.Add(kv.Key);
                }
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                RemoveTowLink(toRemove[i], notifyLocalFsm: true);
            }
            RecomputeTowTrainAuthorities();
        }

        public void SendSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            foreach (var kv in activeLinks)
            {
                TowLinkEntry link = kv.Value;
                if (link == null) continue;
                using (var p = new PacketWriter(PacketType.VehicleTowingSync))
                {
                    p.WriteByte((byte)TowingSubType.RopeAttach);
                    p.WriteInt32(link.OwnerPlayerId);
                    p.WriteInt32(link.LinkId);
                    p.WriteString(link.Hook1Path ?? string.Empty);
                    p.WriteString(link.Hook2Path ?? string.Empty);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            if (lastGifuHoseActivateAction != 0)
            {
                using (var p = new PacketWriter(PacketType.VehicleTowingSync))
                {
                    p.WriteByte((byte)TowingSubType.GifuHose);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteByte(lastGifuHoseActivateAction);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            if (lastGifuHoseCouplerAction != 0)
            {
                using (var p = new PacketWriter(PacketType.VehicleTowingSync))
                {
                    p.WriteByte((byte)TowingSubType.GifuHose);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteByte(lastGifuHoseCouplerAction);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            if (isCaravanDetached)
            {
                using (var p = new PacketWriter(PacketType.VehicleTowingSync))
                {
                    p.WriteByte((byte)TowingSubType.CaravanDetach);
                    p.WriteInt32(LocalPlayerId);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            if (flatbedHitchState.HasValue)
            {
                using (var p = new PacketWriter(PacketType.VehicleTowingSync))
                {
                    p.WriteByte((byte)TowingSubType.FlatbedState);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteByte(0);
                    p.WriteBoolean(flatbedHitchState.Value);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            if (flatbedTipState.HasValue)
            {
                using (var p = new PacketWriter(PacketType.VehicleTowingSync))
                {
                    p.WriteByte((byte)TowingSubType.FlatbedState);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteByte(1);
                    p.WriteBoolean(flatbedTipState.Value);
                    ctx.Transport.SendTo(p, clientId);
                }
            }
        }

        public void ScanTowingAndWinchObjects()
        {
            HookLocalTowingRope();
            HookCaravanAndFlatbed();
            HookGifuHoseAndWinch();
        }

        private void HookLocalTowingRope()
        {
            if (localRopeFsm != null) return;

            GameObject ropeGo = GameObject.Find("FPSCamera/TowingRope/Rope")
                ?? GameObject.Find("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/TowingRope/Rope")
                ?? GameObject.Find("TowingRope/Rope");
            if (ropeGo == null) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(ropeGo, "Status");
            if (fsm == null || !ctx.Ownership.TryClaim(fsm, moduleId, "LocalTowingRope_Status")) return;

            localRopeFsm = fsm;
            fsm.AddSafeGlobalTransition("MP_SYNC_ATTACH", "Set joint");
            fsm.AddSafeGlobalTransition("MP_SYNC_DETACH", "Destroy joint");
            fsm.AddSafeGlobalTransition("MP_SYNC_SNAP", "Snap off");

            if (fsm.HasState("Set joint"))
            {
                fsm.InsertAction("Set joint", () =>
                {
                    if (isSuppressingRope) return;
                    FsmGameObject h1 = fsm.FsmVariables.FindFsmGameObject("Hook1");
                    FsmGameObject h2 = fsm.FsmVariables.FindFsmGameObject("Hook2");
                    GameObject h1Obj = h1 != null ? h1.Value : null;
                    GameObject h2Obj = h2 != null ? h2.Value : null;
                    string p1 = h1Obj != null ? ControlsHelpers.GetHierarchyPath(h1Obj.transform) : string.Empty;
                    string p2 = h2Obj != null ? ControlsHelpers.GetHierarchyPath(h2Obj.transform) : string.Empty;

                    int linkId = (LocalPlayerId * 100000) + (nextLinkSeq++);
                    activeLocalLinkId = linkId;
                    RegisterTowLink(linkId, LocalPlayerId, p1, p2, h1Obj, h2Obj, isLocalFsmOwned: true);

                    if (IsNetworkRunning)
                    {
                        SendRopePacket(TowingSubType.RopeAttach, linkId, p1, p2);
                    }
                }, -1, true);
            }

            if (fsm.HasState("Destroy joint"))
            {
                fsm.InsertAction("Destroy joint", () =>
                {
                    if (isSuppressingRope) return;
                    int closedId = activeLocalLinkId;
                    activeLocalLinkId = 0;
                    if (closedId != 0)
                    {
                        RemoveTowLink(closedId, notifyLocalFsm: false);
                        if (IsNetworkRunning) SendRopePacket(TowingSubType.RopeDetach, closedId, string.Empty, string.Empty);
                    }
                }, -1, true);
            }

            if (fsm.HasState("Snap off"))
            {
                fsm.InsertAction("Snap off", () =>
                {
                    if (isSuppressingRope) return;
                    int snappedId = activeLocalLinkId;
                    activeLocalLinkId = 0;
                    if (snappedId != 0)
                    {
                        RemoveTowLink(snappedId, notifyLocalFsm: false);
                        if (IsNetworkRunning) SendRopePacket(TowingSubType.RopeSnap, snappedId, string.Empty, string.Empty);
                    }
                }, -1, true);
            }
        }

        private void HookCaravanAndFlatbed()
        {
            if (caravanDetachFsm == null)
            {
                GameObject caravanGo = GameObject.Find("CARAVAN") ?? GameObject.Find("Caravan");
                if (caravanGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(caravanGo, "Detach");
                    if (fsm != null && ctx.Ownership.TryClaim(fsm, moduleId, "Caravan_Detach"))
                    {
                        caravanDetachFsm = fsm;
                        fsm.AddSafeGlobalTransition("MP_SYNC_DETACH", "Detach");
                        if (fsm.HasState("Detach"))
                        {
                            fsm.InsertAction("Detach", () =>
                            {
                                if (isSuppressingCaravan) return;
                                isCaravanDetached = true;
                                if (!IsNetworkRunning) return;
                                using (var p = new PacketWriter(PacketType.VehicleTowingSync))
                                {
                                    p.WriteByte((byte)TowingSubType.CaravanDetach);
                                    p.WriteInt32(LocalPlayerId);
                                    ctx.Transport.Send(p);
                                }
                            }, 0, true);
                        }
                    }
                }
            }

            VehicleCatalogEntry flatbed = ControlsHelpers.FindVehicleById(catalog, (int)VehicleSlot.Flatbed);
            GameObject flatbedGo = flatbed?.RootObject ?? GameObject.Find("FLATBED");
            if (flatbedGo != null)
            {
                if (flatbedHitchFsm == null)
                {
                    PlayMakerFSM hitch = PlayMakerBridge.FindFsm(flatbedGo, "Hitch") ?? PlayMakerBridge.FindFsm(flatbedGo, "Attach");
                    if (hitch != null && ctx.Ownership.TryClaim(hitch, moduleId, "Flatbed_Hitch"))
                    {
                        flatbedHitchFsm = hitch;
                        HookBinaryFsm(hitch, "Attach", "Detach", () => isSuppressingFlatbed, state =>
                        {
                            flatbedHitchState = state;
                            if (!IsNetworkRunning) return;
                            using (var p = new PacketWriter(PacketType.VehicleTowingSync))
                            {
                                p.WriteByte((byte)TowingSubType.FlatbedState);
                                p.WriteInt32(LocalPlayerId);
                                p.WriteByte(0);
                                p.WriteBoolean(state);
                                ctx.Transport.Send(p);
                            }
                        });
                    }
                }

                if (flatbedTipFsm == null)
                {
                    PlayMakerFSM tip = PlayMakerBridge.FindFsm(flatbedGo, "Tip") ?? PlayMakerBridge.FindFsm(flatbedGo, "Hydraulics");
                    if (tip != null && ctx.Ownership.TryClaim(tip, moduleId, "Flatbed_Tip"))
                    {
                        flatbedTipFsm = tip;
                        HookBinaryFsm(tip, "Up", "Down", () => isSuppressingFlatbed, state =>
                        {
                            flatbedTipState = state;
                            if (!IsNetworkRunning) return;
                            using (var p = new PacketWriter(PacketType.VehicleTowingSync))
                            {
                                p.WriteByte((byte)TowingSubType.FlatbedState);
                                p.WriteInt32(LocalPlayerId);
                                p.WriteByte(1);
                                p.WriteBoolean(state);
                                ctx.Transport.Send(p);
                            }
                        });
                    }
                }
            }
        }

        private void HookGifuHoseAndWinch()
        {
            VehicleCatalogEntry gifu = ControlsHelpers.FindVehicleById(catalog, (int)VehicleSlot.Gifu);
            GameObject gifuGo = gifu?.RootObject ?? GameObject.Find("GIFU(750/450psi)") ?? GameObject.Find("Gifu");
            if (gifuGo == null) return;

            if (activateHoseFsm == null)
            {
                Transform actTr = PlayMakerBridge.FindChildRecursive(gifuGo.transform, "ActivateHose");
                if (actTr != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(actTr.gameObject, "Use");
                    if (fsm != null && ctx.Ownership.TryClaim(fsm, moduleId, "Gifu_ActivateHose"))
                    {
                        activateHoseFsm = fsm;
                        fsm.AddSafeGlobalTransition("MP_SYNC_REMOVE", "Remove part");
                        fsm.AddSafeGlobalTransition("MP_SYNC_ASSEMBLE", "Put hose back");
                        if (fsm.HasState("Remove part"))
                        {
                            fsm.InsertAction("Remove part", () =>
                            {
                                if (!isSuppressingHose)
                                {
                                    lastGifuHoseActivateAction = 1;
                                    SendGifuHosePacket(1);
                                }
                            }, 0, true);
                        }
                        if (fsm.HasState("Put hose back"))
                        {
                            fsm.InsertAction("Put hose back", () =>
                            {
                                if (!isSuppressingHose)
                                {
                                    lastGifuHoseActivateAction = 2;
                                    SendGifuHosePacket(2);
                                }
                            }, 0, true);
                        }
                    }
                }
            }

            if (hoseCouplerFsm == null)
            {
                Transform couplerTr = PlayMakerBridge.FindChildRecursive(gifuGo.transform, "hose coupler(xxxxx)");
                if (couplerTr != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(couplerTr.gameObject, "Attach");
                    if (fsm != null && ctx.Ownership.TryClaim(fsm, moduleId, "Gifu_HoseCoupler"))
                    {
                        hoseCouplerFsm = fsm;
                        fsm.AddSafeGlobalTransition("MP_SYNC_ATTACH", "Attach");
                        fsm.AddSafeGlobalTransition("MP_SYNC_DETACH", "Hose off");
                        if (fsm.HasState("Attach"))
                        {
                            fsm.InsertAction("Attach", () =>
                            {
                                if (!isSuppressingHose)
                                {
                                    lastGifuHoseCouplerAction = 3;
                                    SendGifuHosePacket(3);
                                }
                            }, 0, true);
                        }
                        if (fsm.HasState("Hose off"))
                        {
                            fsm.InsertAction("Hose off", () =>
                            {
                                if (!isSuppressingHose)
                                {
                                    lastGifuHoseCouplerAction = 4;
                                    SendGifuHosePacket(4);
                                }
                            }, 0, true);
                        }
                    }
                }
            }

            // Defensive discovery возможной лебёдки Gifu (в ванильной MSC может отсутствовать)
            if (gifuWinchFsms.Count == 0)
            {
                PlayMakerFSM[] fsms = gifuGo.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < fsms.Length; i++)
                {
                    PlayMakerFSM fsm = fsms[i];
                    if (fsm == null) continue;
                    string n = fsm.gameObject.name;
                    if (!ControlsHelpers.ContainsOrdinalIgnoreCase(n, "winch") &&
                        !ControlsHelpers.ContainsOrdinalIgnoreCase(n, "crane") &&
                        !ControlsHelpers.ContainsOrdinalIgnoreCase(n, "hoist"))
                    {
                        continue;
                    }
                    if (!ctx.Ownership.TryClaim(fsm, moduleId, $"GifuWinch_{n}_{fsm.FsmName}")) continue;

                    fsm.InitializeSafe();
                    gifuWinchFsms.Add(fsm);
                    int keyHash = ControlsHelpers.GetControlKeyHash(fsm);
                    HookGifuWinchFsm(fsm, keyHash);
                }

                if (!winchDiscoveryLogged)
                {
                    winchDiscoveryLogged = true;
                    ctx.Log?.Invoke(gifuWinchFsms.Count > 0
                        ? $"[Gifu] Обнаружены и подключены FSM лебёдки/крана ({gifuWinchFsms.Count})."
                        : "[Gifu] Отдельные FSM лебёдки не обнаружены (используется стандартная сцепка буксировочного троса и шланга).");
                }
            }
        }

        private void HookGifuWinchFsm(PlayMakerFSM fsm, int keyHash)
        {
            if (fsm.FsmStates == null) return;
            for (int i = 0; i < fsm.FsmStates.Length; i++)
            {
                FsmState st = fsm.FsmStates[i];
                if (st == null) continue;
                string stName = st.Name;
                if (stName == "Extend" || stName == "Retract" || stName == "Winch In" || stName == "Winch Out" ||
                    stName == "In" || stName == "Out" || stName == "Attach" || stName == "Detach" || stName == "Stop")
                {
                    string evName = "MP_WINCH_" + stName.ToUpperInvariant().Replace(' ', '_');
                    fsm.AddSafeGlobalTransition(evName, stName);
                    string capturedState = stName;
                    fsm.InsertAction(capturedState, () =>
                    {
                        if (isSuppressingWinch || !IsNetworkRunning) return;
                        using (var p = new PacketWriter(PacketType.VehicleTowingSync))
                        {
                            p.WriteByte((byte)TowingSubType.GifuWinch);
                            p.WriteInt32(LocalPlayerId);
                            p.WriteInt32(keyHash);
                            p.WriteString(capturedState);
                            ctx.Transport.Send(p);
                        }
                    }, 0, true);
                }
            }
        }

        private static void HookBinaryFsm(PlayMakerFSM fsm, string onState, string offState, Func<bool> isSuppressed, Action<bool> sendFn)
        {
            fsm.InitializeSafe();
            if (fsm.HasState(onState))
            {
                fsm.AddSafeGlobalTransition("MP_ON", onState);
                fsm.InsertAction(onState, () =>
                {
                    if (!isSuppressed()) sendFn(true);
                }, 0, true);
            }
            if (fsm.HasState(offState))
            {
                fsm.AddSafeGlobalTransition("MP_OFF", offState);
                fsm.InsertAction(offState, () =>
                {
                    if (!isSuppressed()) sendFn(false);
                }, 0, true);
            }
        }

        private void SendRopePacket(TowingSubType subType, int linkId, string h1Path, string h2Path)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleTowingSync))
            {
                p.WriteByte((byte)subType);
                p.WriteInt32(LocalPlayerId);
                p.WriteInt32(linkId);
                p.WriteString(h1Path ?? string.Empty);
                p.WriteString(h2Path ?? string.Empty);
                ctx.Transport.Send(p);
            }
        }

        private void SendGifuHosePacket(byte action)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleTowingSync))
            {
                p.WriteByte((byte)TowingSubType.GifuHose);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(action);
                ctx.Transport.Send(p);
            }
        }

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.UnreadBytes < 5) return;
            var subType = (TowingSubType)reader.ReadByte();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            switch (subType)
            {
                case TowingSubType.RopeAttach:
                {
                    int linkId = reader.ReadInt32();
                    string p1 = reader.ReadString();
                    string p2 = reader.ReadString();
                    GameObject h1 = FindGameObjectByPath(p1);
                    GameObject h2 = FindGameObjectByPath(p2);
                    RegisterTowLink(linkId, senderId, p1, p2, h1, h2, isLocalFsmOwned: false);
                    break;
                }
                case TowingSubType.RopeDetach:
                case TowingSubType.RopeSnap:
                {
                    int linkId = reader.ReadInt32();
                    RemoveTowLink(linkId, notifyLocalFsm: true, isSnap: subType == TowingSubType.RopeSnap);
                    break;
                }
                case TowingSubType.GifuHose:
                {
                    byte action = reader.ReadByte();
                    ApplyGifuHoseAction(action);
                    break;
                }
                case TowingSubType.CaravanDetach:
                {
                    isCaravanDetached = true;
                    if (caravanDetachFsm != null)
                    {
                        isSuppressingCaravan = true;
                        try { caravanDetachFsm.SendEvent("MP_SYNC_DETACH"); }
                        finally { isSuppressingCaravan = false; }
                    }
                    break;
                }
                case TowingSubType.FlatbedState:
                {
                    byte kind = reader.ReadByte();
                    bool on = reader.ReadBoolean();
                    if (kind == 0) flatbedHitchState = on;
                    else if (kind == 1) flatbedTipState = on;
                    isSuppressingFlatbed = true;
                    try
                    {
                        if (kind == 0 && flatbedHitchFsm != null) flatbedHitchFsm.SendEvent(on ? "MP_ON" : "MP_OFF");
                        else if (kind == 1 && flatbedTipFsm != null) flatbedTipFsm.SendEvent(on ? "MP_ON" : "MP_OFF");
                    }
                    finally { isSuppressingFlatbed = false; }
                    break;
                }
                case TowingSubType.GifuWinch:
                {
                    int keyHash = reader.ReadInt32();
                    string stateName = reader.ReadString();
                    ApplyGifuWinchState(keyHash, stateName);
                    break;
                }
            }
        }

        private void ApplyGifuHoseAction(byte action)
        {
            if (action == 1 || action == 2) lastGifuHoseActivateAction = action;
            else if (action == 3 || action == 4) lastGifuHoseCouplerAction = action;

            if (activateHoseFsm == null || hoseCouplerFsm == null)
            {
                HookGifuHoseAndWinch();
            }

            isSuppressingHose = true;
            try
            {
                switch (action)
                {
                    case 1: activateHoseFsm?.SendEvent("MP_SYNC_REMOVE"); break;
                    case 2: activateHoseFsm?.SendEvent("MP_SYNC_ASSEMBLE"); break;
                    case 3: hoseCouplerFsm?.SendEvent("MP_SYNC_ATTACH"); break;
                    case 4: hoseCouplerFsm?.SendEvent("MP_SYNC_DETACH"); break;
                }
            }
            finally
            {
                isSuppressingHose = false;
            }
        }

        private void ApplyGifuWinchState(int keyHash, string stateName)
        {
            if (string.IsNullOrEmpty(stateName)) return;
            isSuppressingWinch = true;
            try
            {
                string evName = "MP_WINCH_" + stateName.ToUpperInvariant().Replace(' ', '_');
                for (int i = 0; i < gifuWinchFsms.Count; i++)
                {
                    PlayMakerFSM fsm = gifuWinchFsms[i];
                    if (fsm != null && ControlsHelpers.GetControlKeyHash(fsm) == keyHash)
                    {
                        fsm.SendEvent(evName);
                        break;
                    }
                }
            }
            finally
            {
                isSuppressingWinch = false;
            }
        }

        private void RegisterTowLink(
            int linkId,
            int ownerPlayerId,
            string h1Path,
            string h2Path,
            GameObject h1Obj,
            GameObject h2Obj,
            bool isLocalFsmOwned)
        {
            if (activeLinks.ContainsKey(linkId))
            {
                RemoveTowLink(linkId, notifyLocalFsm: false);
            }

            VehicleCatalogEntry vehA = ControlsHelpers.FindVehicleByTransform(catalog, h1Obj != null ? h1Obj.transform : null);
            VehicleCatalogEntry vehB = ControlsHelpers.FindVehicleByTransform(catalog, h2Obj != null ? h2Obj.transform : null);
            ResolveLeadAndTowed(h1Obj, h2Obj, vehA, vehB, out VehicleCatalogEntry leadVeh, out VehicleCatalogEntry towedVeh);

            var link = new TowLinkEntry
            {
                LinkId = linkId,
                OwnerPlayerId = ownerPlayerId,
                Hook1Path = h1Path,
                Hook2Path = h2Path,
                Hook1Obj = h1Obj,
                Hook2Obj = h2Obj,
                LeadVehicle = leadVeh,
                TowedVehicle = towedVeh,
                IsLocalFsmOwned = isLocalFsmOwned
            };

            // Изолированный визуальный трос для удалённого подключения (не затрагивает FPSCamera/TowingRope/Rope локального игрока!)
            if (!isLocalFsmOwned && h1Obj != null && h2Obj != null)
            {
                CreateVisualRope(link);
            }

            activeLinks[linkId] = link;
            RecomputeTowTrainAuthorities();
        }

        private void RemoveTowLink(int linkId, bool notifyLocalFsm, bool isSnap = false)
        {
            if (!activeLinks.TryGetValue(linkId, out TowLinkEntry link)) return;
            activeLinks.Remove(linkId);

            if (notifyLocalFsm && link.IsLocalFsmOwned && localRopeFsm != null)
            {
                isSuppressingRope = true;
                try
                {
                    FsmBool attachedVar = localRopeFsm.FsmVariables.FindFsmBool("Attached");
                    if (attachedVar != null) attachedVar.Value = false;
                    localRopeFsm.SendEvent(isSnap ? "MP_SYNC_SNAP" : "MP_SYNC_DETACH");
                }
                finally
                {
                    isSuppressingRope = false;
                }
                if (activeLocalLinkId == linkId) activeLocalLinkId = 0;
            }

            DestroyLinkObjects(link);
            RecomputeTowTrainAuthorities();
        }

        private void ClearAllTowLinks()
        {
            foreach (var kv in activeLinks)
            {
                DestroyLinkObjects(kv.Value);
            }
            activeLinks.Clear();
        }

        private static void DestroyLinkObjects(TowLinkEntry link)
        {
            if (link == null) return;
            if (link.RemoteJoint != null)
            {
                UnityEngine.Object.Destroy(link.RemoteJoint);
                link.RemoteJoint = null;
            }
            link.JointCreated = false;
            if (link.VisualRopeObj != null)
            {
                UnityEngine.Object.Destroy(link.VisualRopeObj);
                link.VisualRopeObj = null;
                link.VisualLine = null;
            }
        }

        private GameObject FindGameObjectByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            GameObject direct = GameObject.Find(path);
            if (direct != null) return direct;

            if (catalog != null)
            {
                for (int i = 0; i < catalog.Length; i++)
                {
                    VehicleCatalogEntry veh = catalog[i];
                    if (veh == null || veh.RootObject == null) continue;
                    string rootName = veh.RootObject.name;
                    if (string.Equals(path, rootName, StringComparison.Ordinal))
                    {
                        return veh.RootObject;
                    }
                    if (path.Length > rootName.Length &&
                        path.StartsWith(rootName, StringComparison.Ordinal) &&
                        path[rootName.Length] == '/')
                    {
                        string relPath = path.Substring(rootName.Length + 1);
                        Transform found = veh.RootObject.transform.Find(relPath);
                        if (found != null) return found.gameObject;
                    }
                }
            }

            int slash = path.IndexOf('/');
            if (slash > 0 && slash + 1 < path.Length)
            {
                string rootName = path.Substring(0, slash);
                string relPath = path.Substring(slash + 1);
                GameObject rootGo = GameObject.Find(rootName);
                if (rootGo != null)
                {
                    Transform found = rootGo.transform.Find(relPath);
                    if (found != null) return found.gameObject;
                }
            }

            return null;
        }

        private static void CreateVisualRope(TowLinkEntry link)
        {
            var ropeGo = new GameObject($"OSC_RemoteTowRope_{link.LinkId}");
            LineRenderer lr = ropeGo.AddComponent<LineRenderer>();
            lr.SetVertexCount(2);
            lr.SetWidth(0.035f, 0.035f);
            lr.useWorldSpace = true;
            Material mat = new Material(Shader.Find("Diffuse"));
            mat.color = new Color(0.75f, 0.62f, 0.28f, 1f);
            lr.material = mat;
            lr.SetPosition(0, link.Hook1Obj.transform.position);
            lr.SetPosition(1, link.Hook2Obj.transform.position);

            link.VisualRopeObj = ropeGo;
            link.VisualLine = lr;
        }

        private static void ResolveLeadAndTowed(
            GameObject h1,
            GameObject h2,
            VehicleCatalogEntry vehA,
            VehicleCatalogEntry vehB,
            out VehicleCatalogEntry lead,
            out VehicleCatalogEntry towed)
        {
            lead = vehA;
            towed = vehB;
            if (vehA == null || vehB == null || vehA == vehB) return;

            string n1 = h1 != null ? h1.name : string.Empty;
            string n2 = h2 != null ? h2.name : string.Empty;
            if (ControlsHelpers.ContainsOrdinalIgnoreCase(n2, "Rear") && !ControlsHelpers.ContainsOrdinalIgnoreCase(n1, "Rear"))
            {
                lead = vehB;
                towed = vehA;
                return;
            }
            if (ControlsHelpers.ContainsOrdinalIgnoreCase(n1, "Rear") && !ControlsHelpers.ContainsOrdinalIgnoreCase(n2, "Rear"))
            {
                lead = vehA;
                towed = vehB;
                return;
            }
            if (vehA.DriverPlayerId < 0 && vehB.DriverPlayerId >= 0)
            {
                lead = vehB;
                towed = vehA;
            }
        }

        /// <summary>
        /// R5: Определение единого мастера физики для каждой сцепленной пары/поезда буксируемых машин.
        /// Если ведущая машина занята водителем (Хост или Клиент), этот водитель симулирует обе машины динамически
        /// (ForceDynamic = true), а второй пир переводит обе в кинематический режим (ForceKinematic = true),
        /// исключая взрывы ConfigurableJoint о кинематическое тело. При пустой ведущей машине мастер — водитель ведомой или Хост (0).
        /// </summary>
        private void RecomputeTowTrainAuthorities()
        {
            var desired = new Dictionary<byte, AuthoritySnapshot>();
            var vehById = new Dictionary<byte, VehicleCatalogEntry>();
            var adj = new Dictionary<byte, HashSet<byte>>();

            foreach (var kv in activeLinks)
            {
                TowLinkEntry link = kv.Value;
                if (link == null) continue;

                VehicleCatalogEntry lead = link.LeadVehicle;
                VehicleCatalogEntry towed = link.TowedVehicle;

                if (lead != null && lead.RootObject != null)
                {
                    vehById[lead.VehicleId] = lead;
                    if (!adj.ContainsKey(lead.VehicleId)) adj[lead.VehicleId] = new HashSet<byte>();
                }
                if (towed != null && towed.RootObject != null)
                {
                    vehById[towed.VehicleId] = towed;
                    if (!adj.ContainsKey(towed.VehicleId)) adj[towed.VehicleId] = new HashSet<byte>();
                }
                if (lead != null && lead.RootObject != null &&
                    towed != null && towed.RootObject != null &&
                    lead.VehicleId != towed.VehicleId)
                {
                    adj[lead.VehicleId].Add(towed.VehicleId);
                    adj[towed.VehicleId].Add(lead.VehicleId);
                }
            }

            var visited = new HashSet<byte>();
            var queue = new Queue<byte>();
            var component = new List<VehicleCatalogEntry>();

            foreach (var kv in vehById)
            {
                byte startId = kv.Key;
                if (visited.Contains(startId)) continue;

                component.Clear();
                visited.Add(startId);
                queue.Enqueue(startId);

                int masterPlayerId = int.MaxValue;
                while (queue.Count > 0)
                {
                    byte curId = queue.Dequeue();
                    VehicleCatalogEntry curVeh = vehById[curId];
                    component.Add(curVeh);

                    if (curVeh.DriverPlayerId >= 0 && curVeh.DriverPlayerId < masterPlayerId)
                    {
                        masterPlayerId = curVeh.DriverPlayerId;
                    }

                    if (adj.TryGetValue(curId, out var neighbors))
                    {
                        foreach (byte nb in neighbors)
                        {
                            if (!visited.Contains(nb))
                            {
                                visited.Add(nb);
                                queue.Enqueue(nb);
                            }
                        }
                    }
                }

                if (masterPlayerId == int.MaxValue)
                {
                    masterPlayerId = 0; // По умолчанию Хост
                }

                bool localIsMaster = masterPlayerId == LocalPlayerId;
                for (int i = 0; i < component.Count; i++)
                {
                    AddAuthorityCandidate(desired, component[i], masterPlayerId, localIsMaster);
                }
            }

            foreach (var kv in activeLinks)
            {
                TowLinkEntry link = kv.Value;
                if (link == null) continue;

                int linkMaster = 0;
                if (link.LeadVehicle != null && desired.TryGetValue(link.LeadVehicle.VehicleId, out var leadSnap))
                {
                    linkMaster = leadSnap.AuthorityPlayerId;
                }
                else if (link.TowedVehicle != null && desired.TryGetValue(link.TowedVehicle.VehicleId, out var towedSnap))
                {
                    linkMaster = towedSnap.AuthorityPlayerId;
                }

                UpdateLinkJointForAuthority(link, linkMaster == LocalPlayerId);
            }

            // Освобождаем машины, вышедшие из буксировки
            var toRelease = new List<byte>();
            foreach (var kv in publishedAuthorities)
            {
                if (!desired.ContainsKey(kv.Key))
                {
                    toRelease.Add(kv.Key);
                }
            }
            for (int i = 0; i < toRelease.Count; i++)
            {
                byte vid = toRelease[i];
                AuthoritySnapshot prev = publishedAuthorities[vid];
                publishedAuthorities.Remove(vid);
                ctx.Events.Publish(new VehiclePhysicsAuthorityEvent
                {
                    VehicleId = prev.VehicleId,
                    VehicleTransform = prev.VehicleTransform,
                    SourceModuleId = moduleId,
                    OverrideActive = false,
                    AuthorityPlayerId = 0,
                    ForceKinematic = false,
                    ForceDynamic = false,
                    SuppressWorldTransformSync = false
                });
            }

            // Публикуем новые или изменившиеся авторитеты
            foreach (var kv in desired)
            {
                AuthoritySnapshot next = kv.Value;
                if (publishedAuthorities.TryGetValue(kv.Key, out var cur) &&
                    cur.AuthorityPlayerId == next.AuthorityPlayerId &&
                    cur.ForceDynamic == next.ForceDynamic &&
                    cur.ForceKinematic == next.ForceKinematic)
                {
                    continue;
                }

                publishedAuthorities[kv.Key] = next;
                ctx.Events.Publish(new VehiclePhysicsAuthorityEvent
                {
                    VehicleId = next.VehicleId,
                    VehicleTransform = next.VehicleTransform,
                    SourceModuleId = moduleId,
                    OverrideActive = true,
                    AuthorityPlayerId = next.AuthorityPlayerId,
                    ForceKinematic = next.ForceKinematic,
                    ForceDynamic = next.ForceDynamic,
                    SuppressWorldTransformSync = false
                });
            }
        }

        private static void AddAuthorityCandidate(
            Dictionary<byte, AuthoritySnapshot> desired,
            VehicleCatalogEntry veh,
            int masterPlayerId,
            bool localIsMaster)
        {
            if (veh == null || veh.RootObject == null) return;
            desired[veh.VehicleId] = new AuthoritySnapshot
            {
                VehicleId = veh.VehicleId,
                VehicleTransform = veh.RootObject.transform,
                AuthorityPlayerId = masterPlayerId,
                ForceDynamic = localIsMaster,
                ForceKinematic = !localIsMaster
            };
        }

        private void UpdateLinkJointForAuthority(TowLinkEntry link, bool localIsMaster)
        {
            if (link == null) return;

            if (link.IsLocalFsmOwned)
            {
                if (!localIsMaster)
                {
                    if (localRopeFsm != null)
                    {
                        ConfigurableJoint[] joints = localRopeFsm.GetComponents<ConfigurableJoint>();
                        for (int i = 0; i < joints.Length; i++)
                        {
                            if (joints[i] != null) UnityEngine.Object.Destroy(joints[i]);
                        }
                        if (localRopeFsm.FsmVariables != null && localRopeFsm.FsmVariables.ObjectVariables != null)
                        {
                            for (int i = 0; i < localRopeFsm.FsmVariables.ObjectVariables.Length; i++)
                            {
                                FsmObject objVar = localRopeFsm.FsmVariables.ObjectVariables[i];
                                if (objVar != null && objVar.Value is ConfigurableJoint fsmJoint && fsmJoint != null)
                                {
                                    UnityEngine.Object.Destroy(fsmJoint);
                                    objVar.Value = null;
                                }
                            }
                        }
                    }
                    if (link.RemoteJoint != null)
                    {
                        UnityEngine.Object.Destroy(link.RemoteJoint);
                        link.RemoteJoint = null;
                    }
                    link.JointCreated = false;
                    link.VanillaJointCleared = true;
                    return;
                }

                if (!link.VanillaJointCleared)
                {
                    return;
                }
            }

            if (!localIsMaster)
            {
                if (link.RemoteJoint != null)
                {
                    UnityEngine.Object.Destroy(link.RemoteJoint);
                    link.RemoteJoint = null;
                }
                link.JointCreated = false;
                return;
            }

            if (!link.JointCreated && link.RemoteJoint == null && link.Hook1Obj != null && link.Hook2Obj != null)
            {
                Rigidbody rb1 = link.Hook1Obj.GetComponentInParent<Rigidbody>();
                Rigidbody rb2 = link.Hook2Obj.GetComponentInParent<Rigidbody>();
                if (rb1 == null || rb2 == null || rb1 == rb2) return;

                ConfigurableJoint cj = rb1.gameObject.AddComponent<ConfigurableJoint>();
                cj.connectedBody = rb2;
                cj.autoConfigureConnectedAnchor = false;
                cj.anchor = rb1.transform.InverseTransformPoint(link.Hook1Obj.transform.position);
                cj.connectedAnchor = rb2.transform.InverseTransformPoint(link.Hook2Obj.transform.position);
                cj.xMotion = ConfigurableJointMotion.Limited;
                cj.yMotion = ConfigurableJointMotion.Limited;
                cj.zMotion = ConfigurableJointMotion.Limited;
                float dist = Mathf.Clamp(Vector3.Distance(link.Hook1Obj.transform.position, link.Hook2Obj.transform.position), 1.5f, 8.0f);
                cj.linearLimit = new SoftJointLimit { limit = dist };
                cj.breakForce = 45000f;
                cj.breakTorque = 45000f;
                link.RemoteJoint = cj;
                link.JointCreated = true;
            }
        }

        private void UpdateVisualRopesAndCheckSnaps()
        {
            if (activeLinks.Count == 0) return;
            List<int> snappedLinks = null;

            foreach (var kv in activeLinks)
            {
                TowLinkEntry link = kv.Value;
                if (link == null) continue;

                if (link.VisualLine != null && link.Hook1Obj != null && link.Hook2Obj != null)
                {
                    link.VisualLine.SetPosition(0, link.Hook1Obj.transform.position);
                    link.VisualLine.SetPosition(1, link.Hook2Obj.transform.position);
                }

                bool usesManagedJoint = !link.IsLocalFsmOwned || link.VanillaJointCleared;
                bool isLocalMasterForLink = LocalPlayerId == 0;
                if (link.LeadVehicle != null && publishedAuthorities.TryGetValue(link.LeadVehicle.VehicleId, out var auth))
                {
                    isLocalMasterForLink = auth.AuthorityPlayerId == LocalPlayerId;
                }
                else if (link.TowedVehicle != null && publishedAuthorities.TryGetValue(link.TowedVehicle.VehicleId, out var auth2))
                {
                    isLocalMasterForLink = auth2.AuthorityPlayerId == LocalPlayerId;
                }

                // Если наш изолированный ConfigurableJoint оборвался от перегрузки или растяжения, уведомляем напарника
                bool jointBroken = link.JointCreated && link.RemoteJoint == null;
                bool overstretched = link.Hook1Obj != null && link.Hook2Obj != null &&
                    Vector3.Distance(link.Hook1Obj.transform.position, link.Hook2Obj.transform.position) > 10.5f;
                if (usesManagedJoint && isLocalMasterForLink && (jointBroken || overstretched))
                {
                    if (snappedLinks == null) snappedLinks = new List<int>();
                    snappedLinks.Add(link.LinkId);
                }
            }

            if (snappedLinks != null)
            {
                for (int i = 0; i < snappedLinks.Count; i++)
                {
                    int id = snappedLinks[i];
                    RemoveTowLink(id, notifyLocalFsm: true, isSnap: true);
                    if (IsNetworkRunning) SendRopePacket(TowingSubType.RopeSnap, id, string.Empty, string.Empty);
                }
            }
        }

        private void ReleaseAllPhysicsAuthorities()
        {
            if (publishedAuthorities.Count == 0 || ctx?.Events == null) return;
            foreach (var kv in publishedAuthorities)
            {
                AuthoritySnapshot prev = kv.Value;
                ctx.Events.Publish(new VehiclePhysicsAuthorityEvent
                {
                    VehicleId = prev.VehicleId,
                    VehicleTransform = prev.VehicleTransform,
                    SourceModuleId = moduleId,
                    OverrideActive = false,
                    AuthorityPlayerId = 0,
                    ForceKinematic = false,
                    ForceDynamic = false,
                    SuppressWorldTransformSync = false
                });
            }
            publishedAuthorities.Clear();
        }
    }
}
