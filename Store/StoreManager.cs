using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Store
{
    public class StoreProductSync
    {
        public PlayMakerFSM Fsm;
        public string Name;
        public FsmInt Quantity;
        public FsmInt Bought;
        public FsmEvent PurchaseEvent;
        public FsmEvent DepurchaseEvent;
        public bool DoSync = true;
    }

    public class StoreManager : MonoBehaviour
    {
        private NetworkTransport transport;
        public Action<string> OnLog;

        private GameObject store;
        private bool isStoreInitialized = false;
        private float nextSearchTime = 0f;
        private bool isApplyingRemoteProduct = false;

        private readonly List<StoreProductSync> products = new List<StoreProductSync>();
        private readonly Dictionary<string, StoreProductSync> productsByName = new Dictionary<string, StoreProductSync>();

        private PlayMakerFSM registerFsm;
        private GameObject bagCreator;
        private FsmInt[] registerIntVars;
        private readonly string[] registerResetVars = new string[] { "QBeer", "QCarBattery", "QCharcoal", "QCoolant", "QExtinguisher", "QMotorOil", "QTwoStroke" };
        private PlayMakerHashTableProxy boughtInventory;
        private bool isSuppressingRegister = false;

        // Витрина магазина Теймо
        private GameObject storeWindowsIntact;
        private GameObject storeWindowsBroken;
        private GameObject storeWindows1;
        private PlayMakerFSM breakableWindowFsm;
        private bool isWindowBroken = false;
        private bool isSuppressingWindow = false;

        public void Reset()
        {
            isStoreInitialized = false;
            store = null;
            nextSearchTime = 0f;
            products.Clear();
            productsByName.Clear();
            registerFsm = null;
            bagCreator = null;
            boughtInventory = null;
            storeWindowsIntact = null;
            storeWindowsBroken = null;
            storeWindows1 = null;
            breakableWindowFsm = null;
        }

        public void Initialize(NetworkTransport netTransport)
        {
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;
            transport.OnClientConnected += HandleClientConnected;

            TryInitStore();
        }

        public void InitializeStore()
        {
            isStoreInitialized = false;
            store = null;
            TryInitStore();
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
                transport.OnClientConnected -= HandleClientConnected;
            }
        }

        private void Update()
        {
            if (!isStoreInitialized && Time.time >= nextSearchTime)
            {
                nextSearchTime = Time.time + 30.0f;
                TryInitStore();
            }
        }

        private void TryInitStore()
        {
            try
            {
                if (store == null)
                {
                    store = GameObject.Find("STORE");
                }

                if (store == null)
                {
                    nextSearchTime = Time.time + 30.0f;
                    return;
                }

                Transform lodTr = store.transform.Find("LOD");
                if (lodTr == null) return;

                lodTr.gameObject.SetActive(true);

                // Отключаем FSM LOD дистанции, чтобы товары не выгружались при удалении игроков
                var storeLodFsm = store.GetComponent<PlayMakerFSM>();
                if (storeLodFsm != null && storeLodFsm.FsmName == "LOD")
                {
                    storeLodFsm.enabled = false;
                }

                InitProducts(lodTr);
                InitRegister(lodTr);
                InitBoughtInventory();
                InitStoreWindows();

                isStoreInitialized = true;
                OnLog?.Invoke($"[Магазин Теймо] Синхронизация инициализирована. Товаров: {products.Count}.");
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[StoreManager.TryInitStore Error] {ex.Message}");
            }
        }

        private void InitProducts(Transform lodTr)
        {
            Transform actTr = lodTr.Find("ActivateStore");
            if (actTr == null) return;

            products.Clear();
            productsByName.Clear();

            var buyFsms = actTr.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < buyFsms.Length; i++)
            {
                var fsm = buyFsms[i];
                if (fsm == null || fsm.FsmName != "Buy") continue;

                fsm.Initialize();
                var prod = new StoreProductSync
                {
                    Fsm = fsm,
                    Name = fsm.gameObject.name,
                    Quantity = fsm.FsmVariables.FindFsmInt("Quantity"),
                    Bought = fsm.FsmVariables.FindFsmInt("Bought")
                };

                prod.PurchaseEvent = fsm.AddSafeEvent("MP_PURCHASE");
                string purchaseState = fsm.HasState("Check inventory") ? "Check inventory" : (fsm.HasState("Play anim") ? "Play anim" : null);
                if (purchaseState != null)
                {
                    fsm.AddSafeGlobalTransition(prod.PurchaseEvent, purchaseState);
                    fsm.InsertAction(purchaseState, () =>
                    {
                        if (prod.DoSync && !isApplyingRemoteProduct)
                        {
                            SendStoreProductAction(prod.Name, true, prod.Quantity != null ? prod.Quantity.Value : 0, prod.Bought != null ? prod.Bought.Value : 0);
                        }
                    }, -1, false);
                }

                products.Add(prod);
                if (!productsByName.ContainsKey(prod.Name))
                {
                    productsByName.Add(prod.Name, prod);
                }
            }
        }

        private void InitRegister(Transform lodTr)
        {
            Transform regTr = store.transform.Find("StoreCashRegister/Register");
            if (regTr != null)
            {
                registerFsm = regTr.GetComponent<PlayMakerFSM>();
                if (registerFsm != null)
                {
                    registerFsm.Initialize();
                    registerIntVars = registerFsm.FsmVariables.IntVariables;
                    var regInteract = registerFsm.AddSafeEvent("MP_INTERACT");
                    registerFsm.AddSafeGlobalTransition(regInteract, "Check money");
                    registerFsm.InsertAction("Check money", () =>
                    {
                        if (!isSuppressingRegister)
                        {
                            SendStoreRegisterAction();
                            OnLog?.Invoke("[Магазин Теймо] Оплата покупок на кассе!");
                        }
                        isSuppressingRegister = false;
                    }, 0, false);
                }
            }

            Transform bagTr = lodTr.Find("ShopFunctions/BagCreator");
            if (bagTr != null)
            {
                bagCreator = bagTr.gameObject;
            }
        }

        private void InitBoughtInventory()
        {
            Transform invTr = store.transform.Find("Inventory");
            if (invTr != null)
            {
                var proxies = invTr.GetComponents<PlayMakerHashTableProxy>();
                for (int i = 0; i < proxies.Length; i++)
                {
                    if (proxies[i].referenceName == "Bought")
                    {
                        boughtInventory = proxies[i];
                        break;
                    }
                }
            }
        }

        private void InitStoreWindows()
        {
            Transform gfxTr = store.transform.Find("GFX_Store");
            if (gfxTr != null)
            {
                Transform winTr = gfxTr.Find("store_windows");
                if (winTr != null) storeWindowsIntact = winTr.gameObject;

                Transform breakableTr = gfxTr.Find("BreakableWindows");
                if (breakableTr != null)
                {
                    Transform bw = breakableTr.Find("BreakableWindow");
                    if (bw != null)
                    {
                        breakableWindowFsm = bw.GetComponent<PlayMakerFSM>();
                        if (breakableWindowFsm != null)
                        {
                            breakableWindowFsm.Initialize();
                            var shatterEvent = breakableWindowFsm.AddSafeEvent("MP_SHATTER");
                            breakableWindowFsm.AddSafeGlobalTransition(shatterEvent, "Shatter");
                            breakableWindowFsm.InsertAction("Shatter", () =>
                            {
                                if (!isSuppressingWindow && !isWindowBroken)
                                {
                                    isWindowBroken = true;
                                    SendStoreWindowSync(true);
                                    OnLog?.Invoke("[Магазин Теймо] Витрина разбита! Сработала сигнализация!");
                                }
                                isSuppressingWindow = false;
                            }, 0, false);
                        }
                    }

                    Transform brokenTr = breakableTr.Find("store_windows_broken");
                    if (brokenTr != null) storeWindowsBroken = brokenTr.gameObject;

                    Transform win1Tr = breakableTr.Find("store_windows_1");
                    if (win1Tr != null) storeWindows1 = win1Tr.gameObject;
                }
            }
        }

        public void SendStoreProductAction(string productName, bool isPurchase, int quantity, int bought)
        {
            if (transport == null || !transport.IsRunning) return;
            using (var packet = new PacketWriter(PacketType.StoreProductAction))
            {
                packet.WriteString(productName);
                packet.WriteBoolean(isPurchase);
                packet.WriteInt32(quantity);
                packet.WriteInt32(bought);
                transport.Send(packet);
            }
        }

        public void SendStoreRegisterAction()
        {
            if (transport == null || !transport.IsRunning) return;
            using (var packet = new PacketWriter(PacketType.StoreRegisterAction))
            {
                packet.WriteByte(1);
                transport.Send(packet);
            }
        }

        public void SendStoreWindowSync(bool broken)
        {
            if (transport == null || !transport.IsRunning) return;
            using (var packet = new PacketWriter(PacketType.StoreWindowSync))
            {
                packet.WriteBoolean(broken);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.StoreProductAction)
            {
                string prodName = reader.ReadString();
                bool isPurchase = reader.ReadBoolean();
                int qty = reader.ReadInt32();
                int bgt = reader.ReadInt32();

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.StoreProductAction))
                    {
                        relay.WriteString(prodName);
                        relay.WriteBoolean(isPurchase);
                        relay.WriteInt32(qty);
                        relay.WriteInt32(bgt);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteProductAction(prodName, isPurchase, qty, bgt);
            }
            else if (reader.Type == PacketType.StoreRegisterAction)
            {
                byte dummy = reader.ReadByte();
                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.StoreRegisterAction))
                    {
                        relay.WriteByte(dummy);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteRegister();
            }
            else if (reader.Type == PacketType.StoreWindowSync || reader.Type == PacketType.StoreBreakIn)
            {
                bool broken = reader.ReadBoolean();
                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.StoreWindowSync))
                    {
                        relay.WriteBoolean(broken);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteWindowBroken(broken);
            }
            else if (reader.Type == PacketType.StoreShelfSync)
            {
                ApplyRemoteShelfSync(reader);
            }
        }

        private void ApplyRemoteProductAction(string prodName, bool isPurchase, int qty, int bgt)
        {
            try
            {
                if (productsByName.TryGetValue(prodName, out var prod))
                {
                    isApplyingRemoteProduct = true;
                    prod.DoSync = false;
                    try
                    {
                        if (prod.Quantity != null) prod.Quantity.Value = qty;
                        if (prod.Bought != null) prod.Bought.Value = bgt;

                        if (isPurchase && prod.PurchaseEvent != null)
                        {
                            prod.Fsm.SendEvent(prod.PurchaseEvent.Name);
                        }
                    }
                    finally
                    {
                        isApplyingRemoteProduct = false;
                        prod.DoSync = true;
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[ApplyRemoteProductAction Error] {ex.Message}");
            }
        }

        private void ApplyRemoteRegister()
        {
            try
            {
                isSuppressingRegister = true;
                if (registerIntVars != null)
                {
                    for (int i = 0; i < registerIntVars.Length; i++)
                    {
                        if (registerIntVars[i] != null && registerResetVars.Contains(registerIntVars[i].Name))
                        {
                            registerIntVars[i].Value = 0;
                        }
                    }
                }

                if (bagCreator != null)
                {
                    bagCreator.SetActive(true);
                }

                if (registerFsm != null)
                {
                    registerFsm.SendEvent("MP_INTERACT");
                }

                OnLog?.Invoke("[Магазин Теймо] Напарник оплатил покупки на кассе!");
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[ApplyRemoteRegister Error] {ex.Message}");
            }
            finally
            {
                isSuppressingRegister = false;
            }
        }

        private void ApplyRemoteWindowBroken(bool broken)
        {
            try
            {
                isSuppressingWindow = true;
                isWindowBroken = broken;

                if (storeWindowsIntact != null) storeWindowsIntact.SetActive(!broken);
                if (storeWindows1 != null) storeWindows1.SetActive(!broken);
                if (storeWindowsBroken != null) storeWindowsBroken.SetActive(broken);

                if (broken && breakableWindowFsm != null)
                {
                    breakableWindowFsm.SendEvent("MP_SHATTER");
                }

                if (broken)
                {
                    OnLog?.Invoke("[Магазин Теймо] Витрина разбита напарником!");
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[ApplyRemoteWindowBroken Error] {ex.Message}");
            }
            finally
            {
                isSuppressingWindow = false;
            }
        }

        private void HandleClientConnected(int clientId)
        {
            if (!transport.IsServer || !isStoreInitialized) return;

            try
            {
                using (var packet = new PacketWriter(PacketType.StoreShelfSync))
                {
                    // 1. Товары на полках
                    packet.WriteInt32(products.Count);
                    for (int i = 0; i < products.Count; i++)
                    {
                        var p = products[i];
                        packet.WriteString(p.Name);
                        packet.WriteInt32(p.Quantity != null ? p.Quantity.Value : 0);
                        packet.WriteInt32(p.Bought != null ? p.Bought.Value : 0);
                    }

                    // 2. Статус витрины
                    packet.WriteBoolean(isWindowBroken);

                    // 3. Состояние переменных кассы
                    if (registerIntVars != null)
                    {
                        packet.WriteInt32(registerIntVars.Length);
                        for (int i = 0; i < registerIntVars.Length; i++)
                        {
                            packet.WriteString(registerIntVars[i].Name);
                            packet.WriteInt32(registerIntVars[i].Value);
                        }
                    }
                    else
                    {
                        packet.WriteInt32(0);
                    }

                    // 4. PlayMakerHashTableProxy "Bought"
                    if (boughtInventory != null && boughtInventory._hashTable != null)
                    {
                        var ht = boughtInventory._hashTable;
                        packet.WriteInt32(ht.Count);
                        foreach (DictionaryEntry entry in ht)
                        {
                            packet.WriteString(entry.Key != null ? entry.Key.ToString() : "");
                            int val = 0;
                            if (entry.Value is int iv) val = iv;
                            else if (int.TryParse(entry.Value?.ToString(), out int parsed)) val = parsed;
                            packet.WriteInt32(val);
                        }
                    }
                    else
                    {
                        packet.WriteInt32(0);
                    }

                    transport.Send(packet, clientId);
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[StoreManager.SyncInitial Error] {ex.Message}");
            }
        }

        private void ApplyRemoteShelfSync(PacketReader reader)
        {
            try
            {
                // 1. Товары
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string name = reader.ReadString();
                    int qty = reader.ReadInt32();
                    int bgt = reader.ReadInt32();

                    if (productsByName.TryGetValue(name, out var prod))
                    {
                        prod.DoSync = false;
                        if (prod.Quantity != null) prod.Quantity.Value = qty;
                        if (prod.Bought != null) prod.Bought.Value = bgt;
                    }
                }

                // 2. Витрина
                bool winBroken = reader.ReadBoolean();
                if (winBroken)
                {
                    ApplyRemoteWindowBroken(true);
                }

                // 3. Касса
                int regCount = reader.ReadInt32();
                for (int i = 0; i < regCount; i++)
                {
                    string varName = reader.ReadString();
                    int varVal = reader.ReadInt32();
                    if (registerIntVars != null)
                    {
                        for (int j = 0; j < registerIntVars.Length; j++)
                        {
                            if (registerIntVars[j].Name == varName)
                            {
                                registerIntVars[j].Value = varVal;
                                break;
                            }
                        }
                    }
                }

                // 4. Bought хэштаблица
                int htCount = reader.ReadInt32();
                for (int i = 0; i < htCount; i++)
                {
                    string k = reader.ReadString();
                    int v = reader.ReadInt32();
                    if (boughtInventory != null && boughtInventory._hashTable != null && !string.IsNullOrEmpty(k))
                    {
                        boughtInventory._hashTable[k] = v;
                    }
                }

                OnLog?.Invoke($"[Магазин Теймо] Синхронизировано состояние витрин и {count} полок товаров.");
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[ApplyRemoteShelfSync Error] {ex.Message}");
            }
        }
    }
}
