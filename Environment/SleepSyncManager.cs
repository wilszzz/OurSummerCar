using System;
using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Environment
{
    public class SleepSyncManager : MonoBehaviour
    {
        public static SleepSyncManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        private readonly List<PlayMakerFSM> hookedBedFsms = new List<PlayMakerFSM>();
        private readonly List<Transform> bedPivots = new List<Transform>();

        // Локальное состояние сна
        private bool isLocalLayingDown = false;
        private bool isLocalSmoking = false;
        private Transform activeBedPivot = null;
        private Vector3 playerOriginalPos;
        private Quaternion playerOriginalRot;
        private float soloSleepTimer = 0f;
        private Coroutine activeSleepTransition = null;

        // Фиксация камеры (MouseLook) во время сна
        private readonly List<MouseLook> disabledMouseLooks = new List<MouseLook>();
        private Quaternion lockedHeadRotation = Quaternion.identity;

        public bool IsLocalLayingDown => isLocalLayingDown;
        public bool IsExecutingSleep => isExecutingSleep;

        // Сетевое состояние участников
        private bool hostLayingDown = false;
        private bool hostSmoking = false;
        private bool clientLayingDown = false;
        private bool clientSmoking = false;
        private bool isExecutingSleep = false;

        // Игровые переменные
        private Transform playerTr;
        private Animation sleepEyes;
        private FsmFloat fatigueVar;
        private FsmInt timeVar;
        private FsmInt dayVar;
        private FsmBool playerStopVar;
        private FsmFloat drunkVar;
        private FsmFloat hungerVar;
        private FsmFloat thirstVar;
        private FsmFloat urineVar;

        // Ссылка на FSM дома для пожара
        private PlayMakerFSM fireBedroomFsm;
        private PlayMakerFSM housefireFsm;

        private bool isBedsHooked = false;
        private float nextBedSearchTime = 0f;

        public void Reset()
        {
            isBedsHooked = false;
            isLocalLayingDown = false;
            isLocalSmoking = false;
            hostLayingDown = false;
            hostSmoking = false;
            clientLayingDown = false;
            clientSmoking = false;
            isExecutingSleep = false;
            activeBedPivot = null;
            hookedBedFsms.Clear();
            bedPivots.Clear();
            for (int i = 0; i < createdCustomBeds.Count; i++)
            {
                if (createdCustomBeds[i] != null) Destroy(createdCustomBeds[i]);
            }
            createdCustomBeds.Clear();
            customBedsCreated = false;
            playerTr = null;
            sleepEyes = null;
            fatigueVar = null;
            timeVar = null;
            dayVar = null;
            fireBedroomFsm = null;
            housefireFsm = null;
        }

        public void Initialize(NetworkTransport netTransport)
        {
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;
            transport.OnClientConnected += HandleClientConnected;

            FindGameReferences();
            HookVanillaBeds();
        }

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
                transport.OnClientConnected -= HandleClientConnected;
            }
        }

        private void FindGameReferences()
        {
            try
            {
                var playerObj = GameObject.Find("PLAYER");
                if (playerObj != null)
                {
                    playerTr = playerObj.transform;
                    var eyesTr = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/SleepEyes");
                    if (eyesTr != null)
                    {
                        sleepEyes = eyesTr.GetComponent<Animation>();
                    }
                }

                if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
                {
                    fatigueVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerFatigue");
                    timeVar = PlayMakerGlobals.Instance.Variables.FindFsmInt("GlobalTime") ?? PlayMakerGlobals.Instance.Variables.FindFsmInt("Time");
                    dayVar = PlayMakerGlobals.Instance.Variables.FindFsmInt("GlobalDay");
                    playerStopVar = PlayMakerGlobals.Instance.Variables.FindFsmBool("PlayerStop");
                    drunkVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerDrunkAdjusted");
                    hungerVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerHunger");
                    thirstVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerThirst");
                    urineVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerUrine");
                }

                if (timeVar == null)
                {
                    var sunObj = GameObject.Find("MAP")?.transform.Find("SUN/Pivot/SUN");
                    if (sunObj != null)
                    {
                        var sunFsm = sunObj.GetComponent<PlayMakerFSM>();
                        if (sunFsm != null)
                        {
                            timeVar = sunFsm.FsmVariables.FindFsmInt("Time");
                        }
                    }
                }

                // Поиск пожара спальни и дома
                var bedFireObj = GameObject.Find("FireBedroom1") ?? GameObject.Find("FireBedroom2");
                if (bedFireObj != null)
                {
                    fireBedroomFsm = bedFireObj.GetComponent<PlayMakerFSM>();
                }

                var housefireObj = GameObject.Find("HOUSEFIRE");
                if (housefireObj != null)
                {
                    housefireFsm = housefireObj.GetComponent<PlayMakerFSM>();
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[SleepSyncManager FindReferences Error] {ex.Message}");
            }
        }

        public void HookVanillaBeds()
        {
            string[] knownBeds = new string[]
            {
                "YARD/Building/BEDROOM1/LOD_bedroom1/Sleep/SleepTrigger",
                "ITEMS/sofa(itemx)/Sleep/SleepTrigger",
                "seat rear(Clone)/Sleep/SleepTrigger",
                "RCO_RUSCKO12(270)/LOD/Sleep/SleepTrigger",
                "CABIN/LOD/Sleep/SleepTrigger/PivotSleep",
                "COTTAGE/LOD/Sleep/SleepTrigger",
                "JAIL/Sleep/SleepTrigger",
                "GIFU(750/450psi)/LOD/Sleep/SleepTrigger"
            };

            for (int i = 0; i < knownBeds.Length; i++)
            {
                var go = GameObject.Find(knownBeds[i]);
                if (go != null)
                {
                    HookBedTrigger(go);
                }
            }

            // Дополнительный поиск любых SleepTrigger в сцене
            var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allGo.Length; i++)
            {
                if (allGo[i].name == "SleepTrigger")
                {
                    HookBedTrigger(allGo[i]);
                }
            }

            SetupCustomBeds();

            if (hookedBedFsms.Count > 0 || customBedsCreated)
            {
                isBedsHooked = true;
                OnLog?.Invoke($"[Кооперативный сон] Подключено спальных мест: ванильных {hookedBedFsms.Count}, кастомных {createdCustomBeds.Count}.");
            }
        }

        private struct CustomBedDef
        {
            public string Name;
            public Vector3 Position;
            public float Yaw;
            public Vector3 TriggerSize;

            public CustomBedDef(string name, Vector3 pos, float yaw, Vector3 triggerSize)
            {
                Name = name;
                Position = pos;
                Yaw = yaw;
                TriggerSize = triggerSize;
            }
        }

        private static readonly CustomBedDef[] customBedDefs = new CustomBedDef[]
        {
            new CustomBedDef("Диван дома", new Vector3(-3.981f, 0.138f, 8.744f), 270.0f, new Vector3(1.2f, 0.8f, 2.0f)),
            new CustomBedDef("Спальное место 1 (Гостевая)", new Vector3(-2.319f, 0.199f, 13.576f), 270.0f, new Vector3(1.2f, 0.8f, 2.0f)),
            new CustomBedDef("Спальное место 2 (Гостевая)", new Vector3(-2.348f, 0.199f, 12.587f), 270.0f, new Vector3(1.2f, 0.8f, 2.0f)),
            new CustomBedDef("Дом свина (Кровать)", new Vector3(-167.169f, -3.311f, 1021.886f), 87.0f, new Vector3(1.2f, 0.8f, 2.0f)),
            new CustomBedDef("Коврик в доме свина", new Vector3(-166.469f, -3.689f, 1020.904f), 101.5f, new Vector3(1.2f, 0.6f, 2.0f)),
            new CustomBedDef("Коврик в домике на острове", new Vector3(-847.334f, -2.532f, 505.995f), 333.6f, new Vector3(1.2f, 0.6f, 2.0f))
        };

        private readonly List<GameObject> createdCustomBeds = new List<GameObject>();
        private bool customBedsCreated = false;

        private void SetupCustomBeds()
        {
            if (customBedsCreated) return;

            for (int i = 0; i < customBedDefs.Length; i++)
            {
                var def = customBedDefs[i];
                try
                {
                    var bedObj = new GameObject($"OSC_CustomBed_{i}_{def.Name}");
                    bedObj.transform.position = def.Position;
                    bedObj.transform.rotation = Quaternion.Euler(0f, def.Yaw, 0f);

                    // Слой 0 (Default), чтобы луч Raycast из глаз игрока попадал в триггер
                    bedObj.layer = 0;

                    var boxCol = bedObj.AddComponent<BoxCollider>();
                    boxCol.size = def.TriggerSize;
                    boxCol.center = new Vector3(0f, 0.2f, 0.5f);
                    boxCol.isTrigger = true;

                    var pivotObj = new GameObject("Pivot");
                    pivotObj.transform.parent = bedObj.transform;
                    pivotObj.transform.localPosition = Vector3.zero;
                    pivotObj.transform.localRotation = Quaternion.identity;

                    var wakeObj = new GameObject("PivotWake");
                    wakeObj.transform.parent = bedObj.transform;
                    // Точка вставания на ноги: сбоку от спального места
                    wakeObj.transform.localPosition = new Vector3(0.9f, 0f, 0.5f);
                    wakeObj.transform.localRotation = Quaternion.identity;

                    var trigger = bedObj.AddComponent<CoopBedTrigger>();
                    trigger.Initialize(this, pivotObj.transform);

                    createdCustomBeds.Add(bedObj);
                    bedPivots.Add(pivotObj.transform);
                }
                catch (Exception ex)
                {
                    ModConsole.Print($"[SleepSyncManager] Ошибка создания кастомной кровати {def.Name}: {ex.Message}");
                }
            }

            customBedsCreated = true;
        }

        private void HookBedTrigger(GameObject bedGo)
        {
            if (bedGo == null) return;
            var fsm = bedGo.GetComponent<PlayMakerFSM>();
            if (fsm != null && !hookedBedFsms.Contains(fsm))
            {
                fsm.Initialize();
                hookedBedFsms.Add(fsm);

                // Отключаем одиночный FSM засыпания, чтобы он не перематывал время без напарника
                fsm.enabled = false;

                Transform pivot = bedGo.transform.Find("Pivot") ?? bedGo.transform.Find("PivotSleep") ?? bedGo.transform;
                bedPivots.Add(pivot);

                // Добавляем интерактивный сетевой триггер кровати
                var trigger = bedGo.GetComponent<CoopBedTrigger>() ?? bedGo.AddComponent<CoopBedTrigger>();
                trigger.Initialize(this, pivot);
            }
        }

        private Vector3 GetBedLayingPosition(Transform pivot)
        {
            if (pivot == null) return playerTr.position;
            return pivot.position;
        }

        private Quaternion GetBedLayingRotation(Transform pivot)
        {
            if (pivot == null) return Quaternion.Euler(0f, playerTr.eulerAngles.y, 0f);

            // В MSC узлы кроватей/диванов могут иметь крен до 227°, поэтому проецируем родительский forward/right на XZ
            Vector3 bedForward = Vector3.forward;
            if (pivot.parent != null)
            {
                bedForward = pivot.parent.forward;
                bedForward.y = 0f;
                if (bedForward.sqrMagnitude < 0.01f)
                {
                    bedForward = pivot.parent.right;
                    bedForward.y = 0f;
                }
            }
            if (bedForward.sqrMagnitude < 0.01f)
            {
                bedForward = pivot.forward;
                bedForward.y = 0f;
            }
            if (bedForward.sqrMagnitude < 0.01f)
            {
                bedForward = Vector3.forward;
            }

            bedForward.Normalize();
            float yaw = Mathf.Atan2(bedForward.x, bedForward.z) * Mathf.Rad2Deg;
            return Quaternion.Euler(0f, yaw, 0f);
        }

        private IEnumerator SmoothSleepTransition(Vector3 startPos, Quaternion startRot, Vector3 targetPos, Quaternion targetRot, float duration = 0.75f)
        {
            Transform fpsCam = playerTr != null ? (playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera") ?? playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera")) : null;
            Transform animPivot = playerTr != null ? playerTr.Find("Pivot/AnimPivot") : null;

            Vector3 startCamPos = fpsCam != null ? fpsCam.localPosition : new Vector3(0f, 1.4f, 0f);
            Vector3 targetCamPos = new Vector3(0f, 0.22f, 0f); // Опускаем камеру на подушку (~22 см над матрасом)

            // Крен камеры строго 0! В виде от 1-го лица поворот головы на 80° ломает управление и ориентацию мира
            Quaternion startAnimRot = animPivot != null ? animPivot.localRotation : Quaternion.identity;
            Quaternion targetAnimRot = Quaternion.identity;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float smoothT = Mathf.SmoothStep(0f, 1f, t);

                if (playerTr != null)
                {
                    playerTr.position = Vector3.Lerp(startPos, targetPos, smoothT);
                    playerTr.rotation = Quaternion.Slerp(startRot, targetRot, smoothT);
                }
                if (fpsCam != null)
                {
                    fpsCam.localPosition = Vector3.Lerp(startCamPos, targetCamPos, smoothT);
                }
                if (animPivot != null)
                {
                    animPivot.localRotation = Quaternion.identity;
                }
                yield return null;
            }

            if (playerTr != null)
            {
                playerTr.position = targetPos;
                playerTr.rotation = targetRot;
            }
            if (fpsCam != null)
            {
                fpsCam.localPosition = targetCamPos;
            }
            if (animPivot != null)
            {
                animPivot.localRotation = Quaternion.identity;
            }
            activeSleepTransition = null;
        }

        public void StartLayingDown(Transform pivot)
        {
            if (playerTr == null) FindGameReferences();
            if (playerTr == null) return;

            isLocalLayingDown = true;
            activeBedPivot = pivot;
            playerOriginalPos = playerTr.position;
            playerOriginalRot = playerTr.rotation;
            soloSleepTimer = Time.time;

            if (playerStopVar != null)
            {
                playerStopVar.Value = true;
            }

            isLocalSmoking = CheckIfSmoking();

            if (pivot != null)
            {
                Vector3 targetPos = GetBedLayingPosition(pivot);
                Quaternion targetRot = GetBedLayingRotation(pivot);
                lockedHeadRotation = targetRot;

                if (activeSleepTransition != null) StopCoroutine(activeSleepTransition);
                activeSleepTransition = StartCoroutine(SmoothSleepTransition(playerOriginalPos, playerOriginalRot, targetPos, targetRot, 0.75f));
            }
            else
            {
                lockedHeadRotation = playerTr.rotation;
            }

            SetCameraLookLocked(true);

            // Отправляем голос за сон в сеть
            SendSleepVote(true, isLocalSmoking);
            OnLog?.Invoke(isLocalSmoking
                ? "[Сон] Вы легли в постель С ЗАКУРЕННОЙ СИГАРЕТОЙ! Ждем напарника..."
                : "[Сон] Вы легли в постель. Ожидание второго игрока...");

            if (transport.IsServer)
            {
                hostLayingDown = true;
                hostSmoking = isLocalSmoking;
                CheckCoopSleepCondition();
            }
        }

        public void CancelLayingDown()
        {
            if (!isLocalLayingDown || isExecutingSleep) return;

            SetCameraLookLocked(false);

            if (activeSleepTransition != null)
            {
                StopCoroutine(activeSleepTransition);
                activeSleepTransition = null;
            }

            isLocalLayingDown = false;
            if (playerStopVar != null)
            {
                playerStopVar.Value = false;
            }

            if (playerTr != null)
            {
                playerTr.position = playerOriginalPos;
                playerTr.rotation = Quaternion.Euler(0f, playerOriginalRot.eulerAngles.y, 0f);

                Transform animPivot = playerTr.Find("Pivot/AnimPivot");
                if (animPivot != null) animPivot.localRotation = Quaternion.identity;

                Transform fpsCam = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera");
                if (fpsCam != null) fpsCam.localPosition = new Vector3(0f, 1.4f, 0f);

                ResetPlayerCameraRoll(playerTr);

                var allFsms = playerTr.GetComponents<PlayMakerFSM>();
                for (int i = 0; i < allFsms.Length; i++)
                {
                    if (allFsms[i].FsmName == "Crouch")
                    {
                        allFsms[i].SendEvent("JUMP");
                        break;
                    }
                }
            }

            SendSleepVote(false, false);
            OnLog?.Invoke("[Сон] Вы встали с постели.");

            if (transport.IsServer)
            {
                hostLayingDown = false;
                hostSmoking = false;
            }
        }

        private void SetCameraLookLocked(bool locked)
        {
            try
            {
                if (playerTr == null)
                {
                    var p = GameObject.Find("PLAYER");
                    if (p != null) playerTr = p.transform;
                }

                if (playerTr == null) return;

                if (locked)
                {
                    disabledMouseLooks.Clear();
                    var looks = playerTr.GetComponentsInChildren<MouseLook>(true);
                    for (int i = 0; i < looks.Length; i++)
                    {
                        if (looks[i] != null && looks[i].enabled)
                        {
                            looks[i].enabled = false;
                            disabledMouseLooks.Add(looks[i]);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < disabledMouseLooks.Count; i++)
                    {
                        if (disabledMouseLooks[i] != null)
                        {
                            disabledMouseLooks[i].enabled = true;
                        }
                    }
                    disabledMouseLooks.Clear();

                    // Восстанавливаем возможность оглядываться по сторонам на всех компонентах MouseLook игрока
                    var looks = playerTr.GetComponentsInChildren<MouseLook>(true);
                    for (int i = 0; i < looks.Length; i++)
                    {
                        if (looks[i] != null)
                        {
                            looks[i].enabled = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[SleepSyncManager SetCameraLookLocked Error] {ex.Message}");
            }
        }

        private bool CheckIfSmoking()
        {
            try
            {
                if (Input.GetKey(KeyCode.I)) return true;
                var smokingObj = GameObject.Find("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/Smoking");
                if (smokingObj != null && smokingObj.activeSelf) return true;
            }
            catch { }
            return false;
        }

        private void Update()
        {
            // Периодическое сканирование кроватей при загрузке мира
            if (!isBedsHooked && Time.time >= nextBedSearchTime)
            {
                nextBedSearchTime = Time.time + 2.0f;
                if (OurSummerCar.IsInGameWorld)
                {
                    FindGameReferences();
                    HookVanillaBeds();
                }
            }

            if (playerTr == null && OurSummerCar.IsInGameWorld)
            {
                var p = GameObject.Find("PLAYER");
                if (p != null) playerTr = p.transform;
            }

            if (isLocalLayingDown && !isExecutingSleep)
            {
                // Удерживаем ракурс лежания на подушке во время нахождения в кровати (крен строго 0, камера зафиксирована)
                if (playerTr != null)
                {
                    playerTr.rotation = lockedHeadRotation;

                    Transform animPivot = playerTr.Find("Pivot/AnimPivot");
                    if (animPivot != null) animPivot.localRotation = Quaternion.identity;

                    Transform fpsCam = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera");
                    if (fpsCam != null)
                    {
                        fpsCam.localPosition = new Vector3(0f, 0.22f, 0f);
                        fpsCam.localRotation = Quaternion.identity;
                    }

                    Transform pivot = playerTr.Find("Pivot");
                    if (pivot != null) pivot.localRotation = Quaternion.identity;

                    Transform camNode = playerTr.Find("Pivot/AnimPivot/Camera");
                    if (camNode != null) camNode.localRotation = Quaternion.identity;

                    Transform mainCamChild = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera");
                    if (mainCamChild != null) mainCamChild.localRotation = Quaternion.identity;

                    if (Camera.main != null)
                    {
                        Camera.main.transform.rotation = lockedHeadRotation;
                    }
                }

                // Если игрок нажал клавишу движения - встаем с кровати
                if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.A) ||
                    Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.D) ||
                    Input.GetKeyDown(KeyCode.Space))
                {
                    CancelLayingDown();
                }

                // Если игрок на сервере один (друг еще не зашел), разрешаем уснуть в одиночку через 3.5 секунды
                if (transport.IsServer && transport.ConnectedClientsCount == 0 && Time.time - soloSleepTimer > 3.5f)
                {
                    CheckCoopSleepCondition();
                }
            }
            else if (!isLocalLayingDown && !isExecutingSleep && playerTr != null)
            {
                // Активная непрерывная защита от застревания крена (roll) камеры:
                // В нормальном состоянии все узлы камеры должны иметь крен строго 0!

                // 1. Корневой объект PLAYER может иметь только поворот по Yaw (Y)
                if (Mathf.Abs(playerTr.eulerAngles.z) > 0.05f || Mathf.Abs(playerTr.eulerAngles.x) > 0.05f)
                {
                    playerTr.rotation = Quaternion.Euler(0f, playerTr.eulerAngles.y, 0f);
                }

                // 2. Pivot: сбрасываем крен Z, не мешая наклону вперед Reach (X)
                Transform pivot = playerTr.Find("Pivot");
                if (pivot != null)
                {
                    Vector3 pE = pivot.localEulerAngles;
                    if (Mathf.Abs(pE.z) > 0.05f)
                    {
                        pivot.localEulerAngles = new Vector3(pE.x, pE.y, 0f);
                    }
                }

                // 3. AnimPivot: всегда строгий identity
                Transform animPivot = playerTr.Find("Pivot/AnimPivot");
                if (animPivot != null)
                {
                    if (animPivot.localRotation != Quaternion.identity)
                    {
                        animPivot.localRotation = Quaternion.identity;
                    }
                    if (animPivot.localPosition != Vector3.zero)
                    {
                        animPivot.localPosition = Vector3.zero;
                    }
                }

                // 4. Camera (промежуточный узел): всегда identity
                Transform camNode = playerTr.Find("Pivot/AnimPivot/Camera");
                if (camNode != null && camNode.localRotation != Quaternion.identity)
                {
                    camNode.localRotation = Quaternion.identity;
                }

                // 5. FPSCamera (MouseLook): крен Z строго 0
                Transform fpsCam = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera");
                if (fpsCam != null)
                {
                    Vector3 fE = fpsCam.localEulerAngles;
                    if (Mathf.Abs(fE.z) > 0.05f || Mathf.Abs(fE.y) > 0.05f)
                    {
                        fpsCam.localEulerAngles = new Vector3(fE.x, 0f, 0f);
                    }
                }

                // 6. Активная камера FPSCamera/FPSCamera и Camera.main: сбрасываем любой крен
                Transform mainCamChild = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera");
                if (mainCamChild != null && mainCamChild.localRotation != Quaternion.identity)
                {
                    mainCamChild.localRotation = Quaternion.identity;
                }

                if (Camera.main != null && Camera.main.transform != null)
                {
                    Vector3 cE = Camera.main.transform.localEulerAngles;
                    if (Mathf.Abs(cE.z) > 0.05f)
                    {
                        Camera.main.transform.localEulerAngles = new Vector3(cE.x, cE.y, 0f);
                    }
                }
            }
        }

        private void OnGUI()
        {
            if (isLocalLayingDown && !isExecutingSleep)
            {
                GUI.color = Color.white;
                GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                GUI.skin.label.fontSize = 20;

                string statusText = transport.IsServer
                    ? (clientLayingDown ? "ОБА ИГРОКА ГОТОВЫ... ЗАСЫПАЕМ" : (transport.ConnectedClientsCount == 0 ? "ОДИНОЧНЫЙ СОН... ЗАСЫПАЕМ" : "ВЫ В ПОСТЕЛИ. ОЖИДАНИЕ ВТОРОГО ИГРОКА...\n(WASD или Пробел для отмены)"))
                    : (hostLayingDown ? "ОБА ИГРОКА ГОТОВЫ... ЗАСЫПАЕМ" : "ВЫ В ПОСТЕЛИ. ОЖИДАНИЕ ВТОРОГО ИГРОКА...\n(WASD или Пробел для отмены)");

                if (isLocalSmoking)
                {
                    statusText += "\n<color=red>ВНИМАНИЕ: СИГАРЕТА ВО РТУ! ОПАСНОСТЬ ПОЖАРА!</color>";
                }

                GUI.Label(new Rect(0, Screen.height * 0.7f, Screen.width, 100), statusText);
            }
        }

        private void CheckCoopSleepCondition()
        {
            if (!transport.IsServer) return;

            bool canSleep = (hostLayingDown && clientLayingDown) || (hostLayingDown && transport.ConnectedClientsCount == 0);

            if (canSleep && !isExecutingSleep)
            {
                int sleepHours = 8;
                if (fatigueVar != null)
                {
                    sleepHours = Mathf.Clamp(Mathf.FloorToInt(fatigueVar.Value / 12f) * 2, 2, 12);
                }

                bool isFire = hostSmoking || clientSmoking;

                // Сервер рассылает команду на совместный сон
                using (var packet = new PacketWriter(PacketType.SleepExecute))
                {
                    packet.WriteByte((byte)sleepHours);
                    packet.WriteBoolean(isFire);
                    transport.Send(packet);
                }

                StartCoroutine(SleepRoutine(sleepHours, isFire));
            }
        }

        private IEnumerator SleepRoutine(int sleepHours, bool isFire)
        {
            isExecutingSleep = true;
            OnLog?.Invoke($"[Сон] Наступила ночь. Синхронный сон на {sleepHours} ч...");

            try
            {
                // Затемнение экрана / анимация глаз
                if (sleepEyes != null)
                {
                    sleepEyes.gameObject.SetActive(true);
                    sleepEyes.Play("sleep_on");
                }

                yield return new WaitForSeconds(3.5f);

                // Перевод игрового времени ровно на одну ночь
                if (timeVar != null)
                {
                    int newTime = timeVar.Value + sleepHours;
                    if (newTime >= 24)
                    {
                        newTime -= 24;
                        if (dayVar != null)
                        {
                            dayVar.Value = (dayVar.Value % 7) + 1;
                        }
                    }
                    timeVar.Value = Mathf.Clamp(newTime, 0, 23);
                }

                // Полное снятие усталости у обоих игроков
                if (fatigueVar != null) fatigueVar.Value = 0f;
                if (drunkVar != null) drunkVar.Value = Mathf.Max(0f, drunkVar.Value - (sleepHours * 0.2f));
                if (hungerVar != null) hungerVar.Value = Mathf.Min(100f, hungerVar.Value + (sleepHours * 2.5f));
                if (thirstVar != null) thirstVar.Value = Mathf.Min(100f, thirstVar.Value + (sleepHours * 3.5f));
                if (urineVar != null) urineVar.Value = Mathf.Min(100f, urineVar.Value + (sleepHours * 2.0f));

                yield return new WaitForSeconds(1.5f);

                // Открытие глаз
                if (sleepEyes != null)
                {
                    sleepEyes.Play("sleep_off");
                    yield return new WaitForSeconds(1.0f);
                    sleepEyes.gameObject.SetActive(false);
                }

                // Восстанавливаем позицию и вертикальную ориентацию игрока на ногах рядом с кроватью
                if (playerTr != null)
                {
                    Vector3 wakePos = playerOriginalPos;
                    if (activeBedPivot != null)
                    {
                        Transform wakeTr = (activeBedPivot.parent != null)
                            ? (activeBedPivot.parent.Find("PivotWake") ?? activeBedPivot.parent.Find("SleepTrigger/PivotWake"))
                            : activeBedPivot.Find("PivotWake");
                        if (wakeTr != null)
                        {
                            wakePos = wakeTr.position;
                        }
                        else if (wakePos.sqrMagnitude < 1f)
                        {
                            wakePos = activeBedPivot.position + activeBedPivot.right * 0.8f;
                        }
                    }
                    playerTr.position = wakePos;
                    playerTr.rotation = Quaternion.Euler(0f, playerOriginalRot.eulerAngles.y, 0f);

                    ResetPlayerCameraRoll(playerTr);

                    // Восстанавливаем высоту камеры игрока в полный рост (1.4м) через FSM Crouch
                    var allFsms = playerTr.GetComponents<PlayMakerFSM>();
                    for (int i = 0; i < allFsms.Length; i++)
                    {
                        if (allFsms[i].FsmName == "Crouch")
                        {
                            allFsms[i].SendEvent("JUMP");
                            break;
                        }
                    }
                }
            }
            finally
            {
                // Гарантированное пробуждение и освобождение игрока
                isExecutingSleep = false;
                isLocalLayingDown = false;
                hostLayingDown = false;
                clientLayingDown = false;

                SetCameraLookLocked(false);

                if (playerStopVar != null)
                {
                    playerStopVar.Value = false;
                }

                if (playerTr != null)
                {
                    ResetPlayerCameraRoll(playerTr);
                }
            }

            // ПОЖАР ДОМА: если кто-то лег с сигаретой
            if (isFire)
            {
                OnLog?.Invoke("[ПОЖАР] ОДИН ИЗ ИГРОКОВ ЗАСНУЛ С СИГАРЕТОЙ! ДОМ ЗАГОРЕЛСЯ!");
                TriggerHouseFireLocally();
            }
            else
            {
                OnLog?.Invoke("[Сон] Доброе утро! Вы проснулись бодрыми.");
            }
        }

        public void TriggerHouseFireLocally()
        {
            try
            {
                if (fireBedroomFsm != null)
                {
                    fireBedroomFsm.SendEvent("FIRE");
                }

                if (housefireFsm != null)
                {
                    housefireFsm.SendEvent("FIRE");
                    housefireFsm.Fsm.Event("State 2");
                }

                var fireTrigger = GameObject.Find("FireBedroom1") ?? GameObject.Find("FireTrigger");
                if (fireTrigger != null)
                {
                    var fsm = fireTrigger.GetComponent<PlayMakerFSM>();
                    fsm?.SendEvent("FIRE");
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[TriggerHouseFire Error] {ex.Message}");
            }
        }

        private void SendSleepVote(bool layingDown, bool smoking)
        {
            using (var packet = new PacketWriter(PacketType.SleepVote))
            {
                packet.WriteBoolean(layingDown);
                packet.WriteBoolean(smoking);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.SleepVote)
            {
                bool remoteLayingDown = reader.ReadBoolean();
                bool remoteSmoking = reader.ReadBoolean();

                if (transport.IsServer)
                {
                    clientLayingDown = remoteLayingDown;
                    clientSmoking = remoteSmoking;
                    CheckCoopSleepCondition();
                }
                else
                {
                    hostLayingDown = remoteLayingDown;
                    hostSmoking = remoteSmoking;
                }
            }
            else if (reader.Type == PacketType.SleepExecute)
            {
                int sleepHours = reader.ReadByte();
                bool isFire = reader.ReadBoolean();

                if (!transport.IsServer)
                {
                    StartCoroutine(SleepRoutine(sleepHours, isFire));
                }
            }
            else if (reader.Type == PacketType.HouseFireSync)
            {
                TriggerHouseFireLocally();
            }
        }

        private void HandleClientConnected(int clientId)
        {
            hostLayingDown = false;
            clientLayingDown = false;
            isExecutingSleep = false;
        }

        public static void ResetPlayerCameraRoll(Transform player)
        {
            if (player == null) return;

            // 1. Корневой объект игрока: крен (Z) и тангаж (X) строго 0 (вращение только по Yaw Y)
            Vector3 euler = player.eulerAngles;
            player.rotation = Quaternion.Euler(0f, euler.y, 0f);

            // 2. Pivot: сбрасываем крен Z в 0
            Transform pivot = player.Find("Pivot");
            if (pivot != null)
            {
                Vector3 pE = pivot.localEulerAngles;
                pivot.localEulerAngles = new Vector3(pE.x, pE.y, 0f);
            }

            // 3. AnimPivot: восстанавливаем под Pivot, сбрасываем локальное вращение и позицию
            Transform animPivot = player.Find("Pivot/AnimPivot");
            if (animPivot != null)
            {
                animPivot.localPosition = Vector3.zero;
                animPivot.localRotation = Quaternion.identity;
                var anim = animPivot.GetComponent<Animation>();
                if (anim != null && anim.isPlaying) anim.Stop();
            }

            // 4. Camera (промежуточный узел под AnimPivot)
            Transform camNode = player.Find("Pivot/AnimPivot/Camera");
            if (camNode != null)
            {
                camNode.localRotation = Quaternion.identity;
            }

            // 5. FPSCamera (узел с MouseLook, Pitch по X)
            Transform fpsCam = player.Find("Pivot/AnimPivot/Camera/FPSCamera");
            if (fpsCam != null)
            {
                Vector3 fE = fpsCam.localEulerAngles;
                fpsCam.localEulerAngles = new Vector3(fE.x, 0f, 0f);
                fpsCam.localPosition = new Vector3(0f, 1.4f, 0f);
            }

            // 6. Активная камера FPSCamera/FPSCamera и Camera.main
            Transform mainCamChild = player.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera");
            if (mainCamChild != null)
            {
                mainCamChild.localRotation = Quaternion.identity;
            }

            if (Camera.main != null && Camera.main.transform != null)
            {
                Vector3 cE = Camera.main.transform.localEulerAngles;
                Camera.main.transform.localEulerAngles = new Vector3(cE.x, cE.y, 0f);
            }
        }
    }

    public class CoopBedTrigger : MonoBehaviour
    {
        private SleepSyncManager manager;
        private Transform pivot;
        private Collider col;
        private Camera mainCam;
        private FsmFloat fatigueVar;
        private FsmString guiInteraction;
        private FsmBool guiUse;

        public void Initialize(SleepSyncManager mgr, Transform bedPivot)
        {
            manager = mgr;
            pivot = bedPivot;
            col = GetComponent<Collider>();
            if (col == null)
            {
                var box = gameObject.AddComponent<BoxCollider>();
                box.size = new Vector3(1.2f, 0.6f, 2.0f);
                col = box;
            }
            col.isTrigger = true;
            mainCam = Camera.main;

            if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
            {
                fatigueVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerFatigue");
                guiInteraction = PlayMakerGlobals.Instance.Variables.FindFsmString("GUIinteraction");
                guiUse = PlayMakerGlobals.Instance.Variables.FindFsmBool("GUIuse");
            }
        }

        private void Update()
        {
            if (manager == null || manager.IsLocalLayingDown || manager.IsExecutingSleep) return;

            if (mainCam == null) mainCam = Camera.main;
            if (mainCam == null || col == null) return;

            Ray ray = mainCam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (Physics.Raycast(ray, out RaycastHit hit, 2.8f))
            {
                if (hit.collider == col || hit.collider.transform.IsChildOf(transform))
                {
                    bool canSleep = fatigueVar == null || fatigueVar.Value >= 10f;
                    if (guiInteraction != null) guiInteraction.Value = canSleep ? "SLEEP" : "NOT TIRED";
                    if (guiUse != null) guiUse.Value = canSleep;

                    if (canSleep && (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.F) || Input.GetKeyDown(KeyCode.E)))
                    {
                        if (guiInteraction != null) guiInteraction.Value = "";
                        if (guiUse != null) guiUse.Value = false;
                        manager.StartLayingDown(pivot);
                    }
                }
            }
        }
    }
}
