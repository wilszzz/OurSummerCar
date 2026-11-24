using System;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Savegame
{
    /// <summary>
    /// Личные данные игрока (потребности, сигареты, внешность), которые никогда не должны
    /// перезаписываться сохранением хоста.
    /// </summary>
    public class PlayerPersonalSaveData
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
        public bool HasMoney; public float Money;
        public bool HasWeight; public float Weight;
        public bool HasTattoo; public int Tattoo;
        public bool HasFirstName; public string FirstName;
        public bool HasLastName; public string LastName;
    }

    /// <summary>
    /// Менеджер сетевой синхронизации сохранения мира (World Save Sync).
    /// 
    /// Позволяет подключившемуся игроку:
    /// 1. Загрузить актуальное сохранение мира хоста (машины, болты и сборка Сацумы,
    ///    детали, предметы в мире items.txt, деформации кузова meshsave.txt, квесты и магазин).
    /// 2. Полностью изолировать и СОХРАНИТЬ личные параметры игрока (сигареты, голод,
    ///    жажду, усталость/сон, нужду, стресс, грязь, алкоголь и внешность).
    /// 3. Автоматически запрашивать мир при подключении либо по кнопке в меню.
    /// </summary>
    public class SavegameSyncManager : MonoBehaviour
    {
        public static SavegameSyncManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        public bool AutoSyncOnConnect
        {
            get => PlayerPrefs.GetInt("OSC_AutoSyncWorldSave", 1) == 1;
            set => PlayerPrefs.SetInt("OSC_AutoSyncWorldSave", value ? 1 : 0);
        }

        public bool IsSyncing { get; private set; } = false;
        public string StatusMessage { get; private set; } = "Готов";
        public bool NeedsWorldReload { get; private set; } = false;

        public void Reset()
        {
            IsSyncing = false;
            StatusMessage = "Готов";
            NeedsWorldReload = false;
        }

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;
            transport.OnClientConnected += HandleClientConnected;

            OnLog?.Invoke("[Сохранения] Менеджер синхронизации мира инициализирован");
        }

        public void InitializeSave()
        {
            IsSyncing = false;
            StatusMessage = "Готов";
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
                transport.OnClientConnected -= HandleClientConnected;
            }
        }

        private void HandleClientConnected(int connectionId)
        {
            // Если мы клиент и включена авто-синхронизация, запрашиваем мир у хоста
            if (!transport.IsServer && AutoSyncOnConnect)
            {
                RequestHostWorldSave();
            }
        }

        /// <summary>
        /// Клиент запрашивает у хоста актуальное сохранение мира
        /// </summary>
        public void RequestHostWorldSave()
        {
            if (transport == null || !transport.IsRunning || transport.IsServer) return;

            IsSyncing = true;
            StatusMessage = "Запрос мира у хоста...";
            OnLog?.Invoke("[Сохранения] Запрос актуального сохранения мира у хоста...");

            using (var packet = new PacketWriter(PacketType.SavegameRequest))
            {
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.SavegameRequest:
                    if (transport.IsServer)
                    {
                        HandleSavegameRequest(connectionId);
                    }
                    break;

                case PacketType.SavegameData:
                    if (!transport.IsServer)
                    {
                        HandleSavegameData(reader);
                    }
                    break;

                case PacketType.SavegameComplete:
                    if (transport.IsServer)
                    {
                        OnLog?.Invoke($"[Сохранения] Клиент #{connectionId} успешно применил сохранение мира!");
                    }
                    break;
            }
        }

        /// <summary>
        /// Хост считывает с диска файлы мира и отправляет клиенту
        /// </summary>
        private void HandleSavegameRequest(int connectionId)
        {
            string dir = Application.persistentDataPath;
            string es2File = Path.Combine(dir, "defaultES2File.txt");
            string itemsFile = Path.Combine(dir, "items.txt");
            string meshFile = Path.Combine(dir, "meshsave.txt");

            OnLog?.Invoke($"[Сохранения] Клиент #{connectionId} запросил сохранение мира. Подготовка файлов...");

            try
            {
                byte[] rawEs2 = File.Exists(es2File) ? File.ReadAllBytes(es2File) : new byte[0];
                byte[] rawItems = File.Exists(itemsFile) ? File.ReadAllBytes(itemsFile) : new byte[0];
                byte[] rawMesh = File.Exists(meshFile) ? File.ReadAllBytes(meshFile) : new byte[0];

                using (var packet = new PacketWriter(PacketType.SavegameData))
                {
                    packet.WriteByteArray(rawEs2);
                    packet.WriteByteArray(rawItems);
                    packet.WriteByteArray(rawMesh);

                    transport.SendTo(packet, connectionId);
                }

                OnLog?.Invoke($"[Сохранения] Мир успешно отправлен клиенту #{connectionId} (ES2: {rawEs2.Length / 1024} КБ, Items: {rawItems.Length / 1024} КБ, Mesh: {rawMesh.Length / 1024} КБ)");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Сохранения] Ошибка при чтении или отправке сохранения: {ex.Message}");
            }
        }

        /// <summary>
        /// Клиент принимает файлы мира, защищает личные потребности и сигареты, и объединяет файлы на диске
        /// </summary>
        private void HandleSavegameData(PacketReader reader)
        {
            StatusMessage = "Обработка и объединение сохранения...";
            OnLog?.Invoke("[Сохранения] Получены файлы мира от хоста. Защита личных данных...");

            try
            {
                byte[] rawEs2 = reader.ReadByteArray();
                byte[] rawItems = reader.ReadByteArray();
                byte[] rawMesh = reader.ReadByteArray();

                string dir = Application.persistentDataPath;
                string es2Path = Path.Combine(dir, "defaultES2File.txt");
                string itemsPath = Path.Combine(dir, "items.txt");
                string meshPath = Path.Combine(dir, "meshsave.txt");

                // 1. Извлекаем и защищаем личные данные клиента (потребности, сигареты, внешность)
                PlayerPersonalSaveData personal = CapturePersonalData(es2Path);

                // 2. Создаем резервную копию существующих файлов клиента
                if (File.Exists(es2Path)) File.Copy(es2Path, Path.Combine(dir, "defaultES2File.bak"), true);
                if (File.Exists(itemsPath)) File.Copy(itemsPath, Path.Combine(dir, "items.bak"), true);
                if (File.Exists(meshPath)) File.Copy(meshPath, Path.Combine(dir, "meshsave.bak"), true);

                // 3. Записываем файлы мира хоста
                if (rawItems.Length > 0)
                {
                    File.WriteAllBytes(itemsPath, rawItems);
                }
                if (rawMesh.Length > 0)
                {
                    File.WriteAllBytes(meshPath, rawMesh);
                }
                if (rawEs2.Length > 0)
                {
                    File.WriteAllBytes(es2Path, rawEs2);
                }

                // 4. Восстанавливаем личные данные клиента обратно в объединенный defaultES2File.txt
                RestorePersonalData(personal);

                // 5. Уведомляем хоста об успешном завершении
                using (var confirm = new PacketWriter(PacketType.SavegameComplete))
                {
                    transport.Send(confirm);
                }

                IsSyncing = false;
                StatusMessage = "Мир успешно обновлен!";
                NeedsWorldReload = true;
                OnLog?.Invoke("[Сохранения] Сохранение мира хоста успешно объединено! Потребности (голод, жажда, сон) и сигареты сохранены.");
            }
            catch (Exception ex)
            {
                IsSyncing = false;
                StatusMessage = "Ошибка синхронизации";
                OnLog?.Invoke($"[Сохранения] Ошибка при объединении сохранения мира: {ex.Message}");
            }
        }

        /// <summary>
        /// Считывает личные параметры игрока из локального defaultES2File.txt и текущей сессии
        /// </summary>
        private PlayerPersonalSaveData CapturePersonalData(string es2Path)
        {
            var data = new PlayerPersonalSaveData();

            // Читаем из существующего локального defaultES2File.txt
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
                    if (ES2.Exists("PlayerMoney")) { data.Money = ES2.Load<float>("PlayerMoney"); data.HasMoney = true; }
                    if (ES2.Exists("PlayerWeight")) { data.Weight = ES2.Load<float>("PlayerWeight"); data.HasWeight = true; }
                    if (ES2.Exists("PlayerTattoo")) { data.Tattoo = ES2.Load<int>("PlayerTattoo"); data.HasTattoo = true; }
                    if (ES2.Exists("PlayerFirstName")) { data.FirstName = ES2.Load<string>("PlayerFirstName"); data.HasFirstName = true; }
                    if (ES2.Exists("PlayerLastName")) { data.LastName = ES2.Load<string>("PlayerLastName"); data.HasLastName = true; }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[Сохранения] Заметка при чтении личных ES2 данных: {ex.Message}");
                }
            }

            // Если игрок прямо сейчас в игре, захватываем актуальные значения из FSM
            var gHunger = FsmVariables.GlobalVariables.FindFsmFloat("PlayerHunger");
            if (gHunger != null && gHunger.Value > 0f) { data.Hunger = gHunger.Value; data.HasHunger = true; }

            var gThirst = FsmVariables.GlobalVariables.FindFsmFloat("PlayerThirst");
            if (gThirst != null && gThirst.Value > 0f) { data.Thirst = gThirst.Value; data.HasThirst = true; }

            var gFatigue = FsmVariables.GlobalVariables.FindFsmFloat("PlayerFatigue");
            if (gFatigue != null && gFatigue.Value > 0f) { data.Fatigue = gFatigue.Value; data.HasFatigue = true; }

            var gUrine = FsmVariables.GlobalVariables.FindFsmFloat("PlayerUrine");
            if (gUrine != null && gUrine.Value > 0f) { data.Urine = gUrine.Value; data.HasUrine = true; }

            var gDirt = FsmVariables.GlobalVariables.FindFsmFloat("PlayerDirtiness");
            if (gDirt != null && gDirt.Value > 0f) { data.Dirtiness = gDirt.Value; data.HasDirtiness = true; }

            var gStress = FsmVariables.GlobalVariables.FindFsmFloat("PlayerStress");
            if (gStress != null && gStress.Value > 0f) { data.Stress = gStress.Value; data.HasStress = true; }

            int savedCigs = PlayerPrefs.GetInt("OSC_SavedCigarettes", -1);
            if (savedCigs >= 0)
            {
                data.Cigarettes = savedCigs;
                data.HasCigarettes = true;
            }

            return data;
        }

        /// <summary>
        /// Записывает защищенные личные параметры игрока в объединенный defaultES2File.txt
        /// </summary>
        private void RestorePersonalData(PlayerPersonalSaveData data)
        {
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
                if (data.HasCigarettes)
                {
                    ES2.Save(data.Cigarettes, "PlayerCigarettes");
                    PlayerPrefs.SetInt("OSC_SavedCigarettes", data.Cigarettes);
                    PlayerPrefs.Save();
                }
                if (data.HasStatsCigarettes) ES2.Save(data.StatsCigarettes, "StatsCigarettes");
                if (data.HasMoney) ES2.Save(data.Money, "PlayerMoney");
                if (data.HasWeight) ES2.Save(data.Weight, "PlayerWeight");
                if (data.HasTattoo) ES2.Save(data.Tattoo, "PlayerTattoo");
                if (data.HasFirstName && !string.IsNullOrEmpty(data.FirstName)) ES2.Save(data.FirstName, "PlayerFirstName");
                if (data.HasLastName && !string.IsNullOrEmpty(data.LastName)) ES2.Save(data.LastName, "PlayerLastName");

                // Если в игре, обновляем и живые FSM
                var gHunger = FsmVariables.GlobalVariables.FindFsmFloat("PlayerHunger");
                if (gHunger != null && data.HasHunger) gHunger.Value = data.Hunger;

                var gThirst = FsmVariables.GlobalVariables.FindFsmFloat("PlayerThirst");
                if (gThirst != null && data.HasThirst) gThirst.Value = data.Thirst;

                var gFatigue = FsmVariables.GlobalVariables.FindFsmFloat("PlayerFatigue");
                if (gFatigue != null && data.HasFatigue) gFatigue.Value = data.Fatigue;

                var gUrine = FsmVariables.GlobalVariables.FindFsmFloat("PlayerUrine");
                if (gUrine != null && data.HasUrine) gUrine.Value = data.Urine;

                var gDirt = FsmVariables.GlobalVariables.FindFsmFloat("PlayerDirtiness");
                if (gDirt != null && data.HasDirtiness) gDirt.Value = data.Dirtiness;

                var gStress = FsmVariables.GlobalVariables.FindFsmFloat("PlayerStress");
                if (gStress != null && data.HasStress) gStress.Value = data.Stress;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Сохранения] Ошибка при записи личных данных в ES2: {ex.Message}");
            }
        }

        /// <summary>
        /// Перезагружает сцену игры для немедленного вступления в силу всех объектов мира
        /// </summary>
        public void ReloadGameWorld()
        {
            NeedsWorldReload = false;
            OnLog?.Invoke("[Сохранения] Перезагрузка игрового мира...");
            Application.LoadLevel("GAME");
        }
    }
}
