using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.WorldEconomyJobs
{
    /// <summary>
    /// Синхронизация кооперативного сохранения в туалете (SAVEGAME FSM, чанки <= 24 КБ, CRC32,
    /// атомарное создание .bak на клиенте, сохранение личных параметров игрока)
    /// и кооперативной смерти/возрождения у церкви/больницы без разрыва сессии.
    /// Использует PacketType.SavegameAndRespawnSync (135).
    /// </summary>
    internal sealed class SavegameAndRespawnSync
    {
        private enum SaveNoticeReason : byte
        {
            HostToiletSave = 1,
            ClientJoinStatus = 2,
            ManualSaveSync = 3
        }

        private enum SaveCompleteStatus : byte
        {
            HostFinishedSending = 1,
            ClientAppliedOk = 2,
            ClientCrcMismatchFailed = 3
        }

        private enum RespawnLocationKind : byte
        {
            Church = 1,
            Hospital = 2,
            HomeFallback = 3
        }

        private sealed class SaveFileEntry
        {
            public string FileName;
            public byte[] Data;
            public uint Crc32;
            public int TotalChunks;
        }

        private sealed class OutgoingSaveTransfer
        {
            public int TransferId;
            public int TargetClientId;
            public SaveNoticeReason Reason;
            public readonly List<SaveFileEntry> Files = new List<SaveFileEntry>();
            public int CurrentFileIndex;
            public int CurrentChunkIndex;
            public int TotalBytes;
            public uint GlobalCrc32;
        }

        private sealed class IncomingFileBuffer
        {
            public string FileName;
            public int TotalLength;
            public uint ExpectedCrc32;
            public int TotalChunks;
            public int ReceivedChunks;
            public byte[][] Chunks;
        }

        private sealed class IncomingSaveTransfer
        {
            public int TransferId;
            public int ExpectedFiles;
            public int ExpectedTotalBytes;
            public uint ExpectedGlobalCrc32;
            public readonly Dictionary<string, IncomingFileBuffer> Files = new Dictionary<string, IncomingFileBuffer>();
        }

        private sealed class PersonalSaveSnapshot
        {
            public bool HasHunger; public float Hunger;
            public bool HasThirst; public float Thirst;
            public bool HasFatigue; public float Fatigue;
            public bool HasFatigueRate; public float FatigueRate;
            public bool HasUrine; public float Urine;
            public bool HasDirtiness; public float Dirtiness;
            public bool HasStress; public float Stress;
            public bool HasStressRate; public float StressRate;
            public bool HasDrunk; public float Drunk;
            public bool HasCigarettes; public int Cigarettes;
            public bool HasStatsCigarettes; public int StatsCigarettes;
            public bool HasWeight; public float Weight;
            public bool HasTattoo; public int Tattoo;
            public bool HasFirstName; public string FirstName;
            public bool HasLastName; public string LastName;
        }

        private sealed class PendingJoinNotice
        {
            public int ClientId;
            public float SendAtTime;
        }

        private static readonly string[] SyncedSaveFileNames = new string[]
        {
            "defaultES2File.txt",
            "items.txt",
            "meshsave.txt"
        };

        private static readonly string[] ForbiddenDeathParentKeywords = new string[]
        {
            "SATSUMA", "HAYOSIKO", "GIFU", "FERNDALE", "RUSCKO", "KEKMET",
            "JONNEZ", "BOAT", "MOOSE", "HEADGASKET", "PISTON", "FAN BELT",
            "ROCKERSHAFT", "STARTER", "DRIVERHEADPIVOT"
        };

        private static readonly string[] DestructiveDeathStateNames = new string[]
        {
            "Load", "Load menu", "LoadMenu", "MainMenu", "Menu", "Quit", "Restart", "Delete", "DeleteSave", "Permadeath"
        };

        private static readonly string[] DeathTriggerStateNames = new string[]
        {
            "Activate", "Death", "Dead", "Fade", "Newspaper", "Play", "State 1"
        };

        private static readonly uint[] Crc32Table = BuildCrc32Table();

        private const int MaxChunkPayloadBytes = 20 * 1024; // 20 КБ (итоговый пакет <= 24 КБ)
        private const int ChunksPerFrame = 4;
        private const int IoBufferBytes = 8192;
        private const float ScanIntervalSeconds = 4.0f;
        private const float HostSaveFlushDelaySeconds = 0.45f;
        private const float DeathInterceptCooldownSeconds = 3.0f;

        private static readonly Vector3 FallbackChurchPos = new Vector3(-1560.0f, 3.5f, 1185.0f);
        private static readonly Vector3 FallbackHospitalPos = new Vector3(-1512.0f, 3.5f, 1198.0f);
        private static readonly Vector3 FallbackHomePos = new Vector3(-14.0f, 0.5f, 10.0f);

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly HashSet<int> hookedFsmIds = new HashSet<int>();
        private readonly List<PlayMakerFSM> deathFsms = new List<PlayMakerFSM>();
        private readonly List<GameObject> deathObjects = new List<GameObject>();
        private readonly List<PendingJoinNotice> pendingJoinNotices = new List<PendingJoinNotice>();

        private PlayMakerFSM savegameFsm;
        private bool pendingHostToiletFlush;
        private float hostFlushReadyAtTime;
        private DateTime lastEs2WriteTimeUtc = DateTime.MinValue;
        private int nextTransferId = 1;

        private OutgoingSaveTransfer activeOutgoing;
        private IncomingSaveTransfer activeIncoming;
        private string lastSaveStatus = "Готов";
        private int totalSavesSynced;

        private float lastDeathInterceptTime = -10f;
        private int localDeathsIntercepted;
        private int remoteDeathsObserved;
        private bool preferHospitalNext;

        private float nextScanTime;

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private bool IsServer => ctx != null && ctx.Transport != null && ctx.Transport.IsServer;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public string StatusSummary =>
            $"Сейв: {lastSaveStatus} (синхр.: {totalSavesSynced}), Смертей спасено: {localDeathsIntercepted}, Напарник: {remoteDeathsObserved}";

        public SavegameAndRespawnSync(OscHostContext context, string moduleId)
        {
            ctx = context;
            this.moduleId = moduleId;
        }

        public void OnLevelLoaded()
        {
            Reset();
            ScanAll();
        }

        public void Reset()
        {
            hookedFsmIds.Clear();
            deathFsms.Clear();
            deathObjects.Clear();
            pendingJoinNotices.Clear();

            savegameFsm = null;
            pendingHostToiletFlush = false;
            hostFlushReadyAtTime = 0f;
            lastEs2WriteTimeUtc = GetEs2LastWriteTimeUtc();

            activeOutgoing = null;
            activeIncoming = null;
            lastSaveStatus = "Готов";
            lastDeathInterceptTime = -10f;
            nextScanTime = 0f;
        }

        public void OnClientConnected(int clientId)
        {
            if (!IsNetworkRunning || !IsServer) return;

            SendHostJoinSaveNotice(clientId);

            for (int i = pendingJoinNotices.Count - 1; i >= 0; i--)
            {
                if (pendingJoinNotices[i].ClientId == clientId)
                {
                    pendingJoinNotices.RemoveAt(i);
                }
            }

            pendingJoinNotices.Add(new PendingJoinNotice
            {
                ClientId = clientId,
                SendAtTime = Time.time + 2.0f
            });
        }

        public void OnClientDisconnected(int clientId)
        {
            for (int i = pendingJoinNotices.Count - 1; i >= 0; i--)
            {
                if (pendingJoinNotices[i].ClientId == clientId)
                {
                    pendingJoinNotices.RemoveAt(i);
                }
            }

            if (activeOutgoing != null && (activeOutgoing.TargetClientId == clientId || !IsNetworkRunning))
            {
                activeOutgoing = null;
            }

            if (!IsServer)
            {
                activeIncoming = null;
            }
        }

        public void OnUpdate()
        {
            float now = Time.time;

            if (now >= nextScanTime)
            {
                nextScanTime = now + ScanIntervalSeconds;
                ScanAll();
            }

            // Страховочная проверка прямой активации объекта Death без входа в перехваченное состояние
            CheckActiveDeathObjects(now);

            if (!IsNetworkRunning) return;

            if (IsServer)
            {
                ProcessPendingJoinNotices(now);
                ProcessPendingHostSaveFlush(now);
                PumpOutgoingSaveChunks();
            }
        }

        #region Discovery & Hooks

        private void ScanAll()
        {
            try
            {
                ScanSavegameFsm();
                ScanDeathFsms();
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Сейв/Возрождение] Ошибка сканирования: {ex.Message}");
            }
        }

        private void ScanSavegameFsm()
        {
            if (savegameFsm != null) return;

            PlayMakerFSM candidate = null;
            GameObject saveGo = FindObjectIncludingInactive("SAVEGAME") ?? FindObjectIncludingInactive("Savegame");
            if (saveGo != null)
            {
                candidate = PlayMakerBridge.FindFsm(saveGo, "SAVEGAME")
                    ?? PlayMakerBridge.FindFsm(saveGo, "Save")
                    ?? saveGo.GetComponent<PlayMakerFSM>();
            }

            if (candidate == null)
            {
                PlayMakerFSM[] allFsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
                for (int i = 0; i < allFsms.Length; i++)
                {
                    PlayMakerFSM fsm = allFsms[i];
                    if (fsm == null || fsm.gameObject == null) continue;
                    if (string.Equals(fsm.FsmName, "SAVEGAME", StringComparison.OrdinalIgnoreCase) ||
                        fsm.gameObject.name.IndexOf("SAVEGAME", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        candidate = fsm;
                        break;
                    }
                }
            }

            if (candidate == null) return;
            if (!TryClaimSafe(candidate, "ToiletSavegame")) return;

            savegameFsm = candidate;
            savegameFsm.InitializeSafe();

            HookFsmStateOnce(savegameFsm, "Save", OnToiletSaveTriggered);
            HookFsmStateOnce(savegameFsm, "Save game", OnToiletSaveTriggered);
            HookFsmStateOnce(savegameFsm, "Saving", OnToiletSaveTriggered);
            HookFsmStateOnce(savegameFsm, "Yes", OnToiletSaveTriggered);
        }

        private void ScanDeathFsms()
        {
            PlayMakerFSM[] allFsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            for (int i = 0; i < allFsms.Length; i++)
            {
                PlayMakerFSM fsm = allFsms[i];
                if (fsm == null || fsm.gameObject == null) continue;
                if (!IsPlayerDeathFsm(fsm)) continue;

                if (!deathObjects.Contains(fsm.gameObject))
                {
                    deathObjects.Add(fsm.gameObject);
                }

                if (deathFsms.Contains(fsm)) continue;
                if (!TryClaimSafe(fsm, "CoopPlayerDeath")) continue;

                fsm.InitializeSafe();
                deathFsms.Add(fsm);

                NeutralizeDestructiveDeathActions(fsm);

                string idleState = GetDeathIdleStateName(fsm);
                if (!string.IsNullOrEmpty(idleState))
                {
                    fsm.AddSafeGlobalTransition("MP_COOP_RESPAWN", idleState);
                }

                for (int s = 0; s < DeathTriggerStateNames.Length; s++)
                {
                    string stateName = DeathTriggerStateNames[s];
                    if (string.Equals(stateName, idleState, StringComparison.OrdinalIgnoreCase)) continue;
                    HookFsmStateOnce(fsm, stateName, OnLocalDeathIntercepted);
                }
            }
        }

        private static bool IsPlayerDeathFsm(PlayMakerFSM fsm)
        {
            string goName = fsm.gameObject.name ?? string.Empty;
            string fsmName = fsm.FsmName ?? string.Empty;

            bool nameMatch = string.Equals(goName, "Death", StringComparison.OrdinalIgnoreCase) ||
                             (string.Equals(fsmName, "Death", StringComparison.OrdinalIgnoreCase) &&
                              (string.Equals(goName, "PLAYER", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(goName, "Systems", StringComparison.OrdinalIgnoreCase) ||
                               goName.IndexOf("Death", StringComparison.OrdinalIgnoreCase) >= 0));
            if (!nameMatch) return false;

            Transform t = fsm.transform;
            while (t != null)
            {
                string upper = (t.name ?? string.Empty).ToUpper();
                for (int i = 0; i < ForbiddenDeathParentKeywords.Length; i++)
                {
                    if (upper.Contains(ForbiddenDeathParentKeywords[i]))
                    {
                        return false;
                    }
                }
                t = t.parent;
            }

            return true;
        }

        private static void NeutralizeDestructiveDeathActions(PlayMakerFSM fsm)
        {
            if (fsm == null || fsm.FsmStates == null) return;

            for (int i = 0; i < fsm.FsmStates.Length; i++)
            {
                FsmState state = fsm.FsmStates[i];
                if (state == null || state.Actions == null) continue;

                bool isDestructiveState = false;
                for (int k = 0; k < DestructiveDeathStateNames.Length; k++)
                {
                    if (string.Equals(state.Name, DestructiveDeathStateNames[k], StringComparison.OrdinalIgnoreCase))
                    {
                        isDestructiveState = true;
                        break;
                    }
                }

                for (int a = 0; a < state.Actions.Length; a++)
                {
                    FsmStateAction act = state.Actions[a];
                    if (act == null) continue;
                    string typeName = act.GetType().Name;
                    if (isDestructiveState ||
                        typeName.IndexOf("LoadLevel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("RestartLevel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("ApplicationQuit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("DeleteFile", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("ES2Delete", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        act.Enabled = false;
                    }
                }
            }
        }

        private static string GetDeathIdleStateName(PlayMakerFSM fsm)
        {
            if (fsm == null || fsm.FsmStates == null || fsm.FsmStates.Length == 0) return string.Empty;
            if (fsm.HasState("Wait")) return "Wait";
            if (fsm.HasState("Idle")) return "Idle";
            if (fsm.HasState("Disabled")) return "Disabled";
            return fsm.FsmStates[0] != null ? fsm.FsmStates[0].Name : string.Empty;
        }

        #endregion

        #region Toilet Save Sync (Host -> Client)

        private void OnToiletSaveTriggered()
        {
            if (!IsNetworkRunning) return;

            if (!IsServer)
            {
                ctx?.Log?.Invoke("[Сохранения] Авторитативен сейв хоста — ожидайте сохранения напарником.");
                return;
            }

            pendingHostToiletFlush = true;
            hostFlushReadyAtTime = Time.time + HostSaveFlushDelaySeconds;
            lastSaveStatus = "Подготовка сейва хоста...";
            ctx?.Log?.Invoke("[Сохранения] Хост активировал туалет (SAVEGAME). Ожидание записи файлов на диск...");
        }

        private void ProcessPendingJoinNotices(float now)
        {
            for (int i = pendingJoinNotices.Count - 1; i >= 0; i--)
            {
                if (now >= pendingJoinNotices[i].SendAtTime)
                {
                    int clientId = pendingJoinNotices[i].ClientId;
                    pendingJoinNotices.RemoveAt(i);
                    SendHostJoinSaveNotice(clientId);
                }
            }
        }

        private void ProcessPendingHostSaveFlush(float now)
        {
            if (!pendingHostToiletFlush) return;

            DateTime currentWrite = GetEs2LastWriteTimeUtc();
            bool fileUpdated = currentWrite > lastEs2WriteTimeUtc;
            if (!fileUpdated && now < hostFlushReadyAtTime) return;
            if (now < hostFlushReadyAtTime + 0.15f && !fileUpdated) return;

            pendingHostToiletFlush = false;
            lastEs2WriteTimeUtc = currentWrite;
            BeginOutgoingSaveTransfer(targetClientId: -1, reason: SaveNoticeReason.HostToiletSave);
        }

        private void SendHostJoinSaveNotice(int clientId)
        {
            if (!IsNetworkRunning || !IsServer) return;

            int fileCount = 0;
            int totalBytes = 0;
            uint combinedCrc = 0;

            string dir = Application.persistentDataPath;
            for (int i = 0; i < SyncedSaveFileNames.Length; i++)
            {
                string fullPath = Path.Combine(dir, SyncedSaveFileNames[i]);
                if (!File.Exists(fullPath)) continue;

                try
                {
                    byte[] raw = ReadAllBytesSafe(fullPath);
                    if (raw.Length > 0)
                    {
                        fileCount++;
                        totalBytes += raw.Length;
                        combinedCrc ^= ComputeCrc32(raw, 0, raw.Length);
                    }
                }
                catch { }
            }

            using (var packet = new PacketWriter(PacketType.SavegameAndRespawnSync))
            {
                packet.WriteByte(EconomyProtocol.SaveTriggerNotice);
                packet.WriteInt32(0);
                packet.WriteByte((byte)SaveNoticeReason.ClientJoinStatus);
                packet.WriteInt32(fileCount);
                packet.WriteInt32(totalBytes);
                packet.WriteInt32(unchecked((int)combinedCrc));
                ctx.Transport.SendTo(packet, clientId);
            }
        }

        private void BeginOutgoingSaveTransfer(int targetClientId, SaveNoticeReason reason)
        {
            string dir = Application.persistentDataPath;
            var transfer = new OutgoingSaveTransfer
            {
                TransferId = nextTransferId++,
                TargetClientId = targetClientId,
                Reason = reason
            };

            uint globalCrc = 0;
            for (int i = 0; i < SyncedSaveFileNames.Length; i++)
            {
                string fileName = SyncedSaveFileNames[i];
                string fullPath = Path.Combine(dir, fileName);
                if (!File.Exists(fullPath)) continue;

                try
                {
                    byte[] data = ReadAllBytesSafe(fullPath);
                    if (data.Length == 0) continue;

                    uint fileCrc = ComputeCrc32(data, 0, data.Length);
                    int chunks = (data.Length + MaxChunkPayloadBytes - 1) / MaxChunkPayloadBytes;
                    if (chunks < 1) chunks = 1;

                    transfer.Files.Add(new SaveFileEntry
                    {
                        FileName = fileName,
                        Data = data,
                        Crc32 = fileCrc,
                        TotalChunks = chunks
                    });

                    transfer.TotalBytes += data.Length;
                    globalCrc ^= fileCrc;
                }
                catch (Exception ex)
                {
                    ctx?.Log?.Invoke($"[Сохранения] Не удалось прочитать '{fileName}': {ex.Message}");
                }
            }

            transfer.GlobalCrc32 = globalCrc;

            if (transfer.Files.Count == 0)
            {
                lastSaveStatus = "Нет файлов сохранения";
                ctx?.Log?.Invoke("[Сохранения] Файлы сохранения не найдены в persistentDataPath.");
                return;
            }

            using (var notice = new PacketWriter(PacketType.SavegameAndRespawnSync))
            {
                notice.WriteByte(EconomyProtocol.SaveTriggerNotice);
                notice.WriteInt32(transfer.TransferId);
                notice.WriteByte((byte)transfer.Reason);
                notice.WriteInt32(transfer.Files.Count);
                notice.WriteInt32(transfer.TotalBytes);
                notice.WriteInt32(unchecked((int)transfer.GlobalCrc32));
                SendOutgoingPacket(notice, targetClientId);
            }

            activeOutgoing = transfer;
            lastSaveStatus = $"Отправка ({transfer.TotalBytes / 1024} КБ)...";
            ctx?.Log?.Invoke($"[Сохранения] Начата отправка сохранения #{transfer.TransferId}: {transfer.Files.Count} файл(ов), {transfer.TotalBytes / 1024} КБ.");
        }

        private void PumpOutgoingSaveChunks()
        {
            if (activeOutgoing == null) return;

            int sentThisFrame = 0;
            while (sentThisFrame < ChunksPerFrame && activeOutgoing != null)
            {
                if (activeOutgoing.CurrentFileIndex >= activeOutgoing.Files.Count)
                {
                    FinishOutgoingTransfer(activeOutgoing);
                    activeOutgoing = null;
                    break;
                }

                SaveFileEntry file = activeOutgoing.Files[activeOutgoing.CurrentFileIndex];
                int chunkIdx = activeOutgoing.CurrentChunkIndex;

                int offset = chunkIdx * MaxChunkPayloadBytes;
                int length = Mathf.Min(MaxChunkPayloadBytes, file.Data.Length - offset);
                byte[] slice = new byte[length];
                Buffer.BlockCopy(file.Data, offset, slice, 0, length);

                using (var packet = new PacketWriter(PacketType.SavegameAndRespawnSync))
                {
                    packet.WriteByte(EconomyProtocol.SaveFileChunk);
                    packet.WriteInt32(activeOutgoing.TransferId);
                    packet.WriteString(file.FileName);
                    packet.WriteInt32(file.Data.Length);
                    packet.WriteInt32(unchecked((int)file.Crc32));
                    packet.WriteInt32(chunkIdx);
                    packet.WriteInt32(file.TotalChunks);
                    packet.WriteByteArray(slice);
                    SendOutgoingPacket(packet, activeOutgoing.TargetClientId);
                }

                sentThisFrame++;
                activeOutgoing.CurrentChunkIndex++;
                if (activeOutgoing.CurrentChunkIndex >= file.TotalChunks)
                {
                    activeOutgoing.CurrentFileIndex++;
                    activeOutgoing.CurrentChunkIndex = 0;
                }
            }
        }

        private void FinishOutgoingTransfer(OutgoingSaveTransfer transfer)
        {
            using (var done = new PacketWriter(PacketType.SavegameAndRespawnSync))
            {
                done.WriteByte(EconomyProtocol.SaveFileComplete);
                done.WriteInt32(transfer.TransferId);
                done.WriteByte((byte)SaveCompleteStatus.HostFinishedSending);
                done.WriteInt32(transfer.Files.Count);
                done.WriteInt32(transfer.TotalBytes);
                done.WriteString("Host transfer finished");
                SendOutgoingPacket(done, transfer.TargetClientId);
            }

            lastSaveStatus = "Отправлено клиенту";
        }

        private void SendOutgoingPacket(PacketWriter packet, int targetClientId)
        {
            if (targetClientId >= 0) ctx.Transport.SendTo(packet, targetClientId);
            else ctx.Transport.Send(packet);
        }

        #endregion

        #region Co-op Death & Respawn

        private void CheckActiveDeathObjects(float now)
        {
            if (now - lastDeathInterceptTime < DeathInterceptCooldownSeconds) return;

            for (int i = 0; i < deathObjects.Count; i++)
            {
                GameObject go = deathObjects[i];
                if (go != null && go.activeInHierarchy)
                {
                    OnLocalDeathIntercepted();
                    break;
                }
            }
        }

        private void OnLocalDeathIntercepted()
        {
            float now = Time.time;
            if (now - lastDeathInterceptTime < DeathInterceptCooldownSeconds) return;
            lastDeathInterceptTime = now;
            localDeathsIntercepted++;

            RespawnLocationKind locationKind;
            Vector3 spawnPos = ResolveRespawnPosition(out locationKind);
            float spawnYaw = (locationKind == RespawnLocationKind.Church) ? 180f : 0f;

            RecoverLocalPlayerAfterDeath(spawnPos, spawnYaw);

            string locText = locationKind == RespawnLocationKind.Church
                ? "церкви Перяярви"
                : (locationKind == RespawnLocationKind.Hospital ? "больницы" : "дома");

            ctx?.Log?.Invoke($"[Возрождение] Смерть перехвачена в кооперативе! Игрок возрождён у {locText} ({spawnPos.x:F1}, {spawnPos.y:F1}, {spawnPos.z:F1}).");

            if (IsNetworkRunning)
            {
                SendDeathAndRespawnNotices(locationKind, spawnPos);
            }

            ctx?.Events?.Publish(new PlayerRespawnedEvent
            {
                PlayerId = LocalPlayerId,
                IsLocal = true,
                SpawnPosition = spawnPos
            });
        }

        private Vector3 ResolveRespawnPosition(out RespawnLocationKind locationKind)
        {
            preferHospitalNext = !preferHospitalNext;

            if (!preferHospitalNext)
            {
                GameObject churchGo = GameObject.Find("CHURCH") ?? GameObject.Find("Church") ?? GameObject.Find("ChurchGrandma");
                Vector3 candidate = churchGo != null
                    ? churchGo.transform.position + new Vector3(0f, 0.5f, 6f)
                    : FallbackChurchPos;
                locationKind = RespawnLocationKind.Church;
                return SnapToGround(candidate);
            }

            GameObject hospitalGo = GameObject.Find("Hospital")
                ?? GameObject.Find("TERVEYSKESKUS")
                ?? GameObject.Find("HealthCenter")
                ?? GameObject.Find("SpawnToStore");
            if (hospitalGo != null)
            {
                locationKind = RespawnLocationKind.Hospital;
                return SnapToGround(hospitalGo.transform.position + new Vector3(2f, 0.5f, 2f));
            }

            GameObject yardGo = GameObject.Find("YARD");
            if (yardGo != null)
            {
                locationKind = RespawnLocationKind.Hospital;
                return SnapToGround(FallbackHospitalPos);
            }

            locationKind = RespawnLocationKind.HomeFallback;
            return SnapToGround(FallbackHomePos);
        }

        private static Vector3 SnapToGround(Vector3 candidate)
        {
            RaycastHit hit;
            Vector3 rayOrigin = new Vector3(candidate.x, candidate.y + 35f, candidate.z);
            if (Physics.Raycast(rayOrigin, Vector3.down, out hit, 120f))
            {
                return new Vector3(candidate.x, hit.point.y + 1.05f, candidate.z);
            }
            return candidate;
        }

        private void RecoverLocalPlayerAfterDeath(Vector3 spawnPos, float spawnYaw)
        {
            // 1. Сброс объектов/FSM смерти, восстановление времени и звука
            for (int i = 0; i < deathFsms.Count; i++)
            {
                PlayMakerFSM fsm = deathFsms[i];
                if (fsm != null)
                {
                    fsm.SendEvent("MP_COOP_RESPAWN");
                }
            }

            for (int i = 0; i < deathObjects.Count; i++)
            {
                GameObject go = deathObjects[i];
                if (go != null && string.Equals(go.name, "Death", StringComparison.OrdinalIgnoreCase) && go.activeSelf)
                {
                    go.SetActive(false);
                }
            }

            Time.timeScale = 1.0f;
            AudioListener.pause = false;
            if (AudioListener.volume <= 0.01f)
            {
                AudioListener.volume = 1.0f;
            }

            // 2. Сброс опасных глобальных переменных выживания
            ResetSurvivalGlobalsAfterDeath();

            // 3. Перенос игрока на точку возрождения (церковь/больница)
            GameObject playerGo = GameObject.Find("PLAYER") ?? GameObject.FindWithTag("Player");
            if (playerGo != null)
            {
                if (!playerGo.activeSelf) playerGo.SetActive(true);
                if (playerGo.transform.parent != null)
                {
                    playerGo.transform.SetParent(null, true);
                }

                CharacterController cc = playerGo.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;

                playerGo.transform.position = spawnPos;
                playerGo.transform.rotation = Quaternion.Euler(0f, spawnYaw, 0f);

                if (cc != null)
                {
                    cc.enabled = true;
                }
            }

            GameObject fpsCam = GameObject.Find("FPSCamera");
            if (fpsCam != null && !fpsCam.activeSelf)
            {
                fpsCam.SetActive(true);
            }
        }

        private static void ResetSurvivalGlobalsAfterDeath()
        {
            if (PlayMakerGlobals.Instance == null || PlayMakerGlobals.Instance.Variables == null) return;
            FsmVariables vars = PlayMakerGlobals.Instance.Variables;

            FsmBool stop = vars.FindFsmBool("PlayerStop");
            if (stop != null) stop.Value = false;

            FsmBool dead = vars.FindFsmBool("PlayerDead");
            if (dead != null) dead.Value = false;

            FsmBool inCar = vars.FindFsmBool("PlayerInCar");
            if (inCar != null) inCar.Value = false;

            FsmString curVeh = vars.FindFsmString("PlayerCurrentVehicle");
            if (curVeh != null) curVeh.Value = string.Empty;

            ClampFloatMax(vars.FindFsmFloat("PlayerHunger"), 45f);
            ClampFloatMax(vars.FindFsmFloat("PlayerThirst"), 45f);
            ClampFloatMax(vars.FindFsmFloat("PlayerFatigue"), 45f);
            ClampFloatMax(vars.FindFsmFloat("PlayerStress"), 45f);
            ClampFloatMax(vars.FindFsmFloat("PlayerUrine"), 45f);
            ClampFloatMax(vars.FindFsmFloat("PlayerDirtiness"), 45f);

            FsmFloat drunk = vars.FindFsmFloat("PlayerDrunk");
            if (drunk != null) drunk.Value = 0f;

            FsmFloat blindFloat = vars.FindFsmFloat("PlayerBlind");
            if (blindFloat != null) blindFloat.Value = 0f;

            FsmBool blindBool = vars.FindFsmBool("PlayerBlind");
            if (blindBool != null) blindBool.Value = false;
        }

        private static void ClampFloatMax(FsmFloat variable, float maxValue)
        {
            if (variable != null && variable.Value > maxValue)
            {
                variable.Value = maxValue;
            }
        }

        private void SendDeathAndRespawnNotices(RespawnLocationKind locationKind, Vector3 spawnPos)
        {
            using (var deathPacket = new PacketWriter(PacketType.SavegameAndRespawnSync))
            {
                deathPacket.WriteByte(EconomyProtocol.PlayerDeathNotice);
                deathPacket.WriteInt32(LocalPlayerId);
                deathPacket.WriteByte((byte)locationKind);
                ctx.Transport.Send(deathPacket);
            }

            using (var respawnPacket = new PacketWriter(PacketType.SavegameAndRespawnSync))
            {
                respawnPacket.WriteByte(EconomyProtocol.PlayerRespawnState);
                respawnPacket.WriteInt32(LocalPlayerId);
                respawnPacket.WriteByte((byte)locationKind);
                respawnPacket.WriteVector3(spawnPos);
                ctx.Transport.Send(respawnPacket);
            }
        }

        #endregion

        #region Packet Handlers

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 1) return;

            byte subType = reader.ReadByte();
            switch (subType)
            {
                case EconomyProtocol.SaveTriggerNotice:
                    HandleSaveTriggerNotice(reader);
                    break;

                case EconomyProtocol.SaveFileChunk:
                    HandleSaveFileChunk(reader);
                    break;

                case EconomyProtocol.SaveFileComplete:
                    HandleSaveFileComplete(reader, connectionId);
                    break;

                case EconomyProtocol.PlayerDeathNotice:
                    HandlePlayerDeathNotice(reader);
                    break;

                case EconomyProtocol.PlayerRespawnState:
                    HandlePlayerRespawnState(reader);
                    break;
            }
        }

        private void HandleSaveTriggerNotice(PacketReader reader)
        {
            if (IsServer || reader.UnreadBytes < 17) return;

            int transferId = reader.ReadInt32();
            SaveNoticeReason reason = (SaveNoticeReason)reader.ReadByte();
            int totalFiles = reader.ReadInt32();
            int totalBytes = reader.ReadInt32();
            uint globalCrc = unchecked((uint)reader.ReadInt32());

            if (reason == SaveNoticeReason.ClientJoinStatus)
            {
                lastSaveStatus = totalFiles > 0
                    ? $"Сейв хоста ({totalFiles}ф, {totalBytes / 1024} КБ)"
                    : "Сейв хоста пуст";
                ctx?.Log?.Invoke($"[Сохранения] Уведомление от хоста при подключении: {lastSaveStatus}, CRC={globalCrc:X8}.");
                return;
            }

            activeIncoming = new IncomingSaveTransfer
            {
                TransferId = transferId,
                ExpectedFiles = totalFiles,
                ExpectedTotalBytes = totalBytes,
                ExpectedGlobalCrc32 = globalCrc
            };

            lastSaveStatus = $"Приём сейва #{transferId}...";
            ctx?.Log?.Invoke($"[Сохранения] Хост начал синхронизацию сохранения ({totalFiles} файл(ов), {totalBytes / 1024} КБ).");
        }

        private void HandleSaveFileChunk(PacketReader reader)
        {
            if (IsServer || activeIncoming == null || reader.UnreadBytes < 25) return;

            int transferId = reader.ReadInt32();
            if (transferId != activeIncoming.TransferId) return;

            string fileName = reader.ReadString();
            int fileTotalLength = reader.ReadInt32();
            uint fileCrc = unchecked((uint)reader.ReadInt32());
            int chunkIndex = reader.ReadInt32();
            int totalChunks = reader.ReadInt32();
            byte[] chunkData = reader.ReadByteArray();

            if (!IsAllowedSaveFileName(fileName) || fileTotalLength <= 0 || totalChunks <= 0 ||
                chunkIndex < 0 || chunkIndex >= totalChunks || chunkData.Length > MaxChunkPayloadBytes)
            {
                return;
            }

            if (!activeIncoming.Files.TryGetValue(fileName, out IncomingFileBuffer fileBuf))
            {
                fileBuf = new IncomingFileBuffer
                {
                    FileName = fileName,
                    TotalLength = fileTotalLength,
                    ExpectedCrc32 = fileCrc,
                    TotalChunks = totalChunks,
                    Chunks = new byte[totalChunks][]
                };
                activeIncoming.Files[fileName] = fileBuf;
            }

            if (fileBuf.Chunks[chunkIndex] == null)
            {
                fileBuf.Chunks[chunkIndex] = chunkData;
                fileBuf.ReceivedChunks++;
            }
        }

        private void HandleSaveFileComplete(PacketReader reader, int connectionId)
        {
            if (reader.UnreadBytes < 14) return;

            int transferId = reader.ReadInt32();
            SaveCompleteStatus status = (SaveCompleteStatus)reader.ReadByte();
            int filesCount = reader.ReadInt32();
            int totalBytes = reader.ReadInt32();
            string message = reader.ReadString();

            if (IsServer)
            {
                if (status == SaveCompleteStatus.ClientAppliedOk)
                {
                    totalSavesSynced++;
                    lastSaveStatus = "Синхронизирован";
                    ctx?.Log?.Invoke($"[Сохранения] Клиент #{connectionId} подтвердил сохранение #{transferId} ({filesCount}ф, {totalBytes / 1024} КБ).");
                }
                else if (status == SaveCompleteStatus.ClientCrcMismatchFailed)
                {
                    lastSaveStatus = "Ошибка CRC на клиенте";
                    ctx?.Log?.Invoke($"[Сохранения] Клиент #{connectionId} отклонил сейв #{transferId}: {message}");
                }
                return;
            }

            if (status != SaveCompleteStatus.HostFinishedSending || activeIncoming == null || transferId != activeIncoming.TransferId)
            {
                return;
            }

            ApplyIncomingSaveTransfer(activeIncoming);
            activeIncoming = null;
        }

        private void ApplyIncomingSaveTransfer(IncomingSaveTransfer transfer)
        {
            var assembledFiles = new Dictionary<string, byte[]>();
            uint actualGlobalCrc = 0;
            int totalBytesApplied = 0;

            if (transfer.Files.Count != transfer.ExpectedFiles)
            {
                SendClientComplete(transfer.TransferId, SaveCompleteStatus.ClientCrcMismatchFailed, 0, 0, "Несовпадение числа файлов");
                lastSaveStatus = "Ошибка: неполный сейв";
                return;
            }

            foreach (var kv in transfer.Files)
            {
                IncomingFileBuffer buf = kv.Value;
                if (buf == null || buf.ReceivedChunks != buf.TotalChunks)
                {
                    SendClientComplete(transfer.TransferId, SaveCompleteStatus.ClientCrcMismatchFailed, 0, 0, $"Неполные чанки {kv.Key}");
                    lastSaveStatus = "Ошибка: потерян чанк";
                    return;
                }

                byte[] assembled = new byte[buf.TotalLength];
                int writePos = 0;
                for (int c = 0; c < buf.TotalChunks; c++)
                {
                    byte[] part = buf.Chunks[c];
                    if (part == null || writePos + part.Length > assembled.Length)
                    {
                        SendClientComplete(transfer.TransferId, SaveCompleteStatus.ClientCrcMismatchFailed, 0, 0, $"Повреждён размер {kv.Key}");
                        lastSaveStatus = "Ошибка: размер файла";
                        return;
                    }
                    Buffer.BlockCopy(part, 0, assembled, writePos, part.Length);
                    writePos += part.Length;
                }

                if (writePos != buf.TotalLength)
                {
                    SendClientComplete(transfer.TransferId, SaveCompleteStatus.ClientCrcMismatchFailed, 0, 0, $"Несоответствие длины {kv.Key}");
                    lastSaveStatus = "Ошибка: длина файла";
                    return;
                }

                uint fileCrc = ComputeCrc32(assembled, 0, assembled.Length);
                if (fileCrc != buf.ExpectedCrc32)
                {
                    SendClientComplete(transfer.TransferId, SaveCompleteStatus.ClientCrcMismatchFailed, 0, 0, $"Ошибка CRC32 для {kv.Key}");
                    lastSaveStatus = "Ошибка CRC32";
                    return;
                }

                assembledFiles[kv.Key] = assembled;
                actualGlobalCrc ^= fileCrc;
                totalBytesApplied += assembled.Length;
            }

            if (actualGlobalCrc != transfer.ExpectedGlobalCrc32)
            {
                SendClientComplete(transfer.TransferId, SaveCompleteStatus.ClientCrcMismatchFailed, 0, 0, "Ошибка общего CRC32");
                lastSaveStatus = "Ошибка общего CRC32";
                return;
            }

            try
            {
                string dir = Application.persistentDataPath;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string es2Path = Path.Combine(dir, "defaultES2File.txt");
                PersonalSaveSnapshot personal = CapturePersonalData(es2Path);

                foreach (var kv in assembledFiles)
                {
                    string targetPath = Path.Combine(dir, kv.Key);
                    WriteBytesAtomicWithBackup(targetPath, kv.Value);
                }

                RestorePersonalData(personal);

                totalSavesSynced++;
                lastSaveStatus = $"Применён ({totalBytesApplied / 1024} КБ)";
                ctx?.Log?.Invoke($"[Сохранения] Сейв хоста #{transfer.TransferId} успешно записан с .bak бэкапом ({assembledFiles.Count}ф, CRC OK).");
                SendClientComplete(transfer.TransferId, SaveCompleteStatus.ClientAppliedOk, assembledFiles.Count, totalBytesApplied, "OK");
            }
            catch (Exception ex)
            {
                lastSaveStatus = "Ошибка записи на диск";
                ctx?.Log?.Invoke($"[Сохранения] Исключение при сохранении на диск: {ex.Message}");
                SendClientComplete(transfer.TransferId, SaveCompleteStatus.ClientCrcMismatchFailed, 0, 0, ex.Message);
            }
        }

        private void SendClientComplete(int transferId, SaveCompleteStatus status, int filesCount, int totalBytes, string message)
        {
            using (var packet = new PacketWriter(PacketType.SavegameAndRespawnSync))
            {
                packet.WriteByte(EconomyProtocol.SaveFileComplete);
                packet.WriteInt32(transferId);
                packet.WriteByte((byte)status);
                packet.WriteInt32(filesCount);
                packet.WriteInt32(totalBytes);
                packet.WriteString(message ?? string.Empty);
                ctx.Transport.Send(packet);
            }
        }

        private void HandlePlayerDeathNotice(PacketReader reader)
        {
            if (reader.UnreadBytes < 5) return;

            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            RespawnLocationKind kind = (RespawnLocationKind)reader.ReadByte();
            remoteDeathsObserved++;
            string place = kind == RespawnLocationKind.Church ? "церкви" : "больницы";
            ctx?.Log?.Invoke($"[Возрождение] Напарник (#{senderId}) погиб — перенаправлен к {place}.");
        }

        private void HandlePlayerRespawnState(PacketReader reader)
        {
            if (reader.UnreadBytes < 17) return;

            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            RespawnLocationKind kind = (RespawnLocationKind)reader.ReadByte();
            Vector3 spawnPos = reader.ReadVector3();
            string place = kind == RespawnLocationKind.Church ? "церковь" : (kind == RespawnLocationKind.Hospital ? "больница" : "дом");

            ctx?.Log?.Invoke($"[Возрождение] Напарник (#{senderId}) возродился ({place}: {spawnPos.x:F0}, {spawnPos.z:F0}).");
            ctx?.Events?.Publish(new PlayerRespawnedEvent
            {
                PlayerId = senderId,
                IsLocal = false,
                SpawnPosition = spawnPos
            });
        }

        #endregion

        #region Personal Data & Unity 5 / .NET 3.5 Safe I/O

        private PersonalSaveSnapshot CapturePersonalData(string es2Path)
        {
            var data = new PersonalSaveSnapshot();

            if (File.Exists(es2Path))
            {
                try
                {
                    if (ES2.Exists("PlayerHunger")) { data.Hunger = ES2.Load<float>("PlayerHunger"); data.HasHunger = true; }
                    if (ES2.Exists("PlayerThirst")) { data.Thirst = ES2.Load<float>("PlayerThirst"); data.HasThirst = true; }
                    if (ES2.Exists("PlayerFatigue")) { data.Fatigue = ES2.Load<float>("PlayerFatigue"); data.HasFatigue = true; }
                    if (ES2.Exists("PlayerFatigueRate")) { data.FatigueRate = ES2.Load<float>("PlayerFatigueRate"); data.HasFatigueRate = true; }
                    if (ES2.Exists("PlayerUrine")) { data.Urine = ES2.Load<float>("PlayerUrine"); data.HasUrine = true; }
                    if (ES2.Exists("PlayerDirtiness")) { data.Dirtiness = ES2.Load<float>("PlayerDirtiness"); data.HasDirtiness = true; }
                    if (ES2.Exists("PlayerStress")) { data.Stress = ES2.Load<float>("PlayerStress"); data.HasStress = true; }
                    if (ES2.Exists("PlayerStressRate")) { data.StressRate = ES2.Load<float>("PlayerStressRate"); data.HasStressRate = true; }
                    if (ES2.Exists("PlayerDrunk")) { data.Drunk = ES2.Load<float>("PlayerDrunk"); data.HasDrunk = true; }
                    if (ES2.Exists("PlayerCigarettes")) { data.Cigarettes = ES2.Load<int>("PlayerCigarettes"); data.HasCigarettes = true; }
                    if (ES2.Exists("StatsCigarettes")) { data.StatsCigarettes = ES2.Load<int>("StatsCigarettes"); data.HasStatsCigarettes = true; }
                    if (ES2.Exists("PlayerWeight")) { data.Weight = ES2.Load<float>("PlayerWeight"); data.HasWeight = true; }
                    if (ES2.Exists("PlayerTattoo")) { data.Tattoo = ES2.Load<int>("PlayerTattoo"); data.HasTattoo = true; }
                    if (ES2.Exists("PlayerFirstName")) { data.FirstName = ES2.Load<string>("PlayerFirstName"); data.HasFirstName = true; }
                    if (ES2.Exists("PlayerLastName")) { data.LastName = ES2.Load<string>("PlayerLastName"); data.HasLastName = true; }
                }
                catch (Exception ex)
                {
                    ctx?.Log?.Invoke($"[Сохранения] Заметка при чтении личных ES2 тегов: {ex.Message}");
                }
            }

            if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
            {
                FsmVariables vars = PlayMakerGlobals.Instance.Variables;
                CaptureLiveFloat(vars, "PlayerHunger", ref data.HasHunger, ref data.Hunger);
                CaptureLiveFloat(vars, "PlayerThirst", ref data.HasThirst, ref data.Thirst);
                CaptureLiveFloat(vars, "PlayerFatigue", ref data.HasFatigue, ref data.Fatigue);
                CaptureLiveFloat(vars, "PlayerUrine", ref data.HasUrine, ref data.Urine);
                CaptureLiveFloat(vars, "PlayerDirtiness", ref data.HasDirtiness, ref data.Dirtiness);
                CaptureLiveFloat(vars, "PlayerStress", ref data.HasStress, ref data.Stress);
            }

            return data;
        }

        private static void CaptureLiveFloat(FsmVariables vars, string name, ref bool hasValue, ref float value)
        {
            FsmFloat v = vars.FindFsmFloat(name);
            if (v != null && v.Value > 0f)
            {
                value = v.Value;
                hasValue = true;
            }
        }

        private void RestorePersonalData(PersonalSaveSnapshot data)
        {
            if (data == null) return;
            try
            {
                if (data.HasHunger) ES2.Save(data.Hunger, "PlayerHunger");
                if (data.HasThirst) ES2.Save(data.Thirst, "PlayerThirst");
                if (data.HasFatigue) ES2.Save(data.Fatigue, "PlayerFatigue");
                if (data.HasFatigueRate) ES2.Save(data.FatigueRate, "PlayerFatigueRate");
                if (data.HasUrine) ES2.Save(data.Urine, "PlayerUrine");
                if (data.HasDirtiness) ES2.Save(data.Dirtiness, "PlayerDirtiness");
                if (data.HasStress) ES2.Save(data.Stress, "PlayerStress");
                if (data.HasStressRate) ES2.Save(data.StressRate, "PlayerStressRate");
                if (data.HasDrunk) ES2.Save(data.Drunk, "PlayerDrunk");
                if (data.HasCigarettes) ES2.Save(data.Cigarettes, "PlayerCigarettes");
                if (data.HasStatsCigarettes) ES2.Save(data.StatsCigarettes, "StatsCigarettes");
                if (data.HasWeight) ES2.Save(data.Weight, "PlayerWeight");
                if (data.HasTattoo) ES2.Save(data.Tattoo, "PlayerTattoo");
                if (data.HasFirstName && !string.IsNullOrEmpty(data.FirstName)) ES2.Save(data.FirstName, "PlayerFirstName");
                if (data.HasLastName && !string.IsNullOrEmpty(data.LastName)) ES2.Save(data.LastName, "PlayerLastName");
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Сохранения] Заметка при восстановлении личных ES2 тегов: {ex.Message}");
            }
        }

        private static byte[] ReadAllBytesSafe(string path)
        {
            byte[] buffer = new byte[IoBufferBytes];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream())
            {
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, read);
                }
                return ms.ToArray();
            }
        }

        private static void WriteBytesAtomicWithBackup(string targetPath, byte[] data)
        {
            string dir = Path.GetDirectoryName(targetPath);
            string baseName = Path.GetFileNameWithoutExtension(targetPath);
            string bakPath = Path.Combine(dir, baseName + ".bak");
            string bakTmpPath = bakPath + ".tmp";
            string targetTmpPath = targetPath + ".tmp";

            // 1. Атомарное создание .bak перед перезаписью
            if (File.Exists(targetPath))
            {
                CopyFileWithExplicitBuffer(targetPath, bakTmpPath);
                if (File.Exists(bakPath))
                {
                    File.Delete(bakPath);
                }
                File.Move(bakTmpPath, bakPath);
            }

            // 2. Атомарная запись нового файла через .tmp с откатом на .bak при сбое
            try
            {
                WriteBytesWithExplicitBuffer(targetTmpPath, data);
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                File.Move(targetTmpPath, targetPath);
            }
            catch
            {
                try
                {
                    if (File.Exists(targetTmpPath)) File.Delete(targetTmpPath);
                }
                catch { }

                if (!File.Exists(targetPath) && File.Exists(bakPath))
                {
                    CopyFileWithExplicitBuffer(bakPath, targetPath);
                }
                throw;
            }
        }

        private static void CopyFileWithExplicitBuffer(string sourcePath, string destPath)
        {
            byte[] buffer = new byte[IoBufferBytes];
            using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dst.Write(buffer, 0, read);
                }
                dst.Flush();
            }
        }

        private static void WriteBytesWithExplicitBuffer(string destPath, byte[] data)
        {
            using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int offset = 0;
                while (offset < data.Length)
                {
                    int chunk = Math.Min(IoBufferBytes, data.Length - offset);
                    dst.Write(data, offset, chunk);
                    offset += chunk;
                }
                dst.Flush();
            }
        }

        private static DateTime GetEs2LastWriteTimeUtc()
        {
            try
            {
                string es2Path = Path.Combine(Application.persistentDataPath, "defaultES2File.txt");
                if (File.Exists(es2Path))
                {
                    return File.GetLastWriteTimeUtc(es2Path);
                }
            }
            catch { }
            return DateTime.MinValue;
        }

        private static bool IsAllowedSaveFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            for (int i = 0; i < SyncedSaveFileNames.Length; i++)
            {
                if (string.Equals(SyncedSaveFileNames[i], fileName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static uint[] BuildCrc32Table()
        {
            const uint poly = 0xEDB88320u;
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc & 1u) != 0 ? (crc >> 1) ^ poly : (crc >> 1);
                }
                table[i] = crc;
            }
            return table;
        }

        private static uint ComputeCrc32(byte[] data, int offset, int length)
        {
            if (data == null || length <= 0) return 0u;
            uint crc = 0xFFFFFFFFu;
            int end = offset + length;
            for (int i = offset; i < end; i++)
            {
                byte index = (byte)((crc ^ data[i]) & 0xFFu);
                crc = (crc >> 8) ^ Crc32Table[index];
            }
            return ~crc;
        }

        #endregion

        #region Helpers

        private bool TryClaimSafe(PlayMakerFSM fsm, string claimContext)
        {
            if (fsm == null) return false;
            if (ctx == null || ctx.Ownership == null) return true;

            string currentOwner = ctx.Ownership.GetOwner(fsm);
            if (!string.IsNullOrEmpty(currentOwner) && currentOwner != moduleId)
            {
                return false;
            }

            return ctx.Ownership.TryClaim(fsm, moduleId, claimContext);
        }

        private void HookFsmStateOnce(PlayMakerFSM fsm, string stateName, Action action)
        {
            if (fsm == null || string.IsNullOrEmpty(stateName) || action == null) return;
            if (!fsm.HasState(stateName)) return;

            int key = fsm.GetInstanceID() ^ stateName.GetHashCode();
            if (hookedFsmIds.Contains(key)) return;
            hookedFsmIds.Add(key);

            fsm.InsertAction(stateName, action, 0);
        }

        private static GameObject FindObjectIncludingInactive(string name)
        {
            GameObject active = GameObject.Find(name);
            if (active != null) return active;

            GameObject[] allGo = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allGo.Length; i++)
            {
                if (allGo[i] != null && allGo[i].name == name)
                {
                    return allGo[i];
                }
            }
            return null;
        }

        #endregion
    }
}
