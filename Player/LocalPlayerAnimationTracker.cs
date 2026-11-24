using System;
using System.Collections;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Environment;
using OurSummerCar.Vehicles;
using UnityEngine;

namespace OurSummerCar.Player
{
    /// <summary>
    /// Нативный перехватчик жестов, приседаний и действий локального игрока по архитектуре WreckMP.
    /// Отслеживает объекты под FPSCamera, переменные FSM Crouch и звуки SpeakDatabase.
    /// </summary>
    public class LocalPlayerAnimationTracker : MonoBehaviour
    {
        public static LocalPlayerAnimationTracker Instance { get; private set; }

        public event Action<byte, bool, byte> OnGestureAction; // (gestureIndex, isStart, extraParam)
        public event Action<byte> OnSwearAction;               // (swearIndex)
        public event Action<byte> OnCrouchAction;              // (crouchLevel: 0, 1, 2)
        public event Action<bool> OnCigaretteAction;           // (isActive)

        public byte CurrentCrouchLevel { get; private set; } = 0;
        public bool IsPlayerInCar { get; set; } = false;
        public bool IsSmoking { get; private set; } = false;
        public bool IsLyingDown => isLyingDown;

        private bool isInitialized = false;
        private float nextSearchTime = 0f;

        private PlayMakerFSM crouchFsm;
        private FsmFloat playerHeightVar;
        private FsmBool playerInCarVar;
        private FsmInt middleFingerSwearTypeVar;
        private FsmInt speakDatabaseSwearVar;

        private byte lastSentCrouch = 255;

        private float lastOneShotGestureTime = 0f;
        private byte lastOneShotGestureId = 255;
        private bool lastPushInputState = false;
        private bool lastLiftInputState = false;
        private bool hasLiftWatcher = false;
        private bool hasPushWatcher = false;

        private bool isLyingDown = false;
        private float lastCtrlPressTime = -10f;

        private CharacterController playerCc;
        private Transform pivotTr;
        private Transform animPivotTr;
        private Transform cachedFpsCam;
        private float originalCcHeight = 1.6f;
        private Vector3 originalCcCenter = Vector3.zero;
        private bool hasCachedCc = false;
        private float originalPivotLocalY = 1.6f;
        private bool hasCachedPivot = false;

        private void TriggerLocalOneShot(byte gestureIndex, byte extraParam = 0)
        {
            if (Time.time - lastOneShotGestureTime < 0.35f && lastOneShotGestureId == gestureIndex) return;
            lastOneShotGestureTime = Time.time;
            lastOneShotGestureId = gestureIndex;
            OnGestureAction?.Invoke(gestureIndex, true, extraParam);
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            if (!OurSummerCar.IsInGameWorld)
            {
                isInitialized = false;
                return;
            }

            // ПЕРВЫМ ДЕЛОМ: Всегда опрашиваем ввод игрока с клавиатуры!
            // Никаких зависимостей от поиска внутренних объектов сцены MSC!
            UpdateLieDownInput();
            UpdateGestureKeybinds();

            if (!isInitialized)
            {
                if (Time.time >= nextSearchTime)
                {
                    nextSearchTime = Time.time + 1.0f;
                    TryInitialize();
                }
            }

            // Мониторинг приседаний и нахождения в автомобиле
            UpdateCrouchState();

            // Клик левой кнопкой мыши (анимация взаимодействия/нажатия пальцем)
            if (Input.GetMouseButtonDown(0) && !IsPlayerInCar && !Cursor.visible)
            {
                TriggerLocalOneShot(6, 0); // Click gesture
            }
        }

