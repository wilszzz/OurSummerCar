using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Ownership;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Core.Runtime
{
    public sealed class OscEngineRunner : MonoBehaviour
    {
        public NetworkTransport Transport;
        public List<IOscModule> Modules;
        public OscHostContext Context;
        public Action<string> LogError;

        private void Update()
        {
            Transport?.PollEvents();
            if (Context == null || !Context.IsInGameWorld || Modules == null) return;

            for (int i = 0; i < Modules.Count; i++)
            {
                try { Modules[i].OnUpdate(); }
                catch (Exception ex) { LogError?.Invoke($"[{Modules[i].ModuleId} Update] {ex.Message}"); }
            }
        }

        private void FixedUpdate()
        {
            if (Context == null || !Context.IsInGameWorld || Modules == null) return;

            for (int i = 0; i < Modules.Count; i++)
            {
                try { Modules[i].OnFixedUpdate(); }
                catch (Exception ex) { LogError?.Invoke($"[{Modules[i].ModuleId} FixedUpdate] {ex.Message}"); }
            }
        }

        private void LateUpdate()
        {
            if (Context == null || !Context.IsInGameWorld || Modules == null) return;

            for (int i = 0; i < Modules.Count; i++)
            {
                try { Modules[i].OnLateUpdate(); }
                catch (Exception ex) { LogError?.Invoke($"[{Modules[i].ModuleId} LateUpdate] {ex.Message}"); }
            }
        }
    }

    public sealed class OscRuntimeHost
    {
        private string modulesFolder;
        private NetworkTransport transport;
        private PacketRouter router;
        private OscEventBus eventBus;
        private OwnershipRegistry ownership;
        private OscHostContext context;
        private OscEngineRunner engineRunner;
        private readonly List<IOscModule> loadedModules = new List<IOscModule>();

        private bool showMenu = true;
        private Rect windowRect = new Rect(30, 30, 410, 620);
        private Vector2 windowScrollPos = Vector2.zero;
        private Vector2 logScrollPos = Vector2.zero;
        private readonly List<string> recentLogs = new List<string>();

        private string playerNickname = "Player";
        private string ipAddress = "127.0.0.1";
        private string portString = "7777";
        private string localRadminIp;
        private bool autoStartServer;

        public void Initialize(string assetRootPath)
        {
            modulesFolder = Path.Combine(assetRootPath ?? string.Empty, "Modules");
            LoadSettings();

            transport = new NetworkTransport();
            transport.OnLog += AddLog;

            router = new PacketRouter(transport, AddLog);
            eventBus = new OscEventBus(AddLog);
            ownership = new OwnershipRegistry(AddLog);
            context = new OscHostContext(transport, router, eventBus, ownership, AddLog)
            {
                LocalNickname = playerNickname
            };

            LoadAllFeatureModules();
            EnsureEngineRunner();
        }

        private void EnsureEngineRunner()
        {
            if (engineRunner != null) return;
            var existing = GameObject.Find("OurSummerCar_EngineRunner");
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);

            var go = new GameObject("OurSummerCar_EngineRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            engineRunner = go.AddComponent<OscEngineRunner>();
            engineRunner.Transport = transport;
            engineRunner.Modules = loadedModules;
            engineRunner.Context = context;
            engineRunner.LogError = AddLog;
        }

        private void LoadAllFeatureModules()
        {
            loadedModules.Clear();
            if (string.IsNullOrEmpty(modulesFolder) || !Directory.Exists(modulesFolder))
            {
                AddLog($"[Core] Папка модулей не найдена: {modulesFolder}");
                return;
            }

            string[] dlls = Directory.GetFiles(modulesFolder, "OSC.Module.*.dll");
            Array.Sort(dlls, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < dlls.Length; i++)
            {
                try
                {
                    Assembly asm = Assembly.LoadFrom(dlls[i]);
                    Type[] types = asm.GetTypes();
                    for (int t = 0; t < types.Length; t++)
                    {
                        Type type = types[t];
                        if (type.IsAbstract || !typeof(IOscModule).IsAssignableFrom(type)) continue;

                        var module = (IOscModule)Activator.CreateInstance(type);
                        module.Initialize(context);
                        loadedModules.Add(module);
                        AddLog($"[Модуль] Загружен: {module.DisplayName} ({module.ModuleId})");
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"[Core Load Error] {Path.GetFileName(dlls[i])}: {ex.Message}");
                }
            }
        }

        public void OnMenuLoad()
        {
            if (context == null) return;
            if (context.IsInGameWorld && transport != null && transport.IsRunning)
            {
                transport.Stop();
            }

            context.IsInGameWorld = false;
            SceneHasher.Clear();
            ownership.Clear();

            for (int i = 0; i < loadedModules.Count; i++)
            {
                try { loadedModules[i].OnMenuLoaded(); }
                catch (Exception ex) { AddLog($"[{loadedModules[i].ModuleId} MenuLoad] {ex.Message}"); }
            }
            AddLog("[Ядро] Главное меню активно.");
        }

        public void OnLoad()
        {
            if (context == null) return;
            EnsureEngineRunner();

            SceneHasher.Clear();
            ownership.Clear();
            context.IsInGameWorld = true;

            for (int i = 0; i < loadedModules.Count; i++)
            {
                try { loadedModules[i].OnLevelLoaded(); }
                catch (Exception ex) { AddLog($"[{loadedModules[i].ModuleId} OnLoad] {ex.Message}"); }
            }

            if (autoStartServer && transport != null && !transport.IsRunning && int.TryParse(portString, out int port))
            {
                transport.StartServer(port);
            }
            AddLog($"[Ядро] Мир загружен. Активно модулей: {loadedModules.Count}");
        }

        public void OnUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F10))
            {
                showMenu = !showMenu;
            }
        }

        public void OnGUI()
        {
            if (!showMenu) return;

            GUI.depth = -100;
            windowRect.height = Mathf.Min(Screen.height - 40, 640);
            windowRect = GUI.Window(99211, windowRect, DrawWindow, "Our Summer Car (Modular 0.1)");
        }

        private void DrawWindow(int windowId)
        {
            windowScrollPos = GUILayout.BeginScrollView(windowScrollPos, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>Никнейм:</b>", GUILayout.Width(75));
            string newNick = GUILayout.TextField(playerNickname, 20);
            if (newNick != playerNickname)
            {
                playerNickname = newNick;
                if (context != null) context.LocalNickname = playerNickname;
                SaveSettings();
            }
            GUILayout.EndHorizontal();

            string statusStr = "<color=grey>● Отключен</color>";
            if (transport != null && transport.IsRunning)
            {
                if (transport.IsServer)
                {
                    statusStr = $"<color=lime>● ХОСТ (Клиентов: {transport.ConnectedClientsCount})</color>";
                }
                else if (transport.IsClient)
                {
                    string ping = transport.LastPingRTT >= 0 ? $"{transport.LastPingRTT} мс" : "...";
                    statusStr = $"<color=cyan>● КЛИЕНТ (Пинг: {ping})</color>";
                }
            }
            GUILayout.Label("Сеть: " + statusStr);

            GUILayout.Space(4);
            GUILayout.Label($"<b>Изолированные модули ({loadedModules.Count}):</b>");
            for (int i = 0; i < loadedModules.Count; i++)
            {
                var m = loadedModules[i];
                GUILayout.Label($"  • <color=lime>{m.DisplayName}</color>: {m.StatusSummary}");
            }

            GUILayout.Space(6);
            if (transport == null || !transport.IsRunning)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("IP:", GUILayout.Width(25));
                string newIp = GUILayout.TextField(ipAddress);
                if (newIp != ipAddress) { ipAddress = newIp; SaveSettings(); }

                GUILayout.Label("Порт:", GUILayout.Width(38));
                string newPort = GUILayout.TextField(portString, GUILayout.Width(55));
                if (newPort != portString) { portString = newPort; SaveSettings(); }
                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(localRadminIp))
                {
                    GUILayout.Label($"Ваш IP (Radmin): <color=yellow>{localRadminIp}</color>");
                }

                bool nextAutoHost = GUILayout.Toggle(autoStartServer, "Автозапуск сервера при входе в мир");
                if (nextAutoHost != autoStartServer)
                {
                    autoStartServer = nextAutoHost;
                    SaveSettings();
                }

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Запустить Хост") && int.TryParse(portString, out int hostPort))
                {
                    transport.StartServer(hostPort);
                }
                if (GUILayout.Button("Подключиться") && int.TryParse(portString, out int clientPort))
                {
                    transport.Connect(ipAddress.Trim(), clientPort);
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                if (GUILayout.Button("Остановить соединение"))
                {
                    transport.Stop();
                }
                if (GUILayout.Button("Тестовый Ping"))
                {
                    transport.SendPing();
                }
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>Журнал:</b>");
            if (GUILayout.Button("Очистить", GUILayout.Width(70))) recentLogs.Clear();
            GUILayout.EndHorizontal();

            logScrollPos = GUILayout.BeginScrollView(logScrollPos, GUI.skin.box, GUILayout.Height(150));
            for (int i = 0; i < recentLogs.Count; i++)
            {
                GUILayout.Label(recentLogs[i]);
            }
            GUILayout.EndScrollView();

            GUILayout.Label("<i>F10 — скрыть/показать меню</i>");
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private void LoadSettings()
        {
            playerNickname = PlayerPrefs.GetString("OSC_Nickname", string.Empty);
            if (string.IsNullOrEmpty(playerNickname))
            {
                try { playerNickname = System.Environment.UserName; } catch { playerNickname = "Player"; }
                if (string.IsNullOrEmpty(playerNickname)) playerNickname = "Player";
            }

            ipAddress = PlayerPrefs.GetString("OSC_TargetIP", "127.0.0.1");
            portString = PlayerPrefs.GetString("OSC_Port", "7777");
            autoStartServer = PlayerPrefs.GetInt("OSC_AutoHost", 0) == 1;

            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString().StartsWith("26."))
                    {
                        localRadminIp = ip.ToString();
                        break;
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            PlayerPrefs.SetString("OSC_Nickname", playerNickname);
            PlayerPrefs.SetString("OSC_TargetIP", ipAddress);
            PlayerPrefs.SetString("OSC_Port", portString);
            PlayerPrefs.SetInt("OSC_AutoHost", autoStartServer ? 1 : 0);
            PlayerPrefs.Save();
        }

        private void AddLog(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            recentLogs.Add(line);
            if (recentLogs.Count > 80) recentLogs.RemoveAt(0);
            logScrollPos.y = float.MaxValue;
        }
    }
}
