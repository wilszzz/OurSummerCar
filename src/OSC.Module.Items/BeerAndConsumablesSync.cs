using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Items
{
    public sealed class BeerAndConsumablesSync
    {
        private sealed class CreatorEntry
        {
            public string Name;
            public PlayMakerFSM Fsm;
            public FsmString NewItemIdVar;
            public FsmGameObject NewVar;
            public FsmEvent SpawnEvent;
            public bool DoSync = true;
            public bool IsShopbag;
            public GameObject LastProcessedObject;
        }

        private sealed class TrackedBeerCase
        {
            public int Hash;
            public GameObject CaseObject;
            public PlayMakerFSM Fsm;
            public Transform BottlesParent;
            public FsmInt DestroyedBottlesVar;
            public bool IsHooked;
        }

        private sealed class TrackedShoppingBag
        {
            public int Hash;
            public GameObject BagObject;
            public PlayMakerFSM Fsm;
            public FsmGameObject CurrentBagVar;
            public bool IsHooked;
        }

        private sealed class TrackedConsumable
        {
            public int Hash;
            public GameObject GameObject;
            public PlayMakerFSM Fsm;
            public string Kind;
            public FsmInt ConsumedVar;
            public FsmFloat FluidVar;
            public float LastSyncedFluid;
            public float SpawnTime;
            public bool IsHooked;
        }

        private sealed class ActiveBottleTimer
        {
            public GameObject BottleObject;
            public GameObject DrinksRootObject;
            public float DeactivateAt;
        }

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly List<CreatorEntry> creators = new List<CreatorEntry>();
        private readonly Dictionary<int, TrackedBeerCase> beerCases = new Dictionary<int, TrackedBeerCase>();
        private readonly Dictionary<int, TrackedShoppingBag> shoppingBags = new Dictionary<int, TrackedShoppingBag>();
        private readonly HashSet<int> unpackedBags = new HashSet<int>();
        private readonly Dictionary<int, TrackedConsumable> consumables = new Dictionary<int, TrackedConsumable>();

        private readonly HashSet<int> suppressedBeerCases = new HashSet<int>();
        private readonly HashSet<int> suppressedConsumables = new HashSet<int>();
        private readonly Dictionary<int, ActiveBottleTimer> activePuppetBottles = new Dictionary<int, ActiveBottleTimer>();
        private readonly List<int> bottleTimerKeys = new List<int>();

        private bool spawnerInitialized;
        private bool isApplyingRemoteSpawn;
        private int spawnCounter;
        private float lastBagUnpackTime = -100f;
        private float nextFluidPollTime;
        private float nextScanTime;

        public bool IsBagUnpackCooldown => (Time.time - lastBagUnpackTime) < 4.0f;

        public event Action<GameObject, int> OnDynamicItemRegistered;
        public event Action<GameObject, int> OnItemRemovedFromWorld;

        public BeerAndConsumablesSync(OscHostContext context, string ownerModuleId)
        {
            ctx = context;
            moduleId = ownerModuleId;
        }

        public void Reset()
        {
            foreach (var kv in activePuppetBottles)
            {
                if (kv.Value.BottleObject != null) kv.Value.BottleObject.SetActive(false);
                if (kv.Value.DrinksRootObject != null) kv.Value.DrinksRootObject.SetActive(false);
            }

            creators.Clear();
            beerCases.Clear();
            shoppingBags.Clear();
            unpackedBags.Clear();
            consumables.Clear();
            suppressedBeerCases.Clear();
            suppressedConsumables.Clear();
            activePuppetBottles.Clear();
            bottleTimerKeys.Clear();

            spawnerInitialized = false;
            isApplyingRemoteSpawn = false;
            lastBagUnpackTime = -100f;
            nextFluidPollTime = 0f;
            nextScanTime = 0f;
        }

        public void InitializeSceneObjects()
        {
            InitializeSpawner();

            var rbs = Resources.FindObjectsOfTypeAll<Rigidbody>();
            for (int i = 0; i < rbs.Length; i++)
            {
                if (rbs[i] == null || rbs[i].gameObject == null) continue;
                RegisterCandidateObject(rbs[i].gameObject);
            }

            GameObject defaultCase = GameObject.Find("ITEMS/beercase0") ?? GameObject.Find("beercase0");
            if (defaultCase != null)
            {
                TrackBeerCase(defaultCase);
            }

            ctx.Log?.Invoke($"[Предметы/Расходники] Ящики пива: {beerCases.Count}, расходники: {consumables.Count}, пакеты: {shoppingBags.Count}");
        }

        public void RegisterCandidateObject(GameObject go)
        {
            if (go == null) return;
            string nameLower = (go.name ?? string.Empty).ToLowerInvariant();
            if (nameLower.Contains("beer") && nameLower.Contains("case"))
            {
                TrackBeerCase(go);
            }
            else if (nameLower.StartsWith("shopping bag"))
            {
                TrackShoppingBag(go);
            }
            else
            {
                TryRegisterConsumable(go);
            }
        }

        public void TickUpdate(Vector3 localPlayerPos)
        {
            if (!spawnerInitialized)
            {
                InitializeSpawner();
            }

            TickActivePuppetBottles();

            if (Time.time >= nextScanTime)
            {
                nextScanTime = Time.time + 2.0f;
                if (localPlayerPos != Vector3.zero)
                {
                    ScanNearbySpecials(localPlayerPos);
                }
            }

            if (ctx?.Transport != null && ctx.Transport.IsRunning && Time.time >= nextFluidPollTime)
            {
                nextFluidPollTime = Time.time + 1.5f;
                PollConsumableFluidLevels();
            }
        }

        private void ScanNearbySpecials(Vector3 center)
        {
            Collider[] cols = Physics.OverlapSphere(center, 12f);
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] == null || cols[i].gameObject == null) continue;
                GameObject go = cols[i].attachedRigidbody != null ? cols[i].attachedRigidbody.gameObject : cols[i].gameObject;
                if (go == null) continue;
                string lower = (go.name ?? string.Empty).ToLowerInvariant();
                if (lower.Contains("beer") && lower.Contains("case")) TrackBeerCase(go);
                else if (lower.StartsWith("shopping bag")) TrackShoppingBag(go);
            }
        }

        #region Spawner & Shopping Bags

        private void InitializeSpawner()
        {
            if (spawnerInitialized) return;

            GameObject spawnerGo = GameObject.Find("Spawner");
            if (spawnerGo == null)
            {
                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].name == "Spawner" && all[i].transform.root == all[i].transform)
                    {
                        spawnerGo = all[i];
                        break;
                    }
                }
            }
            if (spawnerGo == null) return;

            HookSpawnerChild(spawnerGo.transform.Find("CreateItems"), isShopbag: false);
            HookSpawnerChild(spawnerGo.transform.Find("CreateSpraycans"), isShopbag: false);
            HookSpawnerChild(spawnerGo.transform.Find("CreateMooseMeat"), isShopbag: false);
            HookSpawnerChild(spawnerGo.transform.Find("CreateShoppingbag"), isShopbag: true);

            spawnerInitialized = creators.Count > 0;
        }

        private void HookSpawnerChild(Transform holder, bool isShopbag)
        {
            if (holder == null) return;
            var fsms = holder.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null) continue;
                if (ctx.Ownership != null && !ctx.Ownership.TryClaim(fsm, moduleId, $"Spawner_{fsm.FsmName}")) continue;

                fsm.InitializeSafe();
                var entry = new CreatorEntry
                {
                    Name = fsm.FsmName,
                    Fsm = fsm,
                    IsShopbag = isShopbag,
                    NewVar = fsm.FsmVariables.FindFsmGameObject("New"),
                    NewItemIdVar = fsm.FsmVariables.FindFsmString("ID"),
                    SpawnEvent = fsm.AddSafeGlobalTransition("MP_SPAWNITEM", fsm.HasState("Spawn") ? "Spawn" : "Create product")
                };

                if (fsm.HasState("Spawn")) fsm.InsertAction("Spawn", () => HandleCreatorSpawned(entry), -1, false);
                if (fsm.HasState("Create product")) fsm.InsertAction("Create product", () => HandleCreatorSpawned(entry), -1, false);
                if (fsm.HasState("Create")) fsm.InsertAction("Create", () => HandleCreatorSpawned(entry), 5, false);
                if (fsm.HasState("Add ID")) fsm.InsertAction("Add ID", () => HandleCreatorSpawned(entry), 5, false);

                creators.Add(entry);
            }
        }

        private void HandleCreatorSpawned(CreatorEntry entry)
        {
            if (isApplyingRemoteSpawn || entry == null || entry.NewVar == null) return;
            GameObject spawnedObj = entry.NewVar.Value;
            if (spawnedObj == null || entry.LastProcessedObject == spawnedObj) return;
            entry.LastProcessedObject = spawnedObj;

            string id = (entry.NewItemIdVar != null && !string.IsNullOrEmpty(entry.NewItemIdVar.Value))
                ? entry.NewItemIdVar.Value
                : spawnedObj.name;

            int myId = ctx.Transport != null ? ctx.Transport.LocalPlayerId : 0;
            string uniqueId = $"{id}_{(myId == 0 ? "H" : "C")}_{++spawnCounter}";
            int hash = SceneHasher.GetStableHash(uniqueId);
            SceneHasher.RegisterDynamicObject(hash, spawnedObj);

            Rigidbody rb = spawnedObj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.useGravity = true;
            }

            RegisterCandidateObject(spawnedObj);
            OnDynamicItemRegistered?.Invoke(spawnedObj, hash);

            if (entry.DoSync && !entry.IsShopbag && ctx.Transport != null && ctx.Transport.IsRunning)
            {
                Vector3 vel = rb != null ? rb.velocity : Vector3.zero;
                Vector3 angVel = rb != null ? rb.angularVelocity : Vector3.zero;

                using (var p = new PacketWriter(PacketType.ItemSync))
                {
                    p.WriteByte((byte)ItemSubPacket.SpawnedItem);
                    p.WriteInt32(myId);
                    p.WriteString(entry.Name);
                    p.WriteInt32(hash);
                    p.WriteString(id);
                    p.WriteVector3(spawnedObj.transform.position);
                    p.WriteQuaternion(spawnedObj.transform.rotation);
                    p.WriteVector3(vel);
                    p.WriteVector3(angVel);
                    ctx.Transport.Send(p);
                }
            }

            entry.DoSync = true;
        }

        public void HandleSpawnedItemPacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            if (ctx.Transport != null && senderId == ctx.Transport.LocalPlayerId) return;

            string creatorName = reader.ReadString();
            int itemHash = reader.ReadInt32();
            string itemId = reader.ReadString();
            Vector3 pos = reader.ReadVector3();
            Quaternion rot = reader.ReadQuaternion();
            Vector3 vel = reader.ReadVector3();
            Vector3 angVel = reader.ReadVector3();

            if (SceneHasher.FindObjectByHash(itemHash) != null) return;

            for (int i = 0; i < creators.Count; i++)
            {
                CreatorEntry entry = creators[i];
                if (!string.Equals(entry.Name, creatorName, StringComparison.OrdinalIgnoreCase)) continue;

                isApplyingRemoteSpawn = true;
                try
                {
                    entry.DoSync = false;
                    if (entry.SpawnEvent != null)
                    {
                        entry.Fsm.SendEvent(entry.SpawnEvent.Name);
                    }

                    GameObject created = entry.NewVar != null ? entry.NewVar.Value : null;
                    if (created != null)
                    {
                        created.transform.position = pos;
                        created.transform.rotation = rot;
                        Rigidbody rb = created.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            rb.isKinematic = false;
                            rb.useGravity = true;
                            rb.velocity = vel;
                            rb.angularVelocity = angVel;
                            rb.WakeUp();
                        }

                        SceneHasher.RegisterDynamicObject(itemHash, created);
                        RegisterCandidateObject(created);
                        OnDynamicItemRegistered?.Invoke(created, itemHash);
                    }
                }
                finally
                {
                    entry.DoSync = true;
                    isApplyingRemoteSpawn = false;
                }
                break;
            }
        }

        private void TrackShoppingBag(GameObject bag)
        {
            if (bag == null) return;
            int hash = SceneHasher.GetHash(bag);
            if (unpackedBags.Contains(hash)) return;
            if (shoppingBags.TryGetValue(hash, out var existing) && existing.IsHooked) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(bag, "Use");
            if (fsm == null) return;
            if (ctx.Ownership != null && !ctx.Ownership.TryClaim(fsm, moduleId, $"ShoppingBag_{hash:X}")) return;

            fsm.InitializeSafe();
            FsmGameObject psVar = fsm.FsmVariables.FindFsmGameObject("ProductSpawner");
            FsmGameObject currentBagVar = null;
            if (psVar != null && psVar.Value != null)
            {
                PlayMakerFSM psLogic = PlayMakerBridge.FindFsm(psVar.Value, "Logic");
                if (psLogic != null)
                {
                    psLogic.InitializeSafe();
                    currentBagVar = psLogic.FsmVariables.FindFsmGameObject("CurrentBag");
                }
            }

            var tracked = new TrackedShoppingBag
            {
                Hash = hash,
                BagObject = bag,
                Fsm = fsm,
                CurrentBagVar = currentBagVar
            };

            if (fsm.HasState("Spawn all"))
            {
                fsm.InsertAction("Spawn all", () =>
                {
                    lastBagUnpackTime = Time.time;
                    if (tracked.CurrentBagVar != null) tracked.CurrentBagVar.Value = bag;
                }, 0, false);
            }

            if (fsm.HasState("Spawn one"))
            {
                fsm.InsertAction("Spawn one", () =>
                {
                    lastBagUnpackTime = Time.time;
                    if (tracked.CurrentBagVar != null) tracked.CurrentBagVar.Value = bag;
                }, 0, false);
            }

            if (fsm.HasState("Is garbage"))
            {
                fsm.InsertAction("Is garbage", () =>
                {
                    unpackedBags.Add(hash);
                    shoppingBags.Remove(hash);
                    OnItemRemovedFromWorld?.Invoke(bag, hash);

                    if (ctx.Transport != null && ctx.Transport.IsRunning)
                    {
                        using (var p = new PacketWriter(PacketType.ItemSync))
                        {
                            p.WriteByte((byte)ItemSubPacket.ShoppingBagGarbage);
                            p.WriteInt32(ctx.Transport.LocalPlayerId);
                            p.WriteInt32(hash);
                            ctx.Transport.Send(p);
                        }
                    }
                }, 0, false);
            }

            tracked.IsHooked = true;
            shoppingBags[hash] = tracked;
        }

        public void HandleShoppingBagGarbagePacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            int bagHash = reader.ReadInt32();
            if (ctx.Transport != null && senderId == ctx.Transport.LocalPlayerId) return;

            ApplyShoppingBagGarbage(bagHash);
        }

        private void ApplyShoppingBagGarbage(int bagHash)
        {
            lastBagUnpackTime = Time.time;
            unpackedBags.Add(bagHash);
            shoppingBags.Remove(bagHash);

            GameObject bag = SceneHasher.FindObjectByHash(bagHash);
            if (bag != null)
            {
                OnItemRemovedFromWorld?.Invoke(bag, bagHash);
                PlayMakerFSM fsm = PlayMakerBridge.FindFsm(bag, "Use");
                if (fsm != null) fsm.SendEvent("GARBAGE");
            }
        }

        #endregion

        #region Beer Cases & Native Bottle Activation

        private void TrackBeerCase(GameObject beerCase)
        {
            if (beerCase == null) return;
            string lower = (beerCase.name ?? string.Empty).ToLowerInvariant();
            if (!lower.Contains("beer") || !lower.Contains("case")) return;

            int caseHash = SceneHasher.GetHash(beerCase);
            if (beerCases.TryGetValue(caseHash, out var existing) && existing.IsHooked) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(beerCase, "Use");
            if (fsm == null) return;
            if (ctx.Ownership != null && !ctx.Ownership.TryClaim(fsm, moduleId, $"BeerCase_{caseHash:X}")) return;

            fsm.InitializeSafe();
            Transform bottles = beerCase.transform.Find("Bottles") ?? beerCase.transform.Find("Bottle");
            FsmInt destroyedVar = fsm.FsmVariables.FindFsmInt("DestroyedBottles");

            var tracked = new TrackedBeerCase
            {
                Hash = caseHash,
                CaseObject = beerCase,
                Fsm = fsm,
                BottlesParent = bottles,
                DestroyedBottlesVar = destroyedVar
            };

            if (fsm.HasState("Remove bottle"))
            {
                fsm.InsertAction("Remove bottle", () =>
                {
                    if (suppressedBeerCases.Contains(caseHash)) return;

                    int remaining = tracked.BottlesParent != null ? Mathf.Max(0, tracked.BottlesParent.childCount - 1) : 0;
                    int destroyed = tracked.DestroyedBottlesVar != null ? tracked.DestroyedBottlesVar.Value + 1 : Mathf.Max(0, 24 - remaining);

                    SendBeerCaseDrink(caseHash, remaining, destroyed);
                    SendDrinkGesturePacket(0);
                    ctx.Log?.Invoke($"[Пиво] Выпито из ящика #{caseHash:X} (осталось: {remaining})");
                }, 0, false);
            }

            tracked.IsHooked = true;
            beerCases[caseHash] = tracked;
        }

        private void SendBeerCaseDrink(int caseHash, int remaining, int destroyed)
        {
            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.ItemSync))
            {
                p.WriteByte((byte)ItemSubPacket.BeerCaseDrink);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteInt32(caseHash);
                p.WriteInt32(remaining);
                p.WriteInt32(destroyed);
                ctx.Transport.Send(p);
            }
        }

        public void HandleBeerCaseDrinkPacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            int caseHash = reader.ReadInt32();
            int remaining = reader.ReadInt32();
            int destroyed = reader.ReadInt32();
            if (ctx.Transport != null && senderId == ctx.Transport.LocalPlayerId) return;

            ApplyBeerCaseState(caseHash, remaining, destroyed, playDrinkEffects: true);
            ActivateNativeRemoteBottle(senderId, drinkIndex: 7);
        }

        private void ApplyBeerCaseState(int caseHash, int remaining, int destroyed, bool playDrinkEffects)
        {
            suppressedBeerCases.Add(caseHash);
            try
            {
                if (!beerCases.TryGetValue(caseHash, out var tracked) || tracked.CaseObject == null)
                {
                    GameObject go = SceneHasher.FindObjectByHash(caseHash) ?? GameObject.Find("ITEMS/beercase0") ?? GameObject.Find("beercase0");
                    if (go != null)
                    {
                        TrackBeerCase(go);
                        beerCases.TryGetValue(caseHash, out tracked);
                        if (tracked == null && beerCases.Count == 1)
                        {
                            foreach (var kv in beerCases)
                            {
                                tracked = kv.Value;
                                break;
                            }
                        }
                    }
                }

                if (tracked == null || tracked.CaseObject == null) return;

                if (playDrinkEffects && tracked.Fsm != null)
                {
                    tracked.Fsm.SendEvent("SUSKI");
                }

                if (tracked.BottlesParent == null)
                {
                    tracked.BottlesParent = tracked.CaseObject.transform.Find("Bottles") ?? tracked.CaseObject.transform.Find("Bottle");
                }

                if (tracked.BottlesParent != null)
                {
                    while (tracked.BottlesParent.childCount > remaining && tracked.BottlesParent.childCount > 0)
                    {
                        Transform lastBottle = tracked.BottlesParent.GetChild(tracked.BottlesParent.childCount - 1);
                        if (lastBottle != null) UnityEngine.Object.Destroy(lastBottle.gameObject);
                        else break;
                    }
                }

                if (tracked.DestroyedBottlesVar != null)
                {
                    tracked.DestroyedBottlesVar.Value = destroyed;
                }
            }
            finally
            {
                suppressedBeerCases.Remove(caseHash);
            }
        }

        private void SendDrinkGesturePacket(byte extra)
        {
            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.PlayerGesture))
            {
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteByte(7); // drinkbeer
                p.WriteBoolean(true);
                p.WriteByte(extra);
                ctx.Transport.Send(p);
            }
        }

        public void ActivateNativeRemoteBottle(int playerId, int drinkIndex = 7)
        {
            GameObject puppetRoot = GameObject.Find(playerId == 0 ? "RemotePuppet_Host" : $"RemotePuppet_Client_{playerId}");
            if (puppetRoot == null) return;

            Transform handLeft = PlayMakerBridge.FindChildRecursive(puppetRoot.transform, "hand_left");
            if (handLeft == null) return;

            GameObject bottleGo = null;
            GameObject drinksRoot = null;

            Transform nativeBottle = handLeft.Find("beer_bottle");
            if (nativeBottle != null)
            {
                bottleGo = nativeBottle.gameObject;
            }
            else
            {
                Transform drinksTr = handLeft.Find("Drinks");
                if (drinksTr != null && drinksTr.childCount > 0)
                {
                    drinksRoot = drinksTr.gameObject;
                    int chosen = Mathf.Clamp(drinkIndex, 0, drinksTr.childCount - 1);
                    for (int i = 0; i < drinksTr.childCount; i++)
                    {
                        Transform child = drinksTr.GetChild(i);
                        if (child != null)
                        {
                            bool match = i == chosen;
                            child.gameObject.SetActive(match);
                            if (match) bottleGo = child.gameObject;
                        }
                    }
                    drinksRoot.SetActive(true);
                }
            }

            if (bottleGo == null) return;

            var rends = bottleGo.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                if (rends[i] != null) rends[i].enabled = true;
            }
            bottleGo.SetActive(true);

            activePuppetBottles[playerId] = new ActiveBottleTimer
            {
                BottleObject = bottleGo,
                DrinksRootObject = drinksRoot,
                DeactivateAt = Time.time + 2.45f
            };
        }

        private void TickActivePuppetBottles()
        {
            if (activePuppetBottles.Count == 0) return;

            bottleTimerKeys.Clear();
            foreach (int k in activePuppetBottles.Keys) bottleTimerKeys.Add(k);

            float now = Time.time;
            for (int i = 0; i < bottleTimerKeys.Count; i++)
            {
                int playerId = bottleTimerKeys[i];
                ActiveBottleTimer timer = activePuppetBottles[playerId];
                if (now >= timer.DeactivateAt)
                {
                    if (timer.BottleObject != null) timer.BottleObject.SetActive(false);
                    if (timer.DrinksRootObject != null) timer.DrinksRootObject.SetActive(false);
                    activePuppetBottles.Remove(playerId);
                }
            }
        }

        #endregion

        #region Consumables (Food, Drinks, Auto Fluids)

        private void TryRegisterConsumable(GameObject go)
        {
            if (go == null) return;
            string nameLower = (go.name ?? string.Empty).ToLowerInvariant();
            if (nameLower.Contains("beer") && nameLower.Contains("case")) return;
            if (ItemValidator.IsVehicleOrAttachedPart(go)) return;

            string kind = ResolveConsumableKind(nameLower);
            if (string.IsNullOrEmpty(kind)) return;

            int hash = SceneHasher.GetHash(go);
            if (consumables.TryGetValue(hash, out var existing))
            {
                if (existing.GameObject == null) existing.GameObject = go;
                if (!existing.IsHooked) HookConsumableFsm(existing);
                return;
            }

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(go, "Use")
                ?? PlayMakerBridge.FindFsm(go, "Data")
                ?? PlayMakerBridge.FindFsm(go, "Item")
                ?? go.GetComponent<PlayMakerFSM>();

            if (fsm == null) return;
            if (ctx.Ownership != null && !ctx.Ownership.TryClaim(fsm, moduleId, $"Consumable_{hash:X}")) return;

            fsm.InitializeSafe();
            FsmInt consumedVar = fsm.FsmVariables.FindFsmInt("Consumed") ?? fsm.FsmVariables.FindFsmInt("ItemConsumed");
            FsmFloat fluidVar = fsm.FsmVariables.FindFsmFloat("Fluid");

            var tracked = new TrackedConsumable
            {
                Hash = hash,
                GameObject = go,
                Fsm = fsm,
                Kind = kind,
                ConsumedVar = consumedVar,
                FluidVar = fluidVar,
                LastSyncedFluid = fluidVar != null ? fluidVar.Value : 0f,
                SpawnTime = Time.time
            };

            consumables[hash] = tracked;
            HookConsumableFsm(tracked);
        }

        private static string ResolveConsumableKind(string nameLower)
        {
            if (nameLower.Contains("cigarette")) return null;
            if (nameLower.Contains("juice")) return "juice";
            if (nameLower.Contains("milk")) return "milk";
            if (nameLower.Contains("sausage")) return "sausage";
            if (nameLower.Contains("pizza")) return "pizza";
            if (nameLower.Contains("macaron")) return "macaron";
            if (nameLower.Contains("chip")) return "chips";
            if (nameLower.Contains("booze") || nameLower.Contains("vodka") || nameLower.Contains("spiritus")) return "booze";
            if (nameLower.Contains("beer")) return "beer";
            if (nameLower.Contains("coffee")) return "coffee";
            if (nameLower.Contains("mosquito") || nameLower.Contains("spray")) return "spray";
            if (nameLower.Contains("extinguisher")) return "extinguisher";
            if (nameLower.Contains("brakefluid") || nameLower.Contains("motoroil") || nameLower.Contains("coolant")) return "fluid";
            return null;
        }

        private void HookConsumableFsm(TrackedConsumable tracked)
        {
            if (tracked.IsHooked || tracked.Fsm == null) return;
            int hash = tracked.Hash;

            if (tracked.Kind == "juice" || tracked.Kind == "milk" || tracked.Kind == "booze" || tracked.Kind == "beer")
            {
                if (tracked.Fsm.HasState("Check drink"))
                {
                    tracked.Fsm.InsertAction("Check drink", () =>
                    {
                        if (IsBagUnpackCooldown || (Time.time - tracked.SpawnTime) < 4.0f)
                        {
                            tracked.Fsm.SendEvent("STOP");
                        }
                    }, 0, false);
                }
            }

            if (tracked.Kind == "juice")
            {
                if (tracked.Fsm.HasState("Juice"))
                {
                    tracked.Fsm.InsertAction("Juice", () =>
                    {
                        if (suppressedConsumables.Contains(hash)) return;
                        if (IsBagUnpackCooldown || (Time.time - tracked.SpawnTime) < 4.0f) return;
                        int val = tracked.ConsumedVar != null ? tracked.ConsumedVar.Value + 1 : 1;
                        SendConsumableAction(hash, ConsumableActionKind.ConsumeDrink, val, 0f);
                        SendDrinkGesturePacket(7);
                    }, 0, false);
                }

                if (tracked.Fsm.HasState("Empty"))
                {
                    tracked.Fsm.InsertAction("Empty", () =>
                    {
                        if (suppressedConsumables.Contains(hash)) return;
                        SendConsumableAction(hash, ConsumableActionKind.Empty, 3, 0f);
                    }, 0, false);
                }
            }
            else if (tracked.Kind == "milk" || tracked.Kind == "booze" || tracked.Kind == "beer")
            {
                string state = tracked.Fsm.HasState("Play anim") ? "Play anim"
                    : (tracked.Fsm.HasState("Eat 2") ? "Eat 2" : (tracked.Fsm.HasState("Eat") ? "Eat" : null));
                if (state != null)
                {
                    tracked.Fsm.InsertAction(state, () =>
                    {
                        if (suppressedConsumables.Contains(hash)) return;
                        if (IsBagUnpackCooldown || (Time.time - tracked.SpawnTime) < 4.0f) return;
                        SendConsumableAction(hash, ConsumableActionKind.ConsumeDrink, 1, 0f);
                        SendDrinkGesturePacket(7);
                    }, 0, false);
                }
            }
            else if (tracked.Kind == "sausage" || tracked.Kind == "pizza" || tracked.Kind == "macaron" ||
                     tracked.Kind == "chips" || tracked.Kind == "coffee")
            {
                string state = tracked.Fsm.HasState("Eat") ? "Eat" : (tracked.Fsm.HasState("Eat 2") ? "Eat 2" : null);
                if (state != null)
                {
                    tracked.Fsm.InsertAction(state, () =>
                    {
                        if (suppressedConsumables.Contains(hash)) return;
                        SendConsumableAction(hash, ConsumableActionKind.ConsumeBite, 1, 0f);
                    }, 0, false);
                }
            }

            tracked.IsHooked = true;
        }

        private void PollConsumableFluidLevels()
        {
            foreach (var kv in consumables)
            {
                TrackedConsumable tracked = kv.Value;
                if (tracked == null || tracked.GameObject == null || tracked.FluidVar == null) continue;
                float current = tracked.FluidVar.Value;
                if (Mathf.Abs(current - tracked.LastSyncedFluid) >= 0.5f)
                {
                    tracked.LastSyncedFluid = current;
                    SendConsumableAction(tracked.Hash, ConsumableActionKind.FluidLevel, 0, current);
                }
            }
        }

        private void SendConsumableAction(int hash, ConsumableActionKind action, int intVal, float floatVal)
        {
            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.ItemSync))
            {
                p.WriteByte((byte)ItemSubPacket.ConsumableAction);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteInt32(hash);
                p.WriteByte((byte)action);
                p.WriteInt32(intVal);
                p.WriteSingle(floatVal);
                ctx.Transport.Send(p);
            }
        }

        public void HandleConsumableActionPacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            int itemHash = reader.ReadInt32();
            var action = (ConsumableActionKind)reader.ReadByte();
            int intVal = reader.ReadInt32();
            float floatVal = reader.ReadSingle();

            if (ctx.Transport != null && senderId == ctx.Transport.LocalPlayerId) return;

            suppressedConsumables.Add(itemHash);
            try
            {
                if (!consumables.TryGetValue(itemHash, out var tracked) || tracked.GameObject == null)
                {
                    GameObject go = SceneHasher.FindObjectByHash(itemHash);
                    if (go != null)
                    {
                        TryRegisterConsumable(go);
                        consumables.TryGetValue(itemHash, out tracked);
                    }
                }

                if (tracked == null || tracked.GameObject == null) return;

                switch (action)
                {
                    case ConsumableActionKind.ConsumeDrink:
                    case ConsumableActionKind.ConsumeBite:
                        if (action == ConsumableActionKind.ConsumeDrink && !IsBagUnpackCooldown)
                        {
                            ActivateNativeRemoteBottle(senderId, tracked.Kind == "milk" ? 3 : (tracked.Kind == "booze" ? 0 : 7));
                        }
                        if (tracked.ConsumedVar != null) tracked.ConsumedVar.Value = intVal;

                        if (tracked.Kind == "juice")
                        {
                            if (intVal >= 3) MakeJuiceEmpty(tracked);
                        }
                        else
                        {
                            ConsumeAndHide(tracked);
                        }
                        break;

                    case ConsumableActionKind.Empty:
                        if (tracked.Kind == "juice") MakeJuiceEmpty(tracked);
                        else ConsumeAndHide(tracked);
                        break;

                    case ConsumableActionKind.Destroy:
                        ConsumeAndHide(tracked);
                        break;

                    case ConsumableActionKind.FluidLevel:
                        if (tracked.FluidVar != null)
                        {
                            tracked.FluidVar.Value = floatVal;
                            tracked.LastSyncedFluid = floatVal;
                        }
                        break;
                }
            }
            finally
            {
                suppressedConsumables.Remove(itemHash);
            }
        }

        private void MakeJuiceEmpty(TrackedConsumable tracked)
        {
            if (tracked.Fsm != null && tracked.Fsm.HasState("Empty"))
            {
                tracked.Fsm.SendEvent("EMPTY");
            }
            Transform fluid = tracked.GameObject.transform.Find("juice") ?? tracked.GameObject.transform.Find("fluid");
            if (fluid != null) fluid.gameObject.SetActive(false);
        }

        private void ConsumeAndHide(TrackedConsumable tracked)
        {
            OnItemRemovedFromWorld?.Invoke(tracked.GameObject, tracked.Hash);

            if (tracked.Fsm != null)
            {
                if (tracked.Fsm.HasState("Destroy")) tracked.Fsm.SendEvent("DESTROY");
                else if (tracked.Fsm.HasState("Destroy self")) tracked.Fsm.SendEvent("FINISHED");
            }

            var cols = tracked.GameObject.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++) if (cols[i] != null) cols[i].enabled = false;

            var rends = tracked.GameObject.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rends.Length; i++) if (rends[i] != null) rends[i].enabled = false;

            Rigidbody rb = tracked.GameObject.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;

            tracked.GameObject.transform.position = new Vector3(0f, -500f, 0f);
        }

        #endregion

        #region Snapshot Serialization

        public void WriteSnapshot(PacketWriter writer)
        {
            writer.WriteInt32(beerCases.Count);
            foreach (var kv in beerCases)
            {
                TrackedBeerCase bc = kv.Value;
                int rem = bc.BottlesParent != null ? bc.BottlesParent.childCount : 0;
                int dest = bc.DestroyedBottlesVar != null ? bc.DestroyedBottlesVar.Value : (24 - rem);
                writer.WriteInt32(bc.Hash);
                writer.WriteInt32(rem);
                writer.WriteInt32(dest);
            }

            writer.WriteInt32(unpackedBags.Count);
            foreach (int bagHash in unpackedBags)
            {
                writer.WriteInt32(bagHash);
            }
        }

        public void ApplySnapshot(PacketReader reader)
        {
            int caseCount = reader.ReadInt32();
            for (int i = 0; i < caseCount; i++)
            {
                int caseHash = reader.ReadInt32();
                int remaining = reader.ReadInt32();
                int destroyed = reader.ReadInt32();
                ApplyBeerCaseState(caseHash, remaining, destroyed, playDrinkEffects: false);
            }

            int bagCount = reader.ReadInt32();
            for (int i = 0; i < bagCount; i++)
            {
                int bagHash = reader.ReadInt32();
                ApplyShoppingBagGarbage(bagHash);
            }
        }

        #endregion
    }
}
