using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Items
{
    public sealed class ItemsModule : IOscModule
    {
        private sealed class WorldItemEntry
        {
            public int Hash;
            public GameObject GameObject;
            public Rigidbody Rigidbody;
            public float LastRemoteReceiveTime;
        }

        private sealed class HeldRecord
        {
            public int PlayerId;
            public int Hash;
            public GameObject Item;
            public string ItemName;
        }

        private sealed class LocalMovingEntry
        {
            public int Hash;
            public GameObject GameObject;
            public Rigidbody Rigidbody;
            public float StartTime;
            public float GraceDuration;
            public float NextSendTime;
            public float StopAtTime;
            public float StillSeconds;
        }

        private sealed class RemoteMovingEntry
        {
            public int Hash;
            public GameObject GameObject;
            public Rigidbody Rigidbody;
            public Vector3 TargetPosition;
            public Quaternion TargetRotation;
            public float LastPacketTime;
        }

        private sealed class PendingSnapshotTarget
        {
            public int ClientId;
            public float SendAtTime;
        }

        public string ModuleId => "Items";
        public string DisplayName => "Предметы и Инвентарь";
        public string StatusSummary => $"Предметов: {worldItems.Count}, в руках: {heldItems.Count}, летит: {localMoving.Count + remoteMoving.Count}";

        private OscHostContext ctx;
        private BeerAndConsumablesSync beerConsumables;
        private GarageAndToolsSync garageTools;

        private readonly Dictionary<int, WorldItemEntry> worldItems = new Dictionary<int, WorldItemEntry>();
        private readonly Dictionary<int, HeldRecord> heldItems = new Dictionary<int, HeldRecord>();
        private readonly Dictionary<int, LocalMovingEntry> localMoving = new Dictionary<int, LocalMovingEntry>();
        private readonly Dictionary<int, RemoteMovingEntry> remoteMoving = new Dictionary<int, RemoteMovingEntry>();
        private readonly List<PendingSnapshotTarget> pendingSnapshots = new List<PendingSnapshotTarget>();
        private readonly List<int> tempKeys = new List<int>();
        private readonly HashSet<int> cargoCarryAllowedIds = new HashSet<int>();

        private Transform playerTr;
        private Transform itemPivotTr;
        private PlayMakerFSM pickUpFsm;
        private FsmGameObject pickedObjectVar;
        private FsmGameObject pickedToolVar;

        private GameObject lastLocalHeldItem;
        private int lastLocalHeldHash;
        private Vector3 lastLocalPlayerPos;

        private float nextHandSearchTime;
        private float nextMovingScanTime;
        private float nextHeldRefreshTime;
        private bool clientRequestedSnapshot;

        private int LocalPlayerId => (ctx?.Transport != null) ? ctx.Transport.LocalPlayerId : 0;
        private bool IsNetworkRunning => ctx?.Transport != null && ctx.Transport.IsRunning;

        public void Initialize(OscHostContext context)
        {
            ctx = context;
            beerConsumables = new BeerAndConsumablesSync(ctx, ModuleId);
            garageTools = new GarageAndToolsSync(ctx, ModuleId);

            beerConsumables.OnDynamicItemRegistered += HandleDynamicItemRegistered;
            beerConsumables.OnItemRemovedFromWorld += HandleItemRemovedFromWorld;

            ctx.Router.Register(PacketType.ItemPickUp, ModuleId, HandleItemPickUp, relayOnServer: true);
            ctx.Router.Register(PacketType.ItemDrop, ModuleId, HandleItemDrop, relayOnServer: true);
            ctx.Router.Register(PacketType.ItemSync, ModuleId, HandleItemSyncRouter, relayOnServer: true);

            ctx.Events.Subscribe<LocalPlayerTransformUpdatedEvent>(HandleLocalPlayerTransformUpdated);
            ctx.Events.Subscribe<VehicleCargoCarryAllowanceEvent>(HandleVehicleCargoCarryAllowance);

            if (ctx.Transport != null)
            {
                ctx.Transport.OnClientConnected += HandleClientConnected;
                ctx.Transport.OnClientDisconnected += HandleClientDisconnected;
            }
        }

        public void OnMenuLoaded() => ResetWorldState();

        public void OnLevelLoaded()
        {
            ResetWorldState();
            IndexPortableWorldItems();
            beerConsumables.InitializeSceneObjects();
            garageTools.InitializeSceneObjects();
            TryFindPlayerHandReferences();
        }

        public void OnShutdown()
        {
            if (beerConsumables != null)
            {
                beerConsumables.OnDynamicItemRegistered -= HandleDynamicItemRegistered;
                beerConsumables.OnItemRemovedFromWorld -= HandleItemRemovedFromWorld;
            }
            ctx?.Events?.Unsubscribe<LocalPlayerTransformUpdatedEvent>(HandleLocalPlayerTransformUpdated);
            ctx?.Events?.Unsubscribe<VehicleCargoCarryAllowanceEvent>(HandleVehicleCargoCarryAllowance);

            if (ctx?.Transport != null)
            {
                ctx.Transport.OnClientConnected -= HandleClientConnected;
                ctx.Transport.OnClientDisconnected -= HandleClientDisconnected;
            }

            ctx?.Router?.UnregisterModule(ModuleId);
            ctx?.Ownership?.ReleaseModule(ModuleId);
            ResetWorldState();
        }

        private void ResetWorldState()
        {
            foreach (var kv in remoteMoving)
            {
                if (kv.Value.Rigidbody != null)
                {
                    kv.Value.Rigidbody.isKinematic = false;
                    kv.Value.Rigidbody.useGravity = true;
                }
            }

            beerConsumables?.Reset();
            garageTools?.Reset();

            worldItems.Clear();
            heldItems.Clear();
            localMoving.Clear();
            remoteMoving.Clear();
            pendingSnapshots.Clear();
            tempKeys.Clear();
            cargoCarryAllowedIds.Clear();

            playerTr = null;
            itemPivotTr = null;
            pickUpFsm = null;
            pickedObjectVar = null;
            pickedToolVar = null;
            lastLocalHeldItem = null;
            lastLocalHeldHash = 0;
            lastLocalPlayerPos = Vector3.zero;
            nextHandSearchTime = 0f;
            nextMovingScanTime = 0f;
            nextHeldRefreshTime = 0f;
            clientRequestedSnapshot = false;
        }

        public void OnUpdate()
        {
            if ((pickUpFsm == null || itemPivotTr == null) && Time.time >= nextHandSearchTime)
            {
                nextHandSearchTime = Time.time + 1.0f;
                TryFindPlayerHandReferences();
            }

            if (lastLocalPlayerPos == Vector3.zero && playerTr != null)
            {
                lastLocalPlayerPos = playerTr.position;
            }

            UpdateLocalHandGrabbing();
            beerConsumables.TickUpdate(lastLocalPlayerPos);
            garageTools.TickUpdate();

            if (IsNetworkRunning)
            {
                if (Time.time >= nextMovingScanTime)
                {
                    nextMovingScanTime = Time.time + 0.15f;
                    ScanNearbyMovingItems();
                }

                UpdateLocalMovingPhysics();
                UpdateRemoteInterpolation();
                ProcessPendingSnapshots();

                if (Time.time >= nextHeldRefreshTime)
                {
                    nextHeldRefreshTime = Time.time + 1.0f;
                    RefreshRemoteHeldItemsOnPuppets();
                }

                if (ctx.Transport.IsClient && !clientRequestedSnapshot)
                {
                    clientRequestedSnapshot = true;
                    SendSnapshotRequest();
                }
            }
            else
            {
                UpdateRemoteInterpolation();
            }
        }

        public void OnFixedUpdate() { }
        public void OnLateUpdate() { }

        #region Indexing & Player Hand Lookup

        private void IndexPortableWorldItems()
        {
            var rbs = Resources.FindObjectsOfTypeAll<Rigidbody>();
            int count = 0;
            for (int i = 0; i < rbs.Length; i++)
            {
                Rigidbody rb = rbs[i];
                if (rb == null || !BeerAndConsumablesSync.IsValidSceneObject(rb.gameObject)) continue;
                GameObject go = rb.gameObject;

                if (ItemValidator.IsSpecialCarryObject(go, IsCargoCarryAllowed(go))) continue;
                if (!ItemValidator.IsValidPortableItem(go, ctx.Ownership, ModuleId, IsCargoCarryAllowed(go))) continue;

                int hash = SceneHasher.GetHash(go);
                if (!worldItems.ContainsKey(hash))
                {
                    worldItems[hash] = new WorldItemEntry
                    {
                        Hash = hash,
                        GameObject = go,
                        Rigidbody = rb
                    };
                    count++;
                }
            }

            ctx.Log?.Invoke($"[Предметы] Проиндексировано переносимых объектов: {count}");
        }

        private void HandleDynamicItemRegistered(GameObject go, int hash)
        {
            if (go == null || hash == 0) return;
            if (ItemValidator.IsSpecialCarryObject(go)) return;
            if (!ItemValidator.IsValidPortableItem(go, ctx.Ownership, ModuleId)) return;

            Rigidbody rb = go.GetComponent<Rigidbody>();
            worldItems[hash] = new WorldItemEntry
            {
                Hash = hash,
                GameObject = go,
                Rigidbody = rb
            };

            if (rb != null && IsNetworkRunning)
            {
                StartLocalMovingTracker(hash, go, rb, graceDuration: 0.85f, maxDuration: 8.0f);
            }
        }

        private void HandleItemRemovedFromWorld(GameObject go, int hash)
        {
            worldItems.Remove(hash);
            localMoving.Remove(hash);
            remoteMoving.Remove(hash);
        }

        private void TryFindPlayerHandReferences()
        {
            GameObject playerObj = GameObject.Find("PLAYER") ?? GameObject.FindWithTag("Player");
            if (playerObj == null) return;

            playerTr = playerObj.transform;
            if (itemPivotTr == null)
            {
                itemPivotTr = PlayMakerBridge.FindChildRecursive(playerTr, "ItemPivot");
            }

            if (pickUpFsm == null)
            {
                var fsms = playerObj.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < fsms.Length; i++)
                {
                    PlayMakerFSM f = fsms[i];
                    if (f != null && f.FsmName == "PickUp")
                    {
                        FsmGameObject pickedVar = f.FsmVariables.FindFsmGameObject("PickedObject");
                        if (pickedVar != null)
                        {
                            pickUpFsm = f;
                            pickedObjectVar = pickedVar;
                            pickedToolVar = f.FsmVariables.FindFsmGameObject("PickedTool");
                            break;
                        }
                    }
                }
            }
        }

        private GameObject GetCurrentHeldItem()
        {
            if (pickedObjectVar != null && pickedObjectVar.Value != null) return pickedObjectVar.Value;
            if (pickedToolVar != null && pickedToolVar.Value != null) return pickedToolVar.Value;
            if (itemPivotTr != null && itemPivotTr.childCount > 0)
            {
                Transform child = itemPivotTr.GetChild(0);
                if (child != null && child.gameObject.activeSelf) return child.gameObject;
            }
            return null;
        }

        #endregion

        #region PickUp / Drop & Held Bridges

        private void UpdateLocalHandGrabbing()
        {
            GameObject current = GetCurrentHeldItem();

            if (lastLocalHeldItem == null && current != null)
            {
                bool allowParentedCarry = IsCargoCarryAllowed(current);
                if (!ItemValidator.IsValidPortableItem(current, ctx.Ownership, ModuleId, allowParentedCarry)) return;

                int hash = SceneHasher.GetHash(current);
                lastLocalHeldItem = current;
                lastLocalHeldHash = hash;

                heldItems[LocalPlayerId] = new HeldRecord
                {
                    PlayerId = LocalPlayerId,
                    Hash = hash,
                    Item = current,
                    ItemName = current.name
                };

                localMoving.Remove(hash);
                remoteMoving.Remove(hash);

                if (ItemValidator.IsSpecialCarryObject(current, allowParentedCarry))
                {
                    ctx.Events.Publish(new SpecialObjectCarryEvent
                    {
                        PlayerId = LocalPlayerId,
                        IsLocal = true,
                        IsCarried = true,
                        TargetObject = current
                    });
                }

                if (IsNetworkRunning)
                {
                    using (var p = new PacketWriter(PacketType.ItemPickUp))
                    {
                        p.WriteInt32(hash);
                        p.WriteInt32(LocalPlayerId);
                        p.WriteString(current.name);
                        ctx.Transport.Send(p);
                    }
                }
            }
            else if (lastLocalHeldItem != null && current == null)
            {
                GameObject dropped = lastLocalHeldItem;
                int droppedHash = lastLocalHeldHash;

                lastLocalHeldItem = null;
                lastLocalHeldHash = 0;
                heldItems.Remove(LocalPlayerId);

                if (dropped == null) return;

                if (dropped.transform.parent != null && ItemValidator.IsHandParent(dropped.transform.parent))
                {
                    dropped.transform.SetParent(null, true);
                }

                if (dropped.transform.localScale.sqrMagnitude > 3.5f || dropped.transform.localScale.sqrMagnitude < 0.2f)
                {
                    dropped.transform.localScale = Vector3.one;
                }

                bool isSpecialCarry = ItemValidator.IsSpecialCarryObject(dropped, IsCargoCarryAllowed(dropped));
                Rigidbody rb = dropped.GetComponent<Rigidbody>();
                Vector3 vel = Vector3.zero;
                Vector3 angVel = Vector3.zero;

                if (isSpecialCarry)
                {
                    ctx.Events.Publish(new SpecialObjectCarryEvent
                    {
                        PlayerId = LocalPlayerId,
                        IsLocal = true,
                        IsCarried = false,
                        TargetObject = dropped
                    });
                    if (rb != null)
                    {
                        vel = rb.velocity;
                        angVel = rb.angularVelocity;
                    }
                }
                else
                {
                    // Не отключаем и не изменяем коллайдеры транспорта; восстанавливаем только сам брошенный предмет
                    if (!ItemValidator.IsVehicleOrAttachedPart(dropped) && rb != null)
                    {
                        rb.isKinematic = false;
                        rb.useGravity = true;
                        rb.WakeUp();
                        vel = rb.velocity;
                        angVel = rb.angularVelocity;
                        StartLocalMovingTracker(droppedHash, dropped, rb, graceDuration: 0.85f, maxDuration: 10.0f);
                    }
                }

                if (IsNetworkRunning)
                {
                    using (var p = new PacketWriter(PacketType.ItemDrop))
                    {
                        p.WriteInt32(droppedHash);
                        p.WriteInt32(LocalPlayerId);
                        p.WriteVector3(dropped.transform.position);
                        p.WriteQuaternion(dropped.transform.rotation);
                        p.WriteVector3(vel);
                        p.WriteVector3(angVel);
                        ctx.Transport.Send(p);
                    }
                }
            }
        }

        private void HandleItemPickUp(PacketReader reader, int connectionId)
        {
            int itemHash = reader.ReadInt32();
            int playerId = reader.ReadInt32();
            string itemName = reader.UnreadBytes > 0 ? reader.ReadString() : string.Empty;

            if (playerId == LocalPlayerId) return;

            localMoving.Remove(itemHash);
            remoteMoving.Remove(itemHash);

            GameObject item = ResolveItemObject(itemHash, itemName, playerId);
            heldItems[playerId] = new HeldRecord
            {
                PlayerId = playerId,
                Hash = itemHash,
                Item = item,
                ItemName = itemName
            };

            if (item == null) return;

            if (ItemValidator.IsSpecialCarryObject(item, IsCargoCarryAllowed(item)))
            {
                ctx.Events.Publish(new SpecialObjectCarryEvent
                {
                    PlayerId = playerId,
                    IsLocal = false,
                    IsCarried = true,
                    TargetObject = item
                });
            }

            ctx.Events.Publish(new PlayerHeldItemEvent
            {
                PlayerId = playerId,
                IsHolding = true,
                ItemObject = item,
                ItemHash = itemHash
            });
        }

        private void HandleItemDrop(PacketReader reader, int connectionId)
        {
            int itemHash = reader.ReadInt32();
            int playerId = reader.ReadInt32();
            Vector3 pos = reader.ReadVector3();
            Quaternion rot = reader.ReadQuaternion();
            Vector3 vel = reader.ReadVector3();
            Vector3 angVel = reader.ReadVector3();

            if (playerId == LocalPlayerId) return;

            HeldRecord prevHeld = null;
            if (heldItems.TryGetValue(playerId, out prevHeld))
            {
                heldItems.Remove(playerId);
            }

            localMoving.Remove(itemHash);

            GameObject item = (prevHeld != null && prevHeld.Item != null && prevHeld.Hash == itemHash)
                ? prevHeld.Item
                : ResolveItemObject(itemHash, prevHeld != null ? prevHeld.ItemName : string.Empty, playerId, pos);

            if (item != null)
            {
                ctx.Events.Publish(new PlayerHeldItemEvent
                {
                    PlayerId = playerId,
                    IsHolding = false,
                    ItemObject = item,
                    ItemHash = itemHash
                });

                if (item.transform.parent != null && ItemValidator.IsHandParent(item.transform.parent))
                {
                    item.transform.SetParent(null, true);
                }

                if (item.transform.localScale.sqrMagnitude > 3.5f || item.transform.localScale.sqrMagnitude < 0.2f)
                {
                    item.transform.localScale = Vector3.one;
                }

                item.transform.position = pos;
                item.transform.rotation = rot;

                bool isSpecial = ItemValidator.IsSpecialCarryObject(item, IsCargoCarryAllowed(item));
                Rigidbody rb = item.GetComponent<Rigidbody>();

                if (isSpecial)
                {
                    ctx.Events.Publish(new SpecialObjectCarryEvent
                    {
                        PlayerId = playerId,
                        IsLocal = false,
                        IsCarried = false,
                        TargetObject = item
                    });
                    if (rb != null)
                    {
                        rb.isKinematic = false;
                        rb.useGravity = true;
                        rb.velocity = vel;
                        rb.angularVelocity = angVel;
                    }
                }
                else
                {
                    if (rb != null)
                    {
                        rb.isKinematic = true;
                        rb.useGravity = false;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }

                    remoteMoving[itemHash] = new RemoteMovingEntry
                    {
                        Hash = itemHash,
                        GameObject = item,
                        Rigidbody = rb,
                        TargetPosition = pos,
                        TargetRotation = rot,
                        LastPacketTime = Time.time
                    };
                }
            }
        }

        private void RefreshRemoteHeldItemsOnPuppets()
        {
            if (heldItems.Count == 0) return;

            foreach (var kv in heldItems)
            {
                HeldRecord hr = kv.Value;
                if (hr == null || hr.PlayerId == LocalPlayerId) continue;

                if (hr.Item == null)
                {
                    hr.Item = ResolveItemObject(hr.Hash, hr.ItemName, hr.PlayerId);
                }
                if (hr.Item == null) continue;

                if (hr.Item.transform.parent == null || hr.Item.transform.parent.name != "ItemHolder")
                {
                    ctx.Events.Publish(new PlayerHeldItemEvent
                    {
                        PlayerId = hr.PlayerId,
                        IsHolding = true,
                        ItemObject = hr.Item,
                        ItemHash = hr.Hash
                    });
                }
            }
        }

        #endregion

        #region Moving-Only Physics Sync (velocity > 0.01f)

        private void ScanNearbyMovingItems()
        {
            Vector3 center = lastLocalPlayerPos;
            if (center == Vector3.zero && playerTr != null) center = playerTr.position;
            if (center == Vector3.zero) return;

            Collider[] cols = Physics.OverlapSphere(center, 8.0f);
            for (int i = 0; i < cols.Length; i++)
            {
                Collider col = cols[i];
                if (col == null) continue;
                Rigidbody rb = col.attachedRigidbody;
                if (rb == null || rb.isKinematic) continue;

                // Передаём координаты ТОЛЬКО для реально движущихся предметов (|v| > 0.01 м/с)
                if (rb.velocity.sqrMagnitude <= 0.0001f && rb.angularVelocity.sqrMagnitude <= 0.0025f) continue;

                GameObject go = rb.gameObject;
                if (ItemValidator.IsSpecialCarryObject(go, IsCargoCarryAllowed(go))) continue;
                if (!ItemValidator.IsValidPortableItem(go, ctx.Ownership, ModuleId, IsCargoCarryAllowed(go))) continue;

                int hash = SceneHasher.GetHash(go);
                if (IsItemHeld(hash) || localMoving.ContainsKey(hash) || remoteMoving.ContainsKey(hash)) continue;

                if (worldItems.TryGetValue(hash, out var entry))
                {
                    if ((Time.time - entry.LastRemoteReceiveTime) < 1.0f) continue;
                }
                else
                {
                    worldItems[hash] = new WorldItemEntry
                    {
                        Hash = hash,
                        GameObject = go,
                        Rigidbody = rb
                    };
                }

                StartLocalMovingTracker(hash, go, rb, graceDuration: 0f, maxDuration: 8.0f);
            }
        }

        private void StartLocalMovingTracker(int hash, GameObject go, Rigidbody rb, float graceDuration, float maxDuration)
        {
            if (go == null || rb == null || hash == 0) return;
            localMoving[hash] = new LocalMovingEntry
            {
                Hash = hash,
                GameObject = go,
                Rigidbody = rb,
                StartTime = Time.time,
                GraceDuration = graceDuration,
                NextSendTime = 0f,
                StopAtTime = Time.time + maxDuration,
                StillSeconds = 0f
            };
        }

        private void UpdateLocalMovingPhysics()
        {
            if (localMoving.Count == 0) return;

            tempKeys.Clear();
            foreach (int k in localMoving.Keys) tempKeys.Add(k);

            float now = Time.time;
            float dt = Time.deltaTime;

            for (int i = 0; i < tempKeys.Count; i++)
            {
                int hash = tempKeys[i];
                if (!localMoving.TryGetValue(hash, out var entry)) continue;

                if (entry.GameObject == null || entry.Rigidbody == null || ItemValidator.IsVehicleOrAttachedPart(entry.GameObject))
                {
                    localMoving.Remove(hash);
                    continue;
                }

                bool inGrace = (now - entry.StartTime) < entry.GraceDuration;
                bool isMoving = entry.Rigidbody.velocity.sqrMagnitude > 0.0001f
                    || entry.Rigidbody.angularVelocity.sqrMagnitude > 0.0025f;

                if (!inGrace)
                {
                    if (isMoving) entry.StillSeconds = 0f;
                    else entry.StillSeconds += dt;

                    if (entry.StillSeconds >= 0.45f || now >= entry.StopAtTime)
                    {
                        SendTransformMove(entry.Hash, entry.GameObject.transform.position, entry.GameObject.transform.rotation, isSleeping: true);
                        entry.Rigidbody.isKinematic = false;
                        entry.Rigidbody.useGravity = true;
                        entry.Rigidbody.Sleep();
                        localMoving.Remove(hash);
                        continue;
                    }
                }

                if ((inGrace || isMoving) && now >= entry.NextSendTime)
                {
                    entry.NextSendTime = now + 0.05f; // 20 Гц только пока предмет движется
                    SendTransformMove(entry.Hash, entry.GameObject.transform.position, entry.GameObject.transform.rotation, isSleeping: false);
                }
            }
        }

        private void UpdateRemoteInterpolation()
        {
            if (remoteMoving.Count == 0) return;

            tempKeys.Clear();
            foreach (int k in remoteMoving.Keys) tempKeys.Add(k);

            float now = Time.time;
            float t = Mathf.Clamp01(Time.deltaTime * 25f);

            for (int i = 0; i < tempKeys.Count; i++)
            {
                int hash = tempKeys[i];
                if (!remoteMoving.TryGetValue(hash, out var entry)) continue;

                if (entry.GameObject == null)
                {
                    remoteMoving.Remove(hash);
                    continue;
                }

                if ((now - entry.LastPacketTime) > 2.0f)
                {
                    entry.GameObject.transform.position = entry.TargetPosition;
                    entry.GameObject.transform.rotation = entry.TargetRotation;
                    if (entry.Rigidbody != null)
                    {
                        entry.Rigidbody.isKinematic = false;
                        entry.Rigidbody.useGravity = true;
                        entry.Rigidbody.WakeUp();
                    }
                    remoteMoving.Remove(hash);
                    continue;
                }

                entry.GameObject.transform.position = Vector3.Lerp(entry.GameObject.transform.position, entry.TargetPosition, t);
                entry.GameObject.transform.rotation = Quaternion.Slerp(entry.GameObject.transform.rotation, entry.TargetRotation, t);
            }
        }

        private void SendTransformMove(int hash, Vector3 pos, Quaternion rot, bool isSleeping)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.ItemSync))
            {
                p.WriteByte((byte)ItemSubPacket.TransformMove);
                p.WriteInt32(LocalPlayerId);
                p.WriteInt32(hash);
                p.WriteVector3(pos);
                p.WriteQuaternion(rot);
                p.WriteBoolean(isSleeping);
                ctx.Transport.Send(p);
            }
        }

        private void HandleTransformMovePacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            int itemHash = reader.ReadInt32();
            Vector3 pos = reader.ReadVector3();
            Quaternion rot = reader.ReadQuaternion();
            bool isSleeping = reader.ReadBoolean();

            if (senderId == LocalPlayerId || itemHash == lastLocalHeldHash) return;

            if (localMoving.ContainsKey(itemHash))
            {
                if (ctx.Transport.IsServer) return;
                localMoving.Remove(itemHash);
            }

            GameObject item = ResolveItemObject(itemHash, string.Empty, senderId, pos);
            if (item == null || ItemValidator.IsSpecialCarryObject(item, IsCargoCarryAllowed(item)) || ItemValidator.IsVehicleOrAttachedPart(item)) return;

            if (worldItems.TryGetValue(itemHash, out var wEntry))
            {
                wEntry.LastRemoteReceiveTime = Time.time;
            }

            if (item.transform.localScale.sqrMagnitude > 3.5f || item.transform.localScale.sqrMagnitude < 0.2f)
            {
                item.transform.localScale = Vector3.one;
            }

            if (isSleeping)
            {
                remoteMoving.Remove(itemHash);
                item.transform.position = pos;
                item.transform.rotation = rot;

                Rigidbody rb = item.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.useGravity = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.Sleep();
                }
            }
            else
            {
                if (!remoteMoving.TryGetValue(itemHash, out var entry))
                {
                    Rigidbody rb = item.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = true;
                        rb.useGravity = false;
                    }
                    entry = new RemoteMovingEntry
                    {
                        Hash = itemHash,
                        GameObject = item,
                        Rigidbody = rb
                    };
                    remoteMoving[itemHash] = entry;
                }

                entry.TargetPosition = pos;
                entry.TargetRotation = rot;
                entry.LastPacketTime = Time.time;
            }
        }

        #endregion

        #region ItemSync Router & Late-Join Snapshot

        private void HandleItemSyncRouter(PacketReader reader, int connectionId)
        {
            if (reader.UnreadBytes < 1) return;
            var sub = (ItemSubPacket)reader.ReadByte();

            switch (sub)
            {
                case ItemSubPacket.TransformMove:
                    HandleTransformMovePacket(reader);
                    break;
                case ItemSubPacket.BeerCaseDrink:
                    beerConsumables.HandleBeerCaseDrinkPacket(reader);
                    break;
                case ItemSubPacket.ConsumableAction:
                    beerConsumables.HandleConsumableActionPacket(reader);
                    break;
                case ItemSubPacket.JerryCanState:
                    garageTools.HandleJerryCanStatePacket(reader);
                    break;
                case ItemSubPacket.ToolSetState:
                    garageTools.HandleToolSetStatePacket(reader);
                    break;
                case ItemSubPacket.EquipmentState:
                    garageTools.HandleEquipmentStatePacket(reader);
                    break;
                case ItemSubPacket.SpawnedItem:
                    beerConsumables.HandleSpawnedItemPacket(reader);
                    break;
                case ItemSubPacket.ShoppingBagGarbage:
                    beerConsumables.HandleShoppingBagGarbagePacket(reader);
                    break;
                case ItemSubPacket.WorldSnapshot:
                    HandleWorldSnapshotPacket(reader);
                    break;
                case ItemSubPacket.SnapshotRequest:
                    if (ctx.Transport != null && ctx.Transport.IsServer)
                    {
                        SendWorldSnapshot(connectionId);
                    }
                    break;
            }
        }

        private void SendSnapshotRequest()
        {
            if (!IsNetworkRunning || ctx.Transport.IsServer) return;
            using (var p = new PacketWriter(PacketType.ItemSync))
            {
                p.WriteByte((byte)ItemSubPacket.SnapshotRequest);
                ctx.Transport.Send(p);
            }
        }

        private void SendWorldSnapshot(int targetClientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            var snapshotItems = new List<WorldItemEntry>();
            foreach (var kv in worldItems)
            {
                WorldItemEntry e = kv.Value;
                if (e.GameObject == null || !e.GameObject.activeInHierarchy) continue;
                if (IsItemHeld(e.Hash)) continue;
                if (ItemValidator.IsVehicleOrAttachedPart(e.GameObject)) continue;
                snapshotItems.Add(e);
            }

            using (var p = new PacketWriter(PacketType.ItemSync))
            {
                p.WriteByte((byte)ItemSubPacket.WorldSnapshot);

                // 1. Удерживаемые в руках предметы
                p.WriteInt32(heldItems.Count);
                foreach (var kv in heldItems)
                {
                    HeldRecord hr = kv.Value;
                    p.WriteInt32(hr.PlayerId);
                    p.WriteInt32(hr.Hash);
                    p.WriteString(hr.ItemName);
                }

                // 2. Координаты и повороты всех лежащих переносимых предметов мира
                p.WriteInt32(snapshotItems.Count);
                for (int i = 0; i < snapshotItems.Count; i++)
                {
                    WorldItemEntry e = snapshotItems[i];
                    p.WriteInt32(e.Hash);
                    p.WriteVector3(e.GameObject.transform.position);
                    p.WriteQuaternion(e.GameObject.transform.rotation);
                }

                // 3. Пивные ящики и распакованные пакеты
                beerConsumables.WriteSnapshot(p);

                // 4. Канистры, наборы ключей, домкрат и кран двигателя
                garageTools.WriteSnapshot(p);

                ctx.Transport.SendTo(p, targetClientId);
            }

            ctx.Log?.Invoke($"[Предметы/Late-Join] Отправлен снимок мира клиенту #{targetClientId} (в руках: {heldItems.Count}, предметов: {snapshotItems.Count})");
        }

        private void HandleWorldSnapshotPacket(PacketReader reader)
        {
            if (ctx.Transport != null && ctx.Transport.IsServer) return;

            int heldCount = reader.ReadInt32();
            for (int i = 0; i < heldCount; i++)
            {
                int playerId = reader.ReadInt32();
                int itemHash = reader.ReadInt32();
                string itemName = reader.ReadString();
                if (playerId == LocalPlayerId) continue;

                GameObject item = ResolveItemObject(itemHash, itemName, playerId);
                heldItems[playerId] = new HeldRecord
                {
                    PlayerId = playerId,
                    Hash = itemHash,
                    Item = item,
                    ItemName = itemName
                };

                if (item != null)
                {
                    if (ItemValidator.IsSpecialCarryObject(item, IsCargoCarryAllowed(item)))
                    {
                        ctx.Events.Publish(new SpecialObjectCarryEvent
                        {
                            PlayerId = playerId,
                            IsLocal = false,
                            IsCarried = true,
                            TargetObject = item
                        });
                    }
                    ctx.Events.Publish(new PlayerHeldItemEvent
                    {
                        PlayerId = playerId,
                        IsHolding = true,
                        ItemObject = item,
                        ItemHash = itemHash
                    });
                }
            }

            int itemCount = reader.ReadInt32();
            int applied = 0;
            for (int i = 0; i < itemCount; i++)
            {
                int hash = reader.ReadInt32();
                Vector3 pos = reader.ReadVector3();
                Quaternion rot = reader.ReadQuaternion();

                if (hash == lastLocalHeldHash || IsItemHeld(hash)) continue;

                GameObject item = SceneHasher.FindObjectByHash(hash);
                if (item == null || ItemValidator.IsVehicleOrAttachedPart(item)) continue;

                item.transform.position = pos;
                item.transform.rotation = rot;
                if (item.transform.localScale.sqrMagnitude > 3.5f || item.transform.localScale.sqrMagnitude < 0.2f)
                {
                    item.transform.localScale = Vector3.one;
                }

                Rigidbody rb = item.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.useGravity = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.Sleep();
                }
                applied++;
            }

            beerConsumables.ApplySnapshot(reader);
            garageTools.ApplySnapshot(reader);

            ctx.Log?.Invoke($"[Предметы/Late-Join] Применён снимок мира (в руках: {heldCount}, предметов: {applied}/{itemCount})");
        }

        private void HandleLocalPlayerTransformUpdated(LocalPlayerTransformUpdatedEvent evt)
        {
            lastLocalPlayerPos = evt.Position;
        }

        private void HandleVehicleCargoCarryAllowance(VehicleCargoCarryAllowanceEvent evt)
        {
            if (evt.TargetObject == null) return;
            int instanceId = evt.TargetObject.GetInstanceID();
            if (evt.AllowCarryWhenParented)
            {
                cargoCarryAllowedIds.Add(instanceId);
            }
            else
            {
                cargoCarryAllowedIds.Remove(instanceId);
            }
        }

        private bool IsCargoCarryAllowed(GameObject go)
        {
            return go != null && cargoCarryAllowedIds.Contains(go.GetInstanceID());
        }

        private void HandleClientConnected(int clientId)
        {
            if (ctx.Transport == null) return;

            if (ctx.Transport.IsServer)
            {
                SendWorldSnapshot(clientId);
                pendingSnapshots.RemoveAll(s => s.ClientId == clientId);
                pendingSnapshots.Add(new PendingSnapshotTarget { ClientId = clientId, SendAtTime = Time.time + 2.0f });
            }
            else if (ctx.Transport.IsClient)
            {
                clientRequestedSnapshot = true;
                SendSnapshotRequest();
            }
        }

        private void HandleClientDisconnected(int clientId)
        {
            int targetId = (ctx.Transport != null && ctx.Transport.IsClient) ? 0 : clientId;
            pendingSnapshots.RemoveAll(s => s.ClientId == clientId);

            if (heldItems.TryGetValue(targetId, out var hr))
            {
                heldItems.Remove(targetId);
                if (hr.Item != null)
                {
                    ctx.Events.Publish(new PlayerHeldItemEvent
                    {
                        PlayerId = targetId,
                        IsHolding = false,
                        ItemObject = hr.Item,
                        ItemHash = hr.Hash
                    });
                    if (ItemValidator.IsSpecialCarryObject(hr.Item, IsCargoCarryAllowed(hr.Item)))
                    {
                        ctx.Events.Publish(new SpecialObjectCarryEvent
                        {
                            PlayerId = targetId,
                            IsLocal = false,
                            IsCarried = false,
                            TargetObject = hr.Item
                        });
                    }
                }
            }

            if (ctx.Transport != null && ctx.Transport.IsClient)
            {
                clientRequestedSnapshot = false;
            }
        }

        private void ProcessPendingSnapshots()
        {
            if (!ctx.Transport.IsServer || pendingSnapshots.Count == 0) return;

            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (Time.time >= pendingSnapshots[i].SendAtTime)
                {
                    SendWorldSnapshot(pendingSnapshots[i].ClientId);
                    pendingSnapshots.RemoveAt(i);
                }
            }
        }

        private bool IsItemHeld(int hash)
        {
            foreach (var kv in heldItems)
            {
                if (kv.Value != null && kv.Value.Hash == hash) return true;
            }
            return false;
        }

        private GameObject ResolveItemObject(int itemHash, string itemName, int playerId, Vector3 fallbackPos = default(Vector3))
        {
            GameObject found = SceneHasher.FindObjectByHash(itemHash);
            if (found != null) return found;

            if (!string.IsNullOrEmpty(itemName) && itemName.StartsWith("JONNEZ", StringComparison.OrdinalIgnoreCase))
            {
                GameObject jonnez = GameObject.Find("JONNEZ ES(Clone)") ?? GameObject.Find("JONNEZ ES");
                if (jonnez != null)
                {
                    SceneHasher.RegisterDynamicObject(itemHash, jonnez);
                    return jonnez;
                }
            }

            Vector3 searchPos = fallbackPos;
            if (searchPos == Vector3.zero)
            {
                GameObject puppetRoot = GameObject.Find(playerId == 0 ? "RemotePuppet_Host" : $"RemotePuppet_Client_{playerId}");
                if (puppetRoot != null) searchPos = puppetRoot.transform.position;
            }

            if (searchPos != Vector3.zero && !string.IsNullOrEmpty(itemName))
            {
                Collider[] cols = Physics.OverlapSphere(searchPos, 4.0f);
                for (int i = 0; i < cols.Length; i++)
                {
                    if (cols[i] == null || cols[i].gameObject == null) continue;
                    GameObject cand = cols[i].attachedRigidbody != null ? cols[i].attachedRigidbody.gameObject : cols[i].gameObject;
                    if (cand == null) continue;
                    if (cand.name.StartsWith(itemName, StringComparison.OrdinalIgnoreCase) ||
                        itemName.StartsWith(cand.name, StringComparison.OrdinalIgnoreCase))
                    {
                        bool allowCarry = IsCargoCarryAllowed(cand);
                        if (!ItemValidator.IsVehicleOrAttachedPart(cand, allowCarry) || ItemValidator.IsSpecialCarryObject(cand, allowCarry))
                        {
                            SceneHasher.RegisterDynamicObject(itemHash, cand);
                            return cand;
                        }
                    }
                }
            }

            return null;
        }

        #endregion
    }
}
