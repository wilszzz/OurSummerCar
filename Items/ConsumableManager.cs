using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Items
{
    /// <summary>
    /// Менеджер сетевой синхронизации всех расходуемых предметов в My Summer Car:
    /// 1. Напитки: сок (juice concentrate), молоко (milk), алкоголь/водка (booze, vodka, shot), кофе (coffee).
    /// 2. Еда: сосиски (sausages, sausage0), пицца (pizza), макароны (macaron box), чипсы (potato chips).
    /// 3. Сигареты: пачки сигарет (cigarettes0), расход штук в пачке.
    /// 4. Спреи и баллоны: спрей от комаров (mosquito spray), огнетушитель (fire extinguisher).
    /// 5. Автомобильные жидкости: канистры тормозной жидкости, моторного масла, антифриза.
    /// 
    /// Гарантирует: если один игрок откусил/выпил/потратил предмет, у второго игрока он не остается полным,
    /// а точно так же уменьшает свой объем, пустеет или уничтожается.
    /// </summary>
    public class ConsumableManager
    {
        public static ConsumableManager Instance { get; private set; }

        private readonly NetworkTransport transport;
        private readonly Dictionary<int, TrackedConsumable> trackedItems = new Dictionary<int, TrackedConsumable>();
        private readonly HashSet<int> remoteSuppressedHashes = new HashSet<int>();

        private float nextPeriodicCheckTime = 0f;
        private bool isInitialized = false;

        public event Action<string> OnLog;
        public int TrackedCount => trackedItems.Count;

        public class TrackedConsumable
        {
            public int Hash;
            public GameObject GameObject;
            public PlayMakerFSM Fsm;
            public string Kind; // "juice", "milk", "sausage", "pizza", "macaron", "chips", "booze", "cigarettes", "spray", "extinguisher", "fluid", "coffee"
            public FsmInt ConsumedVar;
            public FsmFloat FluidVar;
            public FsmFloat ConditionVar;
            public float LastSyncedFluid;
            public int LastSyncedConsumed;
            public float SpawnTime;
            public bool IsHooked;
        }

        public void Reset()
        {
            isInitialized = false;
            trackedItems.Clear();
            remoteSuppressedHashes.Clear();
        }

        public ConsumableManager(NetworkTransport transport)
        {
            Instance = this;
            this.transport = transport;
            this.transport.OnPacketReceived += HandlePacket;
        }

        public void InitializeConsumables()
        {
            if (isInitialized) return;

            try
            {
                var rbs = Resources.FindObjectsOfTypeAll<Rigidbody>();
                for (int i = 0; i < rbs.Length; i++)
                {
                    if (rbs[i] == null || rbs[i].gameObject == null) continue;
                    GameObject go = rbs[i].gameObject;
                    TryRegisterConsumable(go);
                }

                isInitialized = true;
                OnLog?.Invoke($"[Расходники] Инициализировано {trackedItems.Count} расходуемых предметов.");
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[ConsumableManager Init Error] {ex.Message}");
            }
        }

        public void Update()
        {
            if (!transport.IsRunning || !isInitialized) return;

            // Периодическая проверка уровня жидкостей для канистр, баллонов и спреев
            if (Time.time >= nextPeriodicCheckTime)
            {
                nextPeriodicCheckTime = Time.time + 1.5f;
                CheckFluidLevels();
            }
        }

        /// <summary>
        /// Вызывается при создании нового предмета в мире (из пакета или в магазине).
        /// </summary>
        public void CheckCreatedConsumable(GameObject go)
        {
            if (go == null) return;
            TryRegisterConsumable(go);
        }

        private bool TryRegisterConsumable(GameObject go)
        {
            if (go == null) return false;
            string nameLower = go.name.ToLower();

            // Исключаем ящики пива (у них отдельный перманентно зафиксированный менеджер)
            if (nameLower.Contains("beer") && nameLower.Contains("case")) return false;

            string kind = GetConsumableKind(nameLower);
            if (string.IsNullOrEmpty(kind)) return false;

            int hash = SceneHasher.GetHash(go);
            if (trackedItems.TryGetValue(hash, out var existing))
            {
                if (existing.GameObject == null)
                {
                    existing.GameObject = go;
                }
                if (!existing.IsHooked)
                {
                    HookConsumableFsm(existing);
                }
                return true;
            }

            PlayMakerFSM fsm = null;
            foreach (var f in go.GetComponents<PlayMakerFSM>())
            {
                if (f.FsmName == "Use" || f.FsmName == "Data" || f.FsmName == "Item")
                {
                    fsm = f;
                    break;
                }
            }

            if (fsm == null)
            {
                var fsms = go.GetComponents<PlayMakerFSM>();
                if (fsms.Length > 0) fsm = fsms[0];
            }

            FsmInt consumedVar = null;
            FsmFloat fluidVar = null;
            FsmFloat condVar = null;

            if (fsm != null)
            {
                fsm.Initialize();
                consumedVar = fsm.FsmVariables.FindFsmInt("Consumed") ?? fsm.FsmVariables.FindFsmInt("ItemConsumed");
                fluidVar = fsm.FsmVariables.FindFsmFloat("Fluid");
                condVar = fsm.FsmVariables.FindFsmFloat("Condition");
            }

            var tracked = new TrackedConsumable
            {
                Hash = hash,
                GameObject = go,
                Fsm = fsm,
                Kind = kind,
                ConsumedVar = consumedVar,
                FluidVar = fluidVar,
                ConditionVar = condVar,
                LastSyncedFluid = fluidVar != null ? fluidVar.Value : 0f,
                LastSyncedConsumed = consumedVar != null ? consumedVar.Value : 0,
                SpawnTime = Time.time,
                IsHooked = false
            };

            trackedItems[hash] = tracked;
            HookConsumableFsm(tracked);
            return true;
        }

        private string GetConsumableKind(string nameLower)
        {
            if (nameLower.Contains("juice")) return "juice";
            if (nameLower.Contains("milk")) return "milk";
            if (nameLower.Contains("sausage")) return "sausage";
            if (nameLower.Contains("pizza")) return "pizza";
            if (nameLower.Contains("macaron")) return "macaron";
            if (nameLower.Contains("chip")) return "chips";
            if (nameLower.Contains("booze") || nameLower.Contains("vodka") || nameLower.Contains("spiritus")) return "booze";
            if (nameLower.Contains("beer")) return "beer";
            // Сигареты намеренно исключены из синхронизации расхода — у каждого игрока свой личный запас сигарет в пачке
            if (nameLower.Contains("cigarette")) return null;
            if (nameLower.Contains("mosquito") || nameLower.Contains("spray")) return "spray";
            if (nameLower.Contains("extinguisher")) return "extinguisher";
            if (nameLower.Contains("brakefluid") || nameLower.Contains("motoroil") || nameLower.Contains("coolant")) return "fluid";
            if (nameLower.Contains("coffee")) return "coffee";
            return null;
        }

        private void HookConsumableFsm(TrackedConsumable tracked)
        {
            if (tracked.IsHooked || tracked.Fsm == null) return;
            tracked.Fsm.Initialize();
            int hash = tracked.Hash;
            string kind = tracked.Kind;

            switch (kind)
            {
                case "juice":
                    // Отслеживание распития сока
                    // ПРЯМОЙ ЗАПРЕТ: если идет распаковка пакета или предмет только что появился из пакета,
                    // перехватываем "Check drink" и отправляем STOP, чтобы нативный MSC FSM вернулся в Wait player и не пил сок!
                    if (tracked.Fsm.HasState("Check drink"))
                    {
                        tracked.Fsm.InsertAction("Check drink", () =>
                        {
                            if (ItemManager.IsBagUnpackCooldown || (Time.time - tracked.SpawnTime < 4.0f))
                            {
                                tracked.Fsm.SendEvent("STOP");
                            }
                        }, 0, false);
                    }
                    if (tracked.Fsm.HasState("Juice"))
                    {
                        tracked.Fsm.InsertAction("Juice", () =>
                        {
                            if (remoteSuppressedHashes.Contains(hash)) return;
                            if (ItemManager.IsBagUnpackCooldown || (Time.time - tracked.SpawnTime < 4.0f)) return;
                            int cVal = tracked.ConsumedVar != null ? tracked.ConsumedVar.Value + 1 : 1;
                            SendConsumableAction(hash, ConsumableActionType.ConsumeDrink, cVal, 0f);
                            Player.PlayerManager.Instance?.SetDrinkingBeer(2.5f);
                            Player.LocalPlayerAnimationTracker.Instance?.TriggerDrinkBeerGesture();
                            OnLog?.Invoke($"[Расходник] Выпит сок #{hash:X} (глоток #{cVal})");
                        }, 0, false);
                    }
                    if (tracked.Fsm.HasState("Empty"))
                    {
                        tracked.Fsm.InsertAction("Empty", () =>
                        {
                            if (remoteSuppressedHashes.Contains(hash)) return;
                            SendConsumableAction(hash, ConsumableActionType.Empty, 3, 0f);
                        }, 0, false);
                    }
                    break;

                case "milk":
                case "booze":
                case "beer":
                    // Молоко, алкоголь и бутылки пива
                    // ПРЯМОЙ ЗАПРЕТ: при зажатом F во время распаковки пакета нативная бутылка переходит в "Check drink".
                    // Отправляем нативное событие STOP, которое мгновенно возвращает FSM бутылки в "Wait player"!
                    // Это полностью предотвращает срабатывание "Play anim", предотвращает выпивание бутылки и ее удаление.
                    if (tracked.Fsm.HasState("Check drink"))
                    {
                        tracked.Fsm.InsertAction("Check drink", () =>
                        {
                            if (ItemManager.IsBagUnpackCooldown || (Time.time - tracked.SpawnTime < 4.0f))
                            {
                                tracked.Fsm.SendEvent("STOP");
                            }
                        }, 0, false);
                    }

                    string drinkState = tracked.Fsm.HasState("Play anim") ? "Play anim" : (tracked.Fsm.HasState("Eat 2") ? "Eat 2" : (tracked.Fsm.HasState("Eat") ? "Eat" : null));
                    if (drinkState != null)
                    {
                        tracked.Fsm.InsertAction(drinkState, () =>
                        {
                            if (remoteSuppressedHashes.Contains(hash)) return;
                            if (ItemManager.IsBagUnpackCooldown || (Time.time - tracked.SpawnTime < 4.0f)) return;
                            SendConsumableAction(hash, ConsumableActionType.ConsumeDrink, 1, 0f);
                            Player.PlayerManager.Instance?.SetDrinkingBeer(3.5f);
                            Player.LocalPlayerAnimationTracker.Instance?.TriggerDrinkBeerGesture();
                            OnLog?.Invoke($"[Напитки] Выпито {tracked.GameObject.name} #{hash:X}");
                        }, 0, false);
                    }
                    break;

                case "sausage":
                case "pizza":
                case "macaron":
                case "chips":
                case "coffee":
                    // Еда
                    string eatState = tracked.Fsm.HasState("Eat") ? "Eat" : (tracked.Fsm.HasState("Eat 2") ? "Eat 2" : null);
                    if (eatState != null)
                    {
                        tracked.Fsm.InsertAction(eatState, () =>
                        {
                            if (remoteSuppressedHashes.Contains(hash)) return;
                            SendConsumableAction(hash, ConsumableActionType.ConsumeBite, 1, 0f);
                            OnLog?.Invoke($"[Расходник] Съедено {tracked.GameObject.name} #{hash:X}");
                        }, 0, false);
                    }
                    break;

                case "cigarettes":
                    // Пачка сигарет
                    string cigState = tracked.Fsm.HasState("Wait button 2") ? "Wait button 2" : (tracked.Fsm.HasState("Add inventory") ? "Add inventory" : null);
                    if (cigState != null)
                    {
                        tracked.Fsm.InsertAction(cigState, () =>
                        {
                            if (remoteSuppressedHashes.Contains(hash)) return;
                            int cVal = tracked.ConsumedVar != null ? tracked.ConsumedVar.Value + 1 : 1;
                            SendConsumableAction(hash, ConsumableActionType.CigaretteTaken, cVal, 0f);
                            OnLog?.Invoke($"[Расходник] Взята сигарета из пачки #{hash:X} ({cVal}/20)");
                        }, 0, false);
                    }
                    break;
            }

            tracked.IsHooked = true;
        }

        private void CheckFluidLevels()
        {
            foreach (var kvp in trackedItems)
            {
                var tracked = kvp.Value;
                if (tracked == null || tracked.GameObject == null || tracked.FluidVar == null) continue;

                float current = tracked.FluidVar.Value;
                if (Mathf.Abs(current - tracked.LastSyncedFluid) >= 0.5f)
                {
                    tracked.LastSyncedFluid = current;
                    SendConsumableAction(tracked.Hash, ConsumableActionType.FluidLevel, 0, current);
                }
            }
        }

        private void SendConsumableAction(int itemHash, ConsumableActionType actionType, int intVal, float floatVal)
        {
            using (var packet = new PacketWriter(PacketType.ConsumableAction))
            {
                packet.WriteInt32(itemHash);
                packet.WriteByte((byte)actionType);
                packet.WriteInt32(intVal);
                packet.WriteSingle(floatVal);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.ConsumableAction)
            {
                HandleConsumableAction(reader, connectionId);
            }
        }

        private void HandleConsumableAction(PacketReader reader, int connectionId)
        {
            int itemHash = reader.ReadInt32();
            ConsumableActionType actionType = (ConsumableActionType)reader.ReadByte();
            int intVal = reader.ReadInt32();
            float floatVal = reader.ReadSingle();

            // Ретрансляция хостом всем остальным клиентам
            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.ConsumableAction))
                {
                    relay.WriteInt32(itemHash);
                    relay.WriteByte((byte)actionType);
                    relay.WriteInt32(intVal);
                    relay.WriteSingle(floatVal);
                    transport.Send(relay, connectionId);
                }
            }

            ApplyRemoteConsumableAction(itemHash, actionType, intVal, floatVal, connectionId);
        }

        private void ApplyRemoteConsumableAction(int itemHash, ConsumableActionType actionType, int intVal, float floatVal, int connectionId = 0)
        {
            remoteSuppressedHashes.Add(itemHash);
            try
            {
                TrackedConsumable tracked;
                if (!trackedItems.TryGetValue(itemHash, out tracked) || tracked.GameObject == null)
                {
                    GameObject go = SceneHasher.FindObjectByHash(itemHash);
                    if (go != null)
                    {
                        TryRegisterConsumable(go);
                        trackedItems.TryGetValue(itemHash, out tracked);
                    }
                }

                if (tracked == null || tracked.GameObject == null) return;

                switch (actionType)
                {
                    case ConsumableActionType.ConsumeDrink:
                    case ConsumableActionType.ConsumeBite:
                        if (actionType == ConsumableActionType.ConsumeDrink && !ItemManager.IsBagUnpackCooldown)
                        {
                            Player.PlayerManager.Instance?.TriggerRemoteDrinkBeer(connectionId);
                        }
                        if (tracked.ConsumedVar != null)
                        {
                            tracked.ConsumedVar.Value = intVal;
                        }
                        if (tracked.Kind == "juice")
                        {
                            if (intVal >= 3)
                            {
                                MakeJuiceEmpty(tracked);
                            }
                        }
                        else
                        {
                            // Молоко, сосиски, пицца, чипсы, водка — после употребления переходят в Destroy
                            ConsumeAndDestroyObject(tracked);
                        }
                        OnLog?.Invoke($"[Расходник] Другой игрок использовал {tracked.GameObject.name} #{itemHash:X}");
                        break;

                    case ConsumableActionType.Empty:
                        if (tracked.Kind == "juice")
                        {
                            MakeJuiceEmpty(tracked);
                        }
                        else
                        {
                            ConsumeAndDestroyObject(tracked);
                        }
                        break;

                    case ConsumableActionType.Destroy:
                        ConsumeAndDestroyObject(tracked);
                        break;

                    case ConsumableActionType.CigaretteTaken:
                        if (tracked.ConsumedVar != null)
                        {
                            tracked.ConsumedVar.Value = intVal;
                        }
                        if (intVal >= 20)
                        {
                            ConsumeAndDestroyObject(tracked);
                        }
                        OnLog?.Invoke($"[Расходник] Другой игрок взял сигарету ({intVal}/20)");
                        break;

                    case ConsumableActionType.FluidLevel:
                        if (tracked.FluidVar != null)
                        {
                            tracked.FluidVar.Value = floatVal;
                            tracked.LastSyncedFluid = floatVal;
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[Consumable Apply Error] {ex.Message}");
            }
            finally
            {
                remoteSuppressedHashes.Remove(itemHash);
            }
        }

        private void MakeJuiceEmpty(TrackedConsumable tracked)
        {
            if (tracked == null || tracked.GameObject == null) return;

            if (tracked.Fsm != null && tracked.Fsm.HasState("Empty"))
            {
                tracked.Fsm.SendEvent("EMPTY");
            }

            // Отключаем дочерний меш содержимого
            Transform fluidMesh = tracked.GameObject.transform.Find("juice") ?? tracked.GameObject.transform.Find("fluid");
            if (fluidMesh != null)
            {
                fluidMesh.gameObject.SetActive(false);
            }
        }

        private void ConsumeAndDestroyObject(TrackedConsumable tracked)
        {
            if (tracked == null || tracked.GameObject == null) return;

            // 1. Пытаемся запустить нативное состояние Destroy в FSM
            if (tracked.Fsm != null)
            {
                if (tracked.Fsm.HasState("Destroy"))
                {
                    tracked.Fsm.SendEvent("DESTROY");
                }
                else if (tracked.Fsm.HasState("Destroy self"))
                {
                    tracked.Fsm.SendEvent("FINISHED");
                }
            }

            // 2. Гарантированно отключаем коллайдеры и рендереры, чтобы предмет не оставался в мире
            var colliders = tracked.GameObject.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null) colliders[i].enabled = false;
            }

            var renderers = tracked.GameObject.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null) renderers[i].enabled = false;
            }

            var rb = tracked.GameObject.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
            }

            // 3. Перемещаем в мусорную позицию под картой
            tracked.GameObject.transform.position = new Vector3(0, -500f, 0);
        }
    }
}
