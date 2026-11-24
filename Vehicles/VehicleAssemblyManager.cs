using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Vehicles
{
    /// <summary>
    /// Сетевой менеджер сборки автомобиля Satsuma (по архитектуре WreckMP NetPartManager):
    /// 1. Установка деталей на шасси и узлы двигателя/подвески (FSM "Assemble" триггеров).
    /// 2. Снятие деталей (FSM "Removal" на деталях).
    /// 3. Синхронизация болтов и затяжки ключом (FSM "Screw" на болтах, стадии затяжки от 0 до 8).
    /// 4. Пакет начального снимка установленных деталей и закрученных болтов при подключении игрока.
    /// </summary>
    public class VehicleAssemblyManager
    {
        public static VehicleAssemblyManager Instance { get; private set; }

        private readonly NetworkTransport transport;

        // Отслеживание болтов (FSM "Screw")
        public class TrackedBolt
        {
            public int Hash;
            public PlayMakerFSM Fsm;
            public FsmInt StageVar;
            public FsmFloat TightnessVar;
            public FsmFloat AlignmentVar;
            public FsmFloat RotVar;
            public float MaxAlignment;
            public float RotAmount;
            public bool IsTuneBolt;
            public bool IsScrewableLid;
            public bool IsPin;
            public byte LastSyncedStage;
            public bool IsHooked;

            public byte CurrentStage
            {
                get
                {
                    if (IsTuneBolt && AlignmentVar != null && MaxAlignment > 0f)
                    {
                        return (byte)Mathf.Clamp(Mathf.RoundToInt(AlignmentVar.Value / MaxAlignment * 255f), 0, 255);
                    }
                    if (IsScrewableLid && RotVar != null)
                    {
                        return (byte)Mathf.Clamp(Mathf.RoundToInt(RotVar.Value / 360f * 255f), 0, 255);
                    }
                    if (StageVar != null)
                    {
                        return (byte)Mathf.Clamp(StageVar.Value, 0, 255);
                    }
                    if (TightnessVar != null)
                    {
                        return (byte)Mathf.Clamp(Mathf.RoundToInt(TightnessVar.Value), 0, 255);
                    }
                    return 0;
                }
            }
        }

        // Отслеживание снятия деталей (FSM "Removal")
        public class TrackedRemovalPart
        {
            public int Hash;
            public GameObject GameObject;
            public PlayMakerFSM Fsm;
            public FsmEvent MpRemoveEvent;
            public bool IsHooked;
        }

        // Отслеживание триггеров сборки (FSM "Assemble")
        public class TrackedAssembleTrigger
        {
            public int Hash;
            public GameObject GameObject;
            public PlayMakerFSM Fsm;
            public FsmGameObject PartVar;
            public FsmEvent MpAssembleEvent;
            public bool IsHooked;
            public bool IsWiring;
            public bool IsTriggerCharger;
        }

        private struct PendingAssemblePart
        {
            public int TriggerHash;
            public int PartHash;
            public float RetryTime;
            public int RetryCount;
        }

        private readonly Dictionary<int, TrackedBolt> bolts = new Dictionary<int, TrackedBolt>();
        private readonly Dictionary<int, TrackedRemovalPart> removalParts = new Dictionary<int, TrackedRemovalPart>();
        private readonly Dictionary<int, TrackedAssembleTrigger> assembleTriggers = new Dictionary<int, TrackedAssembleTrigger>();
        private readonly List<PendingAssemblePart> pendingParts = new List<PendingAssemblePart>();

        private readonly HashSet<int> suppressedBolts = new HashSet<int>();
        private readonly HashSet<int> suppressedParts = new HashSet<int>();

        private bool isInitialized = false;

        public event Action<string> OnLog;
        public int TrackedBoltsCount => bolts.Count;
        public int TrackedPartsCount => removalParts.Count;
        public int TrackedTriggersCount => assembleTriggers.Count;

        public void Reset()
        {
            isInitialized = false;
            bolts.Clear();
            removalParts.Clear();
            assembleTriggers.Clear();
            pendingParts.Clear();
            suppressedBolts.Clear();
            suppressedParts.Clear();
        }

        public void Update()
        {
            if (pendingParts.Count == 0) return;

            float now = Time.time;
            for (int i = pendingParts.Count - 1; i >= 0; i--)
            {
                var pending = pendingParts[i];
                if (now < pending.RetryTime) continue;

                bool success = TryApplyPartAssemble(pending.TriggerHash, pending.PartHash, true);
                if (success || pending.RetryCount >= 30) // до 3 секунд повторов
                {
                    pendingParts.RemoveAt(i);
                }
                else
                {
                    pending.RetryCount++;
                    pending.RetryTime = now + 0.1f; // повтор через 100 мс
                    pendingParts[i] = pending;
                }
            }
        }

        public VehicleAssemblyManager(NetworkTransport transport)
        {
            Instance = this;
            this.transport = transport;
            this.transport.OnPacketReceived += HandlePacket;
            this.transport.OnClientConnected += HandleClientConnected;
        }

        public void InitializeAssembly()
        {
            if (isInitialized) return;

            try
            {
                // 1. Поиск всех FSM болтов на деталях ("Screw")
                InitBolts();

                // 2. Поиск всех FSM снятия деталей ("Removal")
                InitRemovalParts();

                // 3. Поиск всех триггеров установки деталей ("Assemble")
                InitAssembleTriggers();

                isInitialized = true;
                OnLog?.Invoke($"[Сборка Satsuma] Подключено деталей: {removalParts.Count}, триггеров: {assembleTriggers.Count}, болтов: {bolts.Count}.");
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[VehicleAssemblyManager Init Error] {ex.Message}");
            }
        }

        private void InitBolts()
        {
            var allFsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            for (int i = 0; i < allFsms.Length; i++)
            {
                var fsm = allFsms[i];
                if (fsm == null || fsm.FsmName != "Screw") continue;

                fsm.Initialize();
                var stageVar = fsm.FsmVariables.FindFsmInt("Stage");
                var tightVar = stageVar == null ? fsm.FsmVariables.FindFsmFloat("Tightness") : null;
                var alignmentVar = fsm.FsmVariables.FindFsmFloat("Alignment");
                var rotVar = fsm.FsmVariables.FindFsmFloat("Rot");

                if (stageVar == null && tightVar == null && alignmentVar == null && rotVar == null) continue;

                int hash = SceneHasher.GetHash(fsm.gameObject);
                if (bolts.ContainsKey(hash)) continue;

                bool isTune = fsm.HasState("Setup") && alignmentVar != null;
                bool isLid = fsm.HasState("Wait") && !fsm.HasState("Wait 2") && !fsm.HasState("Wait 3") && !fsm.HasState("Wait 4") && rotVar != null;
                bool isPin = fsm.gameObject.name == "Pin";

                float maxAlign = 0f;
                if (isTune)
                {
                    var maxVar = fsm.FsmVariables.FindFsmFloat("Max");
                    maxAlign = maxVar != null ? maxVar.Value : 1f;
                }

                float rotAmt = 0f;
                if (isLid)
                {
                    var rotAmtVar = fsm.FsmVariables.FindFsmFloat("ScrewAmount");
                    rotAmt = rotAmtVar != null ? rotAmtVar.Value : 10f;
                }

                var bolt = new TrackedBolt
                {
                    Hash = hash,
                    Fsm = fsm,
                    StageVar = stageVar,
                    TightnessVar = tightVar,
                    AlignmentVar = alignmentVar,
                    RotVar = rotVar,
                    MaxAlignment = maxAlign,
                    RotAmount = rotAmt,
                    IsTuneBolt = isTune,
                    IsScrewableLid = isLid,
                    IsPin = isPin,
                    IsHooked = false
                };
                bolt.LastSyncedStage = bolt.CurrentStage;

                bolts[hash] = bolt;
                HookBoltFsm(bolt);
            }
        }

        private void HookBoltFsm(TrackedBolt bolt)
        {
            if (bolt.IsHooked || bolt.Fsm == null) return;
            bolt.Fsm.Initialize();
            int hash = bolt.Hash;

            Action onTightnessAction = () =>
            {
                if (suppressedBolts.Contains(hash)) return;

                byte currentStage = bolt.CurrentStage;
                if (currentStage != bolt.LastSyncedStage)
                {
                    bolt.LastSyncedStage = currentStage;
                    SendBoltScrew(hash, currentStage);
                }
            };

            // Хуки на стейты закручивания и откручивания
            string[] statesToHook = new string[] { "Wait 3", "Wait", "Wait 2", "Wait 4", "Stage up", "Stage down", "Setup", "Rotate" };
            for (int s = 0; s < statesToHook.Length; s++)
            {
                string st = statesToHook[s];
                if (bolt.Fsm.HasState(st))
                {
                    bolt.Fsm.InsertAction(st, onTightnessAction, 0, false);
                }
            }

            // Добавляем глобальные события MP для тихой синхронизации
            if (bolt.IsScrewableLid)
            {
                FsmEvent mpUnscrew = bolt.Fsm.AddEvent("MP_UNSCREW");
                FsmEvent mpScrew = bolt.Fsm.AddEvent("MP_SCREW");
                if (bolt.Fsm.HasState("Unscrew")) bolt.Fsm.AddGlobalTransition(mpUnscrew, "Unscrew");
                if (bolt.Fsm.HasState("Screw")) bolt.Fsm.AddGlobalTransition(mpScrew, "Screw");
            }
            else if (bolt.IsTuneBolt)
            {
                FsmEvent mpSetup = bolt.Fsm.AddEvent("MP_SETUP");
                bolt.Fsm.AddGlobalTransition(mpSetup, "Setup");
            }
            else if (bolt.IsPin)
            {
                FsmEvent mpTighten = bolt.Fsm.AddEvent("MP_TIGHTEN");
                FsmEvent mpUntighten = bolt.Fsm.AddEvent("MP_UNTIGHTEN");
                if (bolt.Fsm.HasState("1")) bolt.Fsm.AddGlobalTransition(mpTighten, "1");
                if (bolt.Fsm.HasState("0")) bolt.Fsm.AddGlobalTransition(mpUntighten, "0");
            }

            bolt.IsHooked = true;
        }

        private void InitRemovalParts()
        {
            var allFsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            for (int i = 0; i < allFsms.Length; i++)
            {
                var fsm = allFsms[i];
                if (fsm == null || fsm.FsmName != "Removal") continue;

                fsm.Initialize();
                if (!fsm.HasState("Remove part")) continue;

                int hash = SceneHasher.GetHash(fsm.gameObject);
                if (removalParts.ContainsKey(hash)) continue;

                FsmEvent mpRemove = fsm.AddEvent("MP_REMOVE");
                fsm.AddGlobalTransition(mpRemove, "Remove part");

                var part = new TrackedRemovalPart
                {
                    Hash = hash,
                    GameObject = fsm.gameObject,
                    Fsm = fsm,
                    MpRemoveEvent = mpRemove,
                    IsHooked = false
                };

                removalParts[hash] = part;
                HookRemovalFsm(part);
            }
        }

        private void HookRemovalFsm(TrackedRemovalPart part)
        {
            if (part.IsHooked || part.Fsm == null) return;
            part.Fsm.Initialize();
            int hash = part.Hash;

            part.Fsm.InsertAction("Remove part", () =>
            {
                // При локальном демонтаже детали включаем ее коллайдеры и физику
                if (part.GameObject != null)
                {
                    var colls = part.GameObject.GetComponentsInChildren<Collider>(true);
                    for (int c = 0; c < colls.Length; c++)
                    {
                        if (colls[c] != null) colls[c].enabled = true;
                    }
                    var rb = part.GameObject.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = false;
                        rb.useGravity = true;
                        rb.WakeUp();
                    }
                }

                if (suppressedParts.Contains(hash)) return;
                SendPartRemove(hash);
                OnLog?.Invoke($"[Сборка] Деталь {part.GameObject.name} демонтирована.");
            }, 0, false);

            part.IsHooked = true;
        }

        private void InitAssembleTriggers()
        {
            var allFsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            for (int i = 0; i < allFsms.Length; i++)
            {
                var fsm = allFsms[i];
                if (fsm == null || fsm.gameObject == null) continue;

                bool isAssemblyFsm = fsm.FsmName == "Assembly";
                bool isAssembleFsm = fsm.FsmName == "Assemble";
                if (!isAssemblyFsm && !isAssembleFsm) continue;

                bool isInsert = isAssemblyFsm && fsm.gameObject.name == "Insert";
                bool isTriggerCharger = isAssemblyFsm && fsm.gameObject.name == "TriggerCharger";
                if (isAssemblyFsm && !isInsert && !isTriggerCharger) continue;

                fsm.Initialize();
                bool isWiring = false;
                string assembleState = null;
                FsmGameObject partVar = null;

                if (isInsert)
                {
                    if (!fsm.HasState("Add battery")) continue;
                    assembleState = "Add battery";
                    partVar = fsm.FsmVariables.FindFsmGameObject("Part");
                }
                else if (isTriggerCharger)
                {
                    if (!fsm.HasState("Init")) continue;
                    assembleState = "Init";
                    partVar = fsm.FsmVariables.FindFsmGameObject("Battery");
                }
                else
                {
                    isWiring = fsm.FsmGlobalTransitions != null && fsm.FsmGlobalTransitions.Any(t => t != null && t.EventName == "RESETWIRING");
                    if (isWiring)
                    {
                        // Для проводки стейтом фиксации соединения является "Sound"
                        assembleState = fsm.HasState("Sound") ? "Sound" : (fsm.HasState("Assemble") ? "Assemble" : null);
                    }
                    else
                    {
                        FsmTransition assembleTransition = null;
                        if (fsm.FsmStates != null && fsm.FsmStates.Length > 0 && fsm.FsmStates[0].Transitions != null)
                        {
                            assembleTransition = fsm.FsmStates[0].Transitions.FirstOrDefault(t => t != null && t.EventName != null && t.EventName.Contains("ASSEMBL"));
                        }

                        if (assembleTransition != null && !string.IsNullOrEmpty(assembleTransition.ToState))
                        {
                            assembleState = assembleTransition.ToState;
                        }
                        else if (fsm.FsmStates != null)
                        {
                            var state = fsm.FsmStates.FirstOrDefault(s => s != null && !string.IsNullOrEmpty(s.Name) && s.Name.IndexOf("assemble", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (state != null)
                            {
                                assembleState = state.Name;
                            }
                        }
                    }

                    partVar = fsm.FsmVariables.FindFsmGameObject("Part");
                }

                if (string.IsNullOrEmpty(assembleState)) continue;

                int hash = SceneHasher.GetHash(fsm.gameObject);
                if (assembleTriggers.ContainsKey(hash)) continue;

                var attachForce = fsm.FsmVariables.FindFsmFloat("InitialAttachForce");
                if (attachForce != null) attachForce.Value = float.MaxValue;

                FsmEvent mpAssemble = null;
                if (!isTriggerCharger)
                {
                    mpAssemble = fsm.AddSafeEvent("MP_ASSEMBLE");
                    fsm.AddSafeGlobalTransition(mpAssemble, assembleState);
                }

                var trigger = new TrackedAssembleTrigger
                {
                    Hash = hash,
                    GameObject = fsm.gameObject,
                    Fsm = fsm,
                    PartVar = partVar,
                    MpAssembleEvent = mpAssemble,
                    IsHooked = false,
                    IsWiring = isWiring,
                    IsTriggerCharger = isTriggerCharger
                };

                assembleTriggers[hash] = trigger;
                HookAssembleFsm(trigger, assembleState);
            }
        }

        private void HookAssembleFsm(TrackedAssembleTrigger trigger, string assembleState)
        {
            if (trigger.IsHooked || trigger.Fsm == null) return;
            trigger.Fsm.Initialize();
            int triggerHash = trigger.Hash;

            Action onAssembledAction = () =>
            {
                var attachForce = trigger.Fsm.FsmVariables.FindFsmFloat("InitialAttachForce");
                if (attachForce != null) attachForce.Value = float.MaxValue;

                GameObject assembledPart = null;
                int partHash = 0;
                if (trigger.PartVar != null && trigger.PartVar.Value != null)
                {
                    assembledPart = trigger.PartVar.Value;
                    partHash = SceneHasher.GetHash(assembledPart);
                }

                // Исключаем деталь из физики рук и трекеров интерполяции ItemManager после отработки ванильной сборки
                Items.ItemManager.Instance?.OnPartAssembled(assembledPart, partHash);

                if (suppressedParts.Contains(triggerHash)) return;

                SendPartAssemble(triggerHash, partHash);
                OnLog?.Invoke($"[Сборка] Деталь установлена в слот {trigger.GameObject.name}.");
            };

            FsmState[] assembleStates = trigger.Fsm.FsmStates != null
                ? trigger.Fsm.FsmStates.Where(s => s != null && !string.IsNullOrEmpty(s.Name) && s.Name.IndexOf("assemble", StringComparison.OrdinalIgnoreCase) >= 0).ToArray()
                : new FsmState[0];

            // Вставляем хук строго в КОНЕЦ стейта (index = -1, ПОСЛЕ всех ванильных действий PlayMaker),
            // как в WreckMP NetPartManager.cs:534
            if (trigger.IsWiring || trigger.IsTriggerCharger || trigger.Fsm.FsmName == "Assembly" || assembleStates.Length == 0)
            {
                trigger.Fsm.InsertAction(assembleState, onAssembledAction, -1, false);
            }
            else
            {
                for (int i = 0; i < assembleStates.Length; i++)
                {
                    trigger.Fsm.InsertAction(assembleStates[i].Name, onAssembledAction, -1, false);
                }
            }

            trigger.IsHooked = true;
        }

        private void SendBoltScrew(int boltHash, byte stage)
        {
            using (var packet = new PacketWriter(PacketType.PartBoltScrew))
            {
                packet.WriteInt32(boltHash);
                packet.WriteByte(stage);
                transport.Send(packet);
            }
        }

        private void SendPartRemove(int partHash)
        {
            using (var packet = new PacketWriter(PacketType.PartRemove))
            {
                packet.WriteInt32(partHash);
                transport.Send(packet);
            }
        }

        private void SendPartAssemble(int triggerHash, int partHash)
        {
            using (var packet = new PacketWriter(PacketType.PartAssemble))
            {
                packet.WriteInt32(triggerHash);
                packet.WriteInt32(partHash);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.PartBoltScrew:
                    HandleBoltScrew(reader, connectionId);
                    break;

                case PacketType.PartRemove:
                    HandlePartRemove(reader, connectionId);
                    break;

                case PacketType.PartAssemble:
                    HandlePartAssemble(reader, connectionId);
                    break;

                case PacketType.PartAssembleSnapshot:
                    HandleSnapshot(reader);
                    break;
            }
        }

        private void HandleBoltScrew(PacketReader reader, int connectionId)
        {
            int boltHash = reader.ReadInt32();
            byte stage = reader.ReadByte();

            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.PartBoltScrew))
                {
                    relay.WriteInt32(boltHash);
                    relay.WriteByte(stage);
                    transport.Send(relay, connectionId);
                }
            }

            ApplyRemoteBoltScrew(boltHash, stage);
        }

        private void ApplyRemoteBoltScrew(int boltHash, byte stage)
        {
            suppressedBolts.Add(boltHash);
            try
            {
                if (bolts.TryGetValue(boltHash, out var bolt) && bolt.Fsm != null)
                {
                    if (bolt.IsTuneBolt)
                    {
                        if (bolt.AlignmentVar != null)
                        {
                            bolt.AlignmentVar.Value = (float)stage / 255f * bolt.MaxAlignment;
                            bolt.Fsm.SendEvent("MP_SETUP");
                        }
                    }
                    else if (bolt.IsScrewableLid)
                    {
                        if (bolt.RotVar != null)
                        {
                            float targetAngle = (float)stage / 255f * 360f;
                            bool isScrew = targetAngle > bolt.RotVar.Value;
                            bolt.RotVar.Value = isScrew ? (targetAngle - bolt.RotAmount) : (targetAngle + bolt.RotAmount);
                            if (!isScrew && targetAngle < 5f)
                            {
                                bolt.RotVar.Value = bolt.RotAmount;
                            }
                            bolt.Fsm.SendEvent(isScrew ? "MP_SCREW" : "MP_UNSCREW");
                        }
                    }
                    else if (bolt.IsPin)
                    {
                        bolt.Fsm.SendEvent(stage > 0 ? "MP_TIGHTEN" : "MP_UNTIGHTEN");
                    }
                    else
                    {
                        // Обычный болт: тихое обновление стейта
                        if (bolt.StageVar != null)
                        {
                            bolt.StageVar.Value = stage;
                        }
                        else if (bolt.TightnessVar != null)
                        {
                            bolt.TightnessVar.Value = stage;
                        }

                        // Если болт закручен (stage > 0), нативная игра при проверке стейта болтов блокирует снятие детали
                        // Для плавной анимации модели болта без звука на всю карту отправляем нативное событие
                        if (bolt.StageVar != null && bolt.Fsm.HasState("Wait 3"))
                        {
                            bolt.Fsm.SendEvent("WAIT");
                        }
                    }

                    bolt.LastSyncedStage = stage;
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[Bolt Apply Error] {ex.Message}");
            }
            finally
            {
                suppressedBolts.Remove(boltHash);
            }
        }

        private void HandlePartRemove(PacketReader reader, int connectionId)
        {
            int partHash = reader.ReadInt32();

            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.PartRemove))
                {
                    relay.WriteInt32(partHash);
                    transport.Send(relay, connectionId);
                }
            }

            ApplyRemotePartRemove(partHash);
        }

        private void ApplyRemotePartRemove(int partHash)
        {
            suppressedParts.Add(partHash);
            try
            {
                if (removalParts.TryGetValue(partHash, out var part) && part.Fsm != null)
                {
                    part.Fsm.SendEvent("MP_REMOVE");

                    if (part.GameObject != null)
                    {
                        var colls = part.GameObject.GetComponentsInChildren<Collider>(true);
                        for (int c = 0; c < colls.Length; c++)
                        {
                            if (colls[c] != null) colls[c].enabled = true;
                        }
                        var rb = part.GameObject.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            rb.isKinematic = false;
                            rb.useGravity = true;
                            rb.WakeUp();
                        }
                    }

                    OnLog?.Invoke($"[Сборка] Другой игрок демонтировал деталь {part.GameObject.name}.");
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[Part Remove Apply Error] {ex.Message}");
            }
            finally
            {
                suppressedParts.Remove(partHash);
            }
        }

        private void HandlePartAssemble(PacketReader reader, int connectionId)
        {
            int triggerHash = reader.ReadInt32();
            int partHash = reader.ReadInt32();

            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.PartAssemble))
                {
                    relay.WriteInt32(triggerHash);
                    relay.WriteInt32(partHash);
                    transport.Send(relay, connectionId);
                }
            }

            ApplyRemotePartAssemble(triggerHash, partHash);
        }

        private void ApplyRemotePartAssemble(int triggerHash, int partHash)
        {
            if (!TryApplyPartAssemble(triggerHash, partHash, false))
            {
                // Если родительский агрегат (коленвал, блок) еще не встал или объект еще не активен,
                // откладываем сборку в очередь pendingParts на следующий тик/кадр
                pendingParts.Add(new PendingAssemblePart
                {
                    TriggerHash = triggerHash,
                    PartHash = partHash,
                    RetryTime = Time.time + 0.05f,
                    RetryCount = 0
                });
            }
        }

        private bool TryApplyPartAssemble(int triggerHash, int partHash, bool isRetry)
        {
            if (!assembleTriggers.TryGetValue(triggerHash, out var trigger) || trigger.Fsm == null)
            {
                return false;
            }

            suppressedParts.Add(triggerHash);
            try
            {
                var attachForce = trigger.Fsm.FsmVariables.FindFsmFloat("InitialAttachForce");
                if (attachForce != null) attachForce.Value = float.MaxValue;

                GameObject partObj = null;
                if (partHash != 0)
                {
                    partObj = SceneHasher.FindObjectByHash(partHash);
                    if (partObj == null && !isRetry)
                    {
                        // Объект еще не зарегистрирован/не найден в сцене
                        return false;
                    }

                    if (partObj != null)
                    {
                        if (!partObj.activeSelf)
                        {
                            partObj.SetActive(true);
                        }

                        if (trigger.PartVar != null)
                        {
                            trigger.PartVar.Value = partObj;
                        }

                        var rb = partObj.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            rb.isKinematic = true;
                            rb.velocity = Vector3.zero;
                            rb.angularVelocity = Vector3.zero;
                        }

                        // Отключаем собственные коллизии установленной детали с кузовом/игроком
                        var colls = partObj.GetComponentsInChildren<Collider>(true);
                        for (int c = 0; c < colls.Length; c++)
                        {
                            if (colls[c] != null && !colls[c].isTrigger)
                            {
                                colls[c].enabled = false;
                            }
                        }
                    }
                }

                // Гарантируем, что trigger.PartVar.Value действительно назначен
                if (trigger.PartVar != null && trigger.PartVar.Value != null)
                {
                    if (!trigger.PartVar.Value.activeSelf)
                    {
                        trigger.PartVar.Value.SetActive(true);
                    }

                    var rb = trigger.PartVar.Value.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = true;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }

                    if (partObj == null)
                    {
                        partObj = trigger.PartVar.Value;
                    }

                    var colls = trigger.PartVar.Value.GetComponentsInChildren<Collider>(true);
                    for (int c = 0; c < colls.Length; c++)
                    {
                        if (colls[c] != null && !colls[c].isTrigger)
                        {
                            colls[c].enabled = false;
                        }
                    }
                }

                // Исключаем деталь из физики рук и трекеров интерполяции ItemManager
                Items.ItemManager.Instance?.OnPartAssembled(partObj, partHash);

                trigger.Fsm.SendEvent(trigger.IsTriggerCharger ? "PLACEBATT" : "MP_ASSEMBLE");
                OnLog?.Invoke($"[Сборка] Другой игрок установил деталь в слот {trigger.GameObject.name}.");
                return true;
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[Part Assemble Apply Error] {ex.Message}");
                return false;
            }
            finally
            {
                suppressedParts.Remove(triggerHash);
            }
        }

        private void HandleClientConnected(int clientId)
        {
            if (transport.IsServer)
            {
                SendSnapshotToClient(clientId);
            }
        }

        private void SendSnapshotToClient(int targetClientId)
        {
            using (var packet = new PacketWriter(PacketType.PartAssembleSnapshot))
            {
                // Снимок затянутых болтов
                var tightenedBolts = bolts.Values.Where(b => b.LastSyncedStage > 0).ToList();
                packet.WriteInt32(tightenedBolts.Count);
                for (int i = 0; i < tightenedBolts.Count; i++)
                {
                    packet.WriteInt32(tightenedBolts[i].Hash);
                    packet.WriteByte(tightenedBolts[i].LastSyncedStage);
                }

                transport.Send(packet, targetClientId);
            }
        }

        private void HandleSnapshot(PacketReader reader)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int bHash = reader.ReadInt32();
                byte stage = reader.ReadByte();
                ApplyRemoteBoltScrew(bHash, stage);
            }
            OnLog?.Invoke($"[Сборка] Получен снимок затяжки {count} болтов.");
        }
    }
}