        private void UpdateLieDownInput()
        {
            if (IsPlayerInCar)
            {
                if (isLyingDown) SetLyingDown(false);
                return;
            }

            // Двойное быстрое нажатие Control (LeftControl или RightControl)
            if (Input.GetKeyDown(KeyCode.LeftControl) || Input.GetKeyDown(KeyCode.RightControl))
            {
                float now = Time.time;
                if (now - lastCtrlPressTime < 0.50f)
                {
                    SetLyingDown(!isLyingDown);
                    lastCtrlPressTime = -10f;
                }
                else
                {
                    lastCtrlPressTime = now;
                }
            }

            // Выход из положения лежа при непрерывном перемещении, нажатии клавиш движения или подъеме камеры
            if (isLyingDown)
            {
                // Если игрок фактически пошел, побежал или нажал кнопки движения/прыжка
                bool isMoving = (playerCc != null && playerCc.velocity.sqrMagnitude > 0.04f) ||
                                Mathf.Abs(cInput.GetAxisRaw("Horizontal")) > 0.1f ||
                                Mathf.Abs(cInput.GetAxisRaw("Vertical")) > 0.1f ||
                                Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) ||
                                Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D) ||
                                Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.C);
                // Если камера уже поднялась выше уровня приседа
                bool cameraUp = (cachedFpsCam != null && cachedFpsCam.localPosition.y >= 1.10f);
                if (isMoving || cameraUp)
                {
                    SetLyingDown(false);
                }
            }
        }

        public void SetLyingDown(bool lying)
        {
            if (isLyingDown == lying) return;
            isLyingDown = lying;

            if (playerCc == null)
            {
                var player = GameObject.Find("PLAYER");
                if (player != null)
                {
                    playerCc = player.GetComponent<CharacterController>();
                    pivotTr = player.transform.Find("Pivot");
                    animPivotTr = player.transform.Find("Pivot/AnimPivot");
                    if (pivotTr != null && !hasCachedPivot)
                    {
                        originalPivotLocalY = pivotTr.localPosition.y;
                        hasCachedPivot = true;
                    }
                }
            }

            if (playerCc != null && !hasCachedCc)
            {
                originalCcHeight = playerCc.height;
                originalCcCenter = playerCc.center;
                hasCachedCc = true;
            }

            if (playerCc != null && hasCachedCc)
            {
                // Не модифицируем CharacterController локального игрока (оставляем ванильную физику)
                playerCc.height = originalCcHeight;
                playerCc.center = originalCcCenter;
            }

            UpdateCrouchState();
            ModConsole.Print($"<color=yellow>[OurSummerCar]</color> Положение тела: {(isLyingDown ? "Лежа (Prone)" : "Стоя/Присед")}");
        }

        private void UpdateGestureKeybinds()
        {
            if (IsPlayerInCar) return;

            // 1. Автостоп (O / Hitchhike) - если есть watcher, MSC нативно управляет объектом Lift
            if (!hasLiftWatcher)
            {
                if (Input.GetKeyDown(KeyCode.O))
                {
                    lastLiftInputState = !lastLiftInputState;
                    OnGestureAction?.Invoke(0, lastLiftInputState, 0);
                }
            }

            // 2. Приветствие (K)
            if (Input.GetKeyDown(KeyCode.K))
            {
                TriggerLocalOneShot(1, 0);
            }

            // 3. Удар кулаком (H / Hit)
            if (Input.GetKeyDown(KeyCode.H))
            {
                TriggerLocalOneShot(2, 0);
            }

            // 4. Толкание руками (J / Push) - если есть watcher, MSC сам активирует Hand Push
            if (!hasPushWatcher)
            {
                if (Input.GetKeyDown(KeyCode.J))
                {
                    StartCoroutine(C_PushFallback());
                }
            }

            // 5. Средний палец (M / Finger)
            if (Input.GetKeyDown(KeyCode.M))
            {
                byte swearType = (byte)(middleFingerSwearTypeVar != null ? middleFingerSwearTypeVar.Value : 0);
                TriggerLocalOneShot(4, swearType);
            }
        }

        private IEnumerator C_PushFallback()
        {
            OnGestureAction?.Invoke(3, true, 0);
            yield return new WaitForSeconds(0.85f);
            OnGestureAction?.Invoke(3, false, 0);
        }

        private static Transform FindChildRecursive(Transform root, string childName)
        {
            if (root == null) return null;
            if (root.name == childName) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindChildRecursive(root.GetChild(i), childName);
                if (found != null) return found;
            }
            return null;
        }

        private bool TryInitialize()
        {
            GameObject player = GameObject.Find("PLAYER");
            if (player == null) return false;

            // 1. Поиск FSM Crouch
            try
            {
                crouchFsm = player.GetPlayMaker("Crouch");
                if (crouchFsm != null)
                {
                    crouchFsm.Initialize();
                    playerHeightVar = crouchFsm.FsmVariables.FindFsmFloat("Position") ?? crouchFsm.FsmVariables.FindFsmFloat("PlayerHeight");
                    playerInCarVar = crouchFsm.FsmVariables.FindFsmBool("PlayerInCar");
                }
            }
            catch { }

            // 2. Поиск камеры
            Transform fpsCamTr = null;
            try
            {
                fpsCamTr = player.transform.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera") ??
                           player.transform.Find("Pivot/AnimPivot/Camera/FPSCamera");
                if (fpsCamTr == null)
                {
                    var cam = player.GetComponentInChildren<Camera>();
                    if (cam != null) fpsCamTr = cam.transform;
                }
                cachedFpsCam = fpsCamTr;
            }
            catch { }

            // 3. Поиск PlayerFunctions для матов и питья
            try
            {
                PlayMakerFSM playerFuncFsm = fpsCamTr != null ? fpsCamTr.GetPlayMaker("PlayerFunctions") : null;
                if (playerFuncFsm == null && fpsCamTr != null && fpsCamTr.parent != null)
                {
                    playerFuncFsm = fpsCamTr.parent.GetPlayMaker("PlayerFunctions");
                }
                if (playerFuncFsm == null)
                {
                    var allFsms = player.GetComponentsInChildren<PlayMakerFSM>(true);
                    for (int i = 0; i < allFsms.Length; i++)
                    {
                        if (allFsms[i].FsmName == "PlayerFunctions")
                        {
                            playerFuncFsm = allFsms[i];
                            break;
                        }
                    }
                }

                if (playerFuncFsm != null)
                {
                    playerFuncFsm.Initialize();
                    middleFingerSwearTypeVar = playerFuncFsm.FsmVariables.FindFsmInt("RandomInt");

                    foreach (var state in playerFuncFsm.FsmStates)
                    {
                        string sName = state.Name.ToUpperInvariant();
                        if (sName.Contains("DRINK") || sName.Contains("BEER") || sName.Contains("BOOZE") || sName.Contains("VIINA") || sName.Contains("SPIRIT"))
                        {
                            playerFuncFsm.InsertAction(state.Name, () =>
                            {
                                var held = Items.ItemManager.Instance != null ? Items.ItemManager.Instance.GetCurrentHeldItem() : null;
                                if (held != null && held.name.ToLower().Contains("bag")) return;
                                if (Items.ItemManager.IsBagUnpackCooldown) return;
                                TriggerDrinkBeerGesture();
                                PlayerManager.Instance?.SetDrinkingBeer(2.5f);
                            }, 0, false);
                        }
                    }
                }
            }
            catch { }

            // 4. Перехват переключения видимости жестов камеры
            try
            {
                var liftObj = (fpsCamTr != null ? fpsCamTr.Find("Lift") : null) ?? FindChildRecursive(player.transform, "Lift");
                if (liftObj != null)
                {
                    hasLiftWatcher = true;
                    var watcher = liftObj.GetComponent<GameobjectToggleWatcher>() ?? liftObj.gameObject.AddComponent<GameobjectToggleWatcher>();
                    watcher.Toggled = (active) =>
                    {
                        lastLiftInputState = active;
                        OnGestureAction?.Invoke(0, active, 0);
                    };
                }

                var helloObj = (fpsCamTr != null ? fpsCamTr.Find("Hello") : null) ?? FindChildRecursive(player.transform, "Hello");
                if (helloObj != null && helloObj.GetComponent<GameobjectToggleWatcher>() == null)
                {
                    var watcher = helloObj.gameObject.AddComponent<GameobjectToggleWatcher>();
                    watcher.Toggled = (active) => { if (active) TriggerLocalOneShot(1, 0); };
                }

                var fistObj = (fpsCamTr != null ? fpsCamTr.Find("Fist") : null) ?? FindChildRecursive(player.transform, "Fist");
                if (fistObj != null && fistObj.GetComponent<GameobjectToggleWatcher>() == null)
                {
                    var watcher = fistObj.gameObject.AddComponent<GameobjectToggleWatcher>();
                    watcher.Toggled = (active) => { if (active) TriggerLocalOneShot(2, 0); };
                }

                var pushObj = (fpsCamTr != null ? fpsCamTr.Find("Hand Push") : null) ?? FindChildRecursive(player.transform, "Hand Push");
                if (pushObj != null)
                {
                    hasPushWatcher = true;
                    var watcher = pushObj.GetComponent<GameobjectToggleWatcher>() ?? pushObj.gameObject.AddComponent<GameobjectToggleWatcher>();
                    watcher.Toggled = (active) =>
                    {
                        lastPushInputState = active;
                        OnGestureAction?.Invoke(3, active, 0);
                    };
                }

                var mfObj = (fpsCamTr != null ? fpsCamTr.Find("MiddleFinger") : null) ?? FindChildRecursive(player.transform, "MiddleFinger");
                if (mfObj != null && mfObj.GetComponent<GameobjectToggleWatcher>() == null)
                {
                    var watcher = mfObj.gameObject.AddComponent<GameobjectToggleWatcher>();
                    watcher.Toggled = (active) =>
                    {
                        if (active)
                        {
                            byte swearType = (byte)(middleFingerSwearTypeVar != null ? middleFingerSwearTypeVar.Value : 0);
                            TriggerLocalOneShot(4, swearType);
                        }
                    };
                }
            }
            catch { }

            // 5. Курение
            try
            {
                var smokeObj = (fpsCamTr != null ? fpsCamTr.Find("Smoking") : null) ?? FindChildRecursive(player.transform, "Smoking");
                if (smokeObj != null)
                {
                    if (smokeObj.GetComponent<GameobjectToggleWatcher>() == null)
                    {
                        var watcher = smokeObj.gameObject.AddComponent<GameobjectToggleWatcher>();
                        watcher.Toggled = (active) =>
                        {
                            IsSmoking = active;
                            OnCigaretteAction?.Invoke(active);
                        };
                    }

                    var smokeFsm = smokeObj.GetPlayMaker("Start");
                    if (smokeFsm != null)
                    {
                        smokeFsm.Initialize();
                        smokeFsm.InsertAction("Double", () => OnGestureAction?.Invoke(5, true, 0), -1, false);
                        smokeFsm.InsertAction("Anim 3", () => OnGestureAction?.Invoke(5, false, 0), -1, false);
                        smokeFsm.InsertAction("Put out 2", () => OnGestureAction?.Invoke(5, false, 0), -1, false);
                    }
                }
            }
            catch { }

            // 6. SpeakDatabase/Speech
            try
            {
                Transform speakDbTr = (fpsCamTr != null && fpsCamTr.parent != null ? fpsCamTr.parent.Find("SpeakDatabase") : null) ??
                                      player.transform.Find("Pivot/AnimPivot/Camera/SpeakDatabase") ??
                                      player.transform.Find("Pivot/AnimPivot/SpeakDatabase") ??
                                      FindChildRecursive(player.transform, "SpeakDatabase");
                if (speakDbTr != null)
                {
                    var speechFsm = speakDbTr.GetPlayMaker("Speech");
                    if (speechFsm != null)
                    {
                        speechFsm.Initialize();
                        speakDatabaseSwearVar = speechFsm.FsmVariables.FindFsmInt("RandomInt");
                        speechFsm.InsertAction("Swear", () =>
                        {
                            byte swearId = (byte)(speakDatabaseSwearVar != null ? speakDatabaseSwearVar.Value : 0);
                            OnSwearAction?.Invoke(swearId);
                        }, -1, false);
                    }
                }
            }
            catch { }

            isInitialized = true;
            ModConsole.Print("<color=green>[OurSummerCar]</color> Трекер анимаций локального игрока успешно подключен к MSC!");
            return true;
        }

        private void UpdateCrouchState()
        {
            bool inCar = (playerInCarVar != null && playerInCarVar.Value) || 
                         (VehiclePhysicsManager.Instance != null && VehiclePhysicsManager.Instance.IsLocalPlayerDriving());
            bool inBed = (SleepSyncManager.Instance != null && SleepSyncManager.Instance.IsLocalLayingDown);
            IsPlayerInCar = inCar;

            // Если персонаж физически выпрямился в полный рост по высоте камеры (camY >= 1.15f) вне машины/кровати
            if (!inCar && !inBed && cachedFpsCam != null && cachedFpsCam.localPosition.y >= 1.15f)
            {
                if (isLyingDown)
                {
                    SetLyingDown(false);
                }
            }

            byte level = 0;
            if (inCar)
            {
                level = 2; // В машине посадка приравнена к положению 2
            }
            else if (inBed)
            {
                level = 0; // Во время сна тело нейтрально прямое (поворот на бок делает синхронизатор сна)
            }
            else if (!inCar && !inBed && cachedFpsCam != null && cachedFpsCam.localPosition.y >= 1.15f)
            {
                isLyingDown = false;
                level = 0; // Гарантированно стоя!
            }
            else if (isLyingDown)
            {
                level = 2; // Положение лежа
            }
            else if (crouchFsm != null)
            {
                string state = crouchFsm.ActiveStateName;
                if (state == "Wait key")
                {
                    level = 0; // Строго стоя!
                }
                else if (state == "Crouched 2" || state == "Move down 2")
                {
                    level = 2; // Полное приседание/лежание
                }
                else if (state == "Crouched 1" || state == "Move down 1" || state == "Crouch" || state == "Move down")
                {
                    level = 1; // Полуприсед
                }
                else if (cachedFpsCam != null)
                {
                    float camY = cachedFpsCam.localPosition.y;
                    if (camY >= 1.15f) level = 0;
                    else if (camY >= 0.55f) level = 1;
                    else level = 2;
                }
                else if (playerHeightVar != null)
                {
                    float h = playerHeightVar.Value;
                    if (h >= 1.0f) level = 0;
                    else if (h >= 0.45f) level = 1;
                    else level = 2;
                }
            }
            else if (cachedFpsCam != null)
            {
                float camY = cachedFpsCam.localPosition.y;
                if (camY >= 1.15f) level = 0;
                else if (camY >= 0.55f) level = 1;
                else level = 2;
            }
            else if (cInput.GetButton("Crouch") || Input.GetKey(KeyCode.C))
            {
                level = 1;
            }

            CurrentCrouchLevel = level;

            if (level != lastSentCrouch)
            {
                lastSentCrouch = level;
                OnCrouchAction?.Invoke(level);
            }
        }

        /// <summary>
        /// Вызывается при употреблении пива из инвентаря / ящика.
        /// </summary>
        public void TriggerDrinkBeerGesture()
        {
            if (Items.ItemManager.IsBagUnpackCooldown) return;
            OnGestureAction?.Invoke(7, true, 0); // Gesture 7: drinkbeer
        }

        public void Reset()
        {
            if (isLyingDown) SetLyingDown(false);
            isLyingDown = false;
            lastCtrlPressTime = -10f;
            playerCc = null;
            pivotTr = null;
            animPivotTr = null;
            hasCachedCc = false;
            hasCachedPivot = false;

            isInitialized = false;
            crouchFsm = null;
            playerHeightVar = null;
            playerInCarVar = null;
            middleFingerSwearTypeVar = null;
            speakDatabaseSwearVar = null;
            lastSentCrouch = 255;
            nextSearchTime = 0f;
        }

        private void OnDestroy()
        {
            Instance = null;
        }
    }
}
