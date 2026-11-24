using System;
using System.Collections;
using UnityEngine;

namespace OurSummerCar.Player
{
    /// <summary>
    /// Контроллер воспроизведения анимаций удалённой куклы (RemotePuppet) по модели WreckMP.
    /// Управляет двумя аниматорами (локомоция + жесты), каналами AnimatorSyncer, 
    /// плавным приседанием, idle-действиями и позами в автомобиле.
    /// </summary>
    public class PuppetAnimationController : MonoBehaviour
    {
        public Animator animator;
        public Animator animatorGestures;
        public AnimatorSyncer syncer;
        public Transform headPivot;
        public Transform rotationBendPivot;
        public Transform charTransform;
        public float targetHeadYaw;
        public GameObject cigaretteObject;
        public GameObject beerBottleObject;
        public FastIKFabric rightHandIK;

        private static readonly string[] gesture_keywords = new string[]
        {
            "hitchhike", "wave", "hit", "push", "finger", "smoke", "click", "drinkbeer"
        };
        private static readonly string[] gesture_state_names = new string[]
        {
            "Lift", "Hello", "Fist", "HandPush", "MiddleFinger", "Smoke", "click", "Drink"
        };
        private static readonly int[] gesture_hashes = new int[]
        {
            Animator.StringToHash("hitchhike"),
            Animator.StringToHash("wave"),
            Animator.StringToHash("hit"),
            Animator.StringToHash("push"),
            Animator.StringToHash("finger"),
            Animator.StringToHash("smoke"),
            Animator.StringToHash("click"),
            Animator.StringToHash("drinkbeer")
        };
        private static readonly int HashWalking = Animator.StringToHash("walking");
        private static readonly int HashRunning = Animator.StringToHash("running");
        private static readonly int HashCrouch = Animator.StringToHash("crouch");
        private static readonly int HashPassenger = Animator.StringToHash("passenger");
        private static readonly int HashIdleAction = Animator.StringToHash("idleAction");
        private static readonly int HashSmoke = Animator.StringToHash("smoke");

        private static readonly bool[] gesture_onetime = new bool[] { false, true, true, false, true, false, true, true };
        private static readonly bool[] gesture_lefthand = new bool[] { false, false, false, true, true, false, false, true };
        private static readonly bool[] gesture_righthand = new bool[] { true, true, true, true, true, true, true, false };

        private static readonly float[] idleActionsLengths = new float[]
        {
            0f, 2.5f, 2.25f, 4f, 3.25f, 1.7f, 3.1f, 4f, 0f, 2f, 6f
        };

        public int CurrentGesture => currentGesture;
        public float CurrentTorsoPitch => currentTorsoPitch;
        public Action OnLateUpdateAction;

        private int currentGesture = -1;
        private float gestureStartTime = 0f;
        private Coroutine currentGestureCoroutine;

        private byte currentCrouchLevel = 0;
        private float targetCharY = -0.38f;
        private float currentCharY = -0.38f;

        private bool isInCar = false;
        private bool isPassenger = false;
        private bool wasPassenger = false;
        private bool wasAirborne = false;
        private bool isHoldingItem = false;
        private bool isSmoking = false;
        private Coroutine stopSmokingCoroutine;
        private bool isLeaningForward = false;
        private float currentLeanRoll = 0f;
        private float currentTorsoPitch = 0f;

        private float idleActionTimer = 15f;
        private int currentIdleAction = 0;

        private void Start()
        {
            idleActionTimer = UnityEngine.Random.Range(8f, 25f);
            if (charTransform != null)
            {
                Vector3 lp = charTransform.localPosition;
                lp.y = -0.38f;
                charTransform.localPosition = lp;
                currentCharY = -0.38f;
                targetCharY = -0.38f;
            }
        }

        private void Update()
        {
            // 1. Плавная интерполяция высоты персонажа при приседании
            if (charTransform != null && Mathf.Abs(currentCharY - targetCharY) > 0.005f)
            {
                currentCharY = Mathf.Lerp(currentCharY, targetCharY, Time.deltaTime * 6f);
                Vector3 lp = charTransform.localPosition;
                lp.y = currentCharY;
                charTransform.localPosition = lp;
            }
        }

        private void LateUpdate()
        {
            // Повторно закрепляем угол наклона позвоночника в LateUpdate для точной позы перед IK (-Z наклоняет торс ВПЕРЕД)
            if (!isInCar && rotationBendPivot != null)
            {
                float rollX = isLeaningForward ? 0f : -currentLeanRoll;
                rotationBendPivot.localEulerAngles = new Vector3(rollX, 0f, -currentTorsoPitch);
            }

            if (OnLateUpdateAction != null)
            {
                OnLateUpdateAction();
            }
        }

        /// <summary>
        /// Обновляет параметры локомоции (ходьба, бег, прыжок, присед, idle).
        /// </summary>
        public void UpdateLocomotion(float speed, byte crouchLevel, bool inCar, bool passenger, bool holdingItem, bool isSleeping = false, bool isAirborne = false)
        {
            isInCar = inCar;
            isPassenger = passenger;
            isHoldingItem = holdingItem;

            // Обновление высоты приседания (стандарт WreckMP: -0.38f / -0.88f / -1.08f)
            SetCrouchLevel(crouchLevel);

            if (animator == null) return;

            if (inCar && !passenger)
            {
                // Поза водителя: торс наклонен вперед к рулю, ноги в локомоции, без PassengerMode
                animator.enabled = true;
                animator.SetBool(HashPassenger, false);
                animator.SetBool(HashWalking, false);
                animator.SetBool(HashRunning, false);
                animator.SetInteger(HashCrouch, 0);
                if (rotationBendPivot != null)
                {
                    rotationBendPivot.localEulerAngles = Vector3.forward * -16f;
                }
                if (syncer != null)
                {
                    syncer.lockStraightLegs = false;
                    if (currentGesture == -1)
                    {
                        syncer.leftArm = 0;
                        syncer.rightArm = isSmoking ? 2 : 0;
                    }
                    syncer.SetLegChannels(1, 1);
                }
                wasPassenger = false;
                wasAirborne = false;
                return;
            }

            if (!inCar && rotationBendPivot != null)
            {
                // Положение позвоночника контролируется через SetPitchAndLean
            }

            animator.enabled = true;

            if (syncer != null && currentGesture == -1)
            {
                syncer.rightArm = isSmoking ? 2 : ((holdingItem || inCar) ? 0 : 1);
                syncer.leftArm = inCar ? 0 : 1;
            }

            if (isSleeping)
            {
                if (syncer != null)
                {
                    syncer.lockStraightLegs = true;
                    syncer.SetLegChannels(0, 0);
                }
                if (currentIdleAction != 0)
                {
                    ResetIdleAction();
                }
            }
            else if (!inCar)
            {
                if (syncer != null && syncer.lockStraightLegs)
                {
                    syncer.lockStraightLegs = false;
                    syncer.SetLegChannels(1, 1);
                }
            }

            if (passenger)
            {
                animator.SetBool(HashPassenger, true);
                if (!wasPassenger && !animator.GetCurrentAnimatorStateInfo(0).IsName("PassengerMode"))
                {
                    animator.Play("PassengerMode");
                }
                if (syncer != null)
                {
                    syncer.lockStraightLegs = false;
                    if (currentGesture == -1 && !isSmoking)
                    {
                        syncer.ResetChannels();
                    }
                }
                wasPassenger = true;
                wasAirborne = false;
                return;
            }

            wasPassenger = false;
            animator.SetBool(HashPassenger, false);

            bool isWalking = speed > 0.15f;
            bool isRunning = speed > 3.5f;

            animator.SetBool(HashWalking, isWalking);
            animator.SetBool(HashRunning, isRunning);
            animator.SetInteger(HashCrouch, crouchLevel);

            if (isAirborne && !wasAirborne && !inCar && !isSleeping)
            {
                animator.CrossFade("jump", 0.08f);
            }
            else if (!isAirborne && !animator.IsInTransition(0))
            {
                var curState = animator.GetCurrentAnimatorStateInfo(0);
                if (crouchLevel == 2)
                {
                    if (isWalking || isRunning)
                    {
                        if (!curState.IsName("crouch_floor_walk") && !curState.IsName("crouch_floor_walk3"))
                        {
                            animator.CrossFade("crouch_floor_walk", 0.2f);
                        }
                    }
                    else
                    {
                        if (!curState.IsName("crouch_floor_idle") && !curState.IsName("crouch_floor"))
                        {
                            animator.CrossFade("crouch_floor_idle", 0.2f);
                        }
                    }
                }
                else if (crouchLevel == 1)
                {
                    if (isWalking || isRunning)
                    {
                        if (!curState.IsName("crouch_walk"))
                        {
                            animator.CrossFade("crouch_walk", 0.2f);
                        }
                    }
                    else
                    {
                        if (!curState.IsName("crouch_idle") && !curState.IsName("crouch_idleH") && !curState.IsName("crouch"))
                        {
                            animator.CrossFade("crouch_idle", 0.2f);
                        }
                    }
                }
                else if (crouchLevel == 0)
                {
                    // При выходе в стоячее положение однократно гарантируем выход из любых сидячих/лежачих состояний
                    if (curState.IsName("crouch_idle") || curState.IsName("crouch_idleH") || curState.IsName("crouch") ||
                        curState.IsName("crouch_walk") || curState.IsName("crouch_floor_idle") || curState.IsName("crouch_floor") ||
                        curState.IsName("crouch_floor_walk") || curState.IsName("crouch_floor_walk3"))
                    {
                        if (isWalking || isRunning)
                        {
                            animator.CrossFade(isRunning ? "sprint" : "walk", 0.2f);
                        }
                        else
                        {
                            animator.CrossFade("crouch_standup", 0.2f);
                        }
                    }
                }
            }

            wasAirborne = isAirborne && !inCar && !isSleeping;

            // Обработка случайных idle-действий при покое (только когда не спим и не в воздухе)
            bool isStationary = speed <= 0.15f && currentGesture == -1 && !isAirborne;
            if (isStationary && !isSleeping)
            {
                idleActionTimer -= Time.deltaTime;
                if (idleActionTimer <= 0f && currentIdleAction == 0)
                {
                    TriggerIdleAction();
                }
            }
            else
            {
                if (currentIdleAction != 0)
                {
                    ResetIdleAction();
                }
                idleActionTimer = UnityEngine.Random.Range(8f, 25f);
            }
        }

        public void SetHeadPitch(float headPitch)
        {
            SetPitchAndLean(headPitch, 0f, false);
        }

        public void SetPitchAndLean(float headPitch, float leanRoll)
        {
            SetPitchAndLean(headPitch, leanRoll, false);
        }

        public void SetPitchAndLean(float headPitch, float leanRoll, bool isLeaningForward)
        {
            this.isLeaningForward = isLeaningForward;
            currentLeanRoll = isLeaningForward ? 0f : Mathf.Lerp(currentLeanRoll, leanRoll, Time.deltaTime * 10f);

            // Если персонаж не в машине, распределяем pitch между позвоночником (наклон туловища) и головой
            if (!isInCar)
            {
                // Наклон туловища вперед на 45 градусов при зажатой Q ("поза раком"). В покое туловище держится ровно
                float baseTorso = isLeaningForward ? 45f : Mathf.Clamp(headPitch * 0.15f, -5f, 15f);
                currentTorsoPitch = Mathf.Lerp(currentTorsoPitch, baseTorso, Time.deltaTime * 10f);

                if (rotationBendPivot != null)
                {
                    // Вращение позвоночника: -Z = наклон вперед ("раком"), X = наклон вбок
                    float rollX = isLeaningForward ? 0f : -currentLeanRoll;
                    rotationBendPivot.localEulerAngles = new Vector3(rollX, 0f, -currentTorsoPitch);
                }

                if (headPivot != null && (syncer == null || syncer.head == 0))
                {
                    float headRemainingPitch = headPitch - (isLeaningForward ? (currentTorsoPitch * 0.4f) : currentTorsoPitch);
                    headPivot.localEulerAngles = Vector3.forward * -headRemainingPitch;
                }
            }
            else
            {
                // В автомобиле: наклон позвоночника 16 градусов вперед к рулю (-16f по оси Z)
                if (rotationBendPivot != null)
                {
                    rotationBendPivot.localEulerAngles = new Vector3(-currentLeanRoll, 0f, -16f);
                }

                if (headPivot != null && (syncer == null || syncer.head == 0))
                {
                    // В автомобиле/на мопеде тело смотрит строго вперед по сиденью,
                    // а голова свободно вращается за взглядом игрока в горизонтальной и вертикальной плоскостях
                    float rootYaw = (charTransform != null && charTransform.parent != null)
                        ? charTransform.parent.eulerAngles.y
                        : (transform.root != null ? transform.root.eulerAngles.y : 0f);
                    float relYaw = Mathf.DeltaAngle(rootYaw, targetHeadYaw);
                    relYaw = Mathf.Clamp(relYaw, -95f, 95f);
                    float clampedYaw = rootYaw + relYaw;
                    headPivot.eulerAngles = new Vector3(0f, clampedYaw - 90f, -headPitch);
                }
            }
        }

        public void SetCrouchLevel(byte level)
        {
            if (currentCrouchLevel == level) return;
            byte prevLevel = currentCrouchLevel;
            currentCrouchLevel = level;

            // Высота модели char (стандарт WreckMP): 0 = -0.38f (стоя), 1 = -0.88f (полуприсед), 2 = -1.08f (лежа/в авто)
            targetCharY = (level == 0) ? -0.38f : ((level == 1) ? -0.88f : -1.08f);

            if (animator != null && animator.isActiveAndEnabled)
            {
                if (level == 2)
                {
                    // Мгновенный плавный вход в позу лежа на полу
                    animator.CrossFade("crouch_floor_idle", 0.2f);
                }
                else if (level == 1)
                {
                    // Переход в присед
                    animator.CrossFade("crouch_idle", 0.2f);
                }
                else if (level == 0)
                {
                    // Подъем в полный рост из приседа или лежа
                    animator.CrossFade("crouch_standup", 0.2f);
                }
            }
        }

        public void SetSmoking(bool smoking)
        {
            if (isSmoking == smoking) return;
            isSmoking = smoking;

            if (stopSmokingCoroutine != null)
            {
                StopCoroutine(stopSmokingCoroutine);
                stopSmokingCoroutine = null;
            }

            if (smoking)
            {
                if (cigaretteObject != null)
                {
                    cigaretteObject.SetActive(true);
                }
                if (animatorGestures != null)
                {
                    animatorGestures.SetBool(HashSmoke, true);
                }
                if (syncer != null && currentGesture == -1)
                {
                    syncer.rightArm = 2; // Gestures
                }
            }
            else
            {
                stopSmokingCoroutine = StartCoroutine(C_StopSmokingSmooth());
            }
        }

        private IEnumerator C_StopSmokingSmooth()
        {
            if (animatorGestures != null)
            {
                animatorGestures.SetBool(HashSmoke, false);
            }

            // Плавное возвращение руки в исходное положение (длительность перехода в дыхание ~0.45с)
            yield return new WaitForSeconds(0.45f);

            if (cigaretteObject != null)
            {
                cigaretteObject.SetActive(false);
            }

            if (syncer != null && currentGesture == -1 && !isSmoking)
            {
                syncer.rightArm = (isHoldingItem || isInCar) ? 0 : 1;
            }
            stopSmokingCoroutine = null;
        }

        public void TriggerGesture(int index, byte extraParam = 0)
        {
            if (index < 0 || index >= gesture_keywords.Length) return;
            if (index == 7 && Items.ItemManager.IsBagUnpackCooldown) return;

            // Если циклический жест уже активен, не прерывать и не перезапускать его
            if (currentGesture == index && !gesture_onetime[index]) return;

            if (currentIdleAction != 0)
            {
                ResetIdleAction();
            }

            if (currentGestureCoroutine != null)
            {
                StopCoroutine(currentGestureCoroutine);
                currentGestureCoroutine = null;
            }

            currentGestureCoroutine = StartCoroutine(C_TriggerGesture(index, extraParam));
        }

        public void StopGesture(int index = -1)
        {
            int g = (index >= 0) ? index : currentGesture;
            if (g < 0 || g >= gesture_keywords.Length) return;

            if (gesture_onetime[g]) return;
            // Не останавливать, если этот жест сейчас не активен
            if (currentGesture != g) return;

            if (currentGestureCoroutine != null)
            {
                StopCoroutine(currentGestureCoroutine);
                currentGestureCoroutine = null;
            }

            StartCoroutine(C_StopGesture(g));
        }

        private IEnumerator C_TriggerGesture(int index, byte extraParam)
        {
            if (currentGesture != -1 && currentGesture != index && !gesture_onetime[currentGesture])
            {
                yield return StartCoroutine(C_StopGesture(currentGesture));
            }

            currentGesture = index;
            gestureStartTime = Time.time;
            bool isOneTime = gesture_onetime[index];
            int hash = gesture_hashes[index];

            if (animatorGestures != null)
            {
                if (isOneTime)
                {
                    animatorGestures.SetTrigger(hash);
                }
                else
                {
                    animatorGestures.SetBool(hash, true);
                }

                // Прямой запуск анимации в слое гарантирует немедленное начало движения
                if (index >= 0 && index < gesture_state_names.Length && !string.IsNullOrEmpty(gesture_state_names[index]))
                {
                    animatorGestures.Play(gesture_state_names[index], 0, 0f);
                }
            }

            // Маршрутизация каналов костей в AnimatorSyncer (2 = Gestures)
            if (syncer != null)
            {
                if (gesture_lefthand[index]) syncer.leftArm = 2;
                if (gesture_righthand[index]) syncer.rightArm = 2;
                if (index == 7) syncer.head = 2; // drinkbeer запрокидывает голову
            }

            if (rightHandIK != null && gesture_righthand[index])
            {
                rightHandIK.enabled = false;
            }

            // Специальные эффекты для конкретных жестов
            if (index == 4) // MiddleFinger -> Swear Voice
            {
                PlaySpokenAudio("Fuck", (extraParam > 0 ? extraParam : (byte)UnityEngine.Random.Range(1, 10)).ToString());
            }
            else if (index == 7) // DrinkBeer
            {
                PlayAudioSafe("HouseFoley", "bottle_cap");
                if (beerBottleObject != null) beerBottleObject.SetActive(true);

                yield return new WaitForSeconds(2.4f);

                string burbName = "burb0" + UnityEngine.Random.Range(1, 3).ToString();
                PlayAudioSafe("Burb", burbName);

                yield return new WaitForSeconds(0.9f);
            }

            if (isOneTime)
            {
                yield return new WaitForSeconds(0.6f);

                if (animatorGestures != null)
                {
                    float timeout = 1.8f;
                    while (timeout > 0f)
                    {
                        var state = animatorGestures.GetCurrentAnimatorStateInfo(0);
                        if (state.IsName("breath") || state.IsName("Breath")) break;
                        timeout -= Time.deltaTime;
                        yield return null;
                    }
                }

                // Восстановление каналов Syncer к исходным
                RestoreSyncerChannels(index);
                if (index == 7 && beerBottleObject != null) beerBottleObject.SetActive(false);
                currentGesture = -1;
            }

            if (index == 7 && beerBottleObject != null) beerBottleObject.SetActive(false);

            currentGestureCoroutine = null;
        }

        private IEnumerator C_StopGesture(int index)
        {
            if (index == 7 && beerBottleObject != null)
            {
                beerBottleObject.SetActive(false);
            }

            // Гарантируем минимальное удержание жеста (для пуша 0.70с, для остальных 0.40с)
            float minHold = (index == 3) ? 0.70f : 0.40f;
            float elapsed = Time.time - gestureStartTime;
            if (elapsed < minHold)
            {
                yield return new WaitForSeconds(minHold - elapsed);
            }

            if (animatorGestures != null)
            {
                animatorGestures.SetBool(gesture_hashes[index], false);
                if (index == 3) // Hand Push -> переход в HandPushOver
                {
                    animatorGestures.Play("HandPushOver", 0, 0f);
                }

                // Ждем смены кадра для завершения перехода
                yield return null;

                float timeout = 1.4f;
                while (timeout > 0f)
                {
                    var state = animatorGestures.GetCurrentAnimatorStateInfo(0);
                    if (state.IsName("breath") || state.IsName("Breath")) break;
                    timeout -= Time.deltaTime;
                    yield return null;
                }
            }

            RestoreSyncerChannels(index);
            currentGesture = -1;
        }

        /// <summary>
        /// Принудительный сброс рук в нормальное локомоторное состояние при отпускании предметов или десинхронизации.
        /// </summary>
        public void ForceResetArms(bool holdingItem = false)
        {
            isHoldingItem = holdingItem;

            if (currentGestureCoroutine != null)
            {
                StopCoroutine(currentGestureCoroutine);
                currentGestureCoroutine = null;
            }

            if (animatorGestures != null && currentGesture >= 0 && currentGesture < gesture_hashes.Length)
            {
                animatorGestures.SetBool(gesture_hashes[currentGesture], false);
            }

            currentGesture = -1;

            if (syncer != null)
            {
                syncer.leftArm = isInCar ? 0 : 1;
                syncer.rightArm = (isHoldingItem || isInCar) ? 0 : 1;
                syncer.head = 0;
            }

            if (beerBottleObject != null) beerBottleObject.SetActive(false);
            if (rightHandIK != null && !isHoldingItem) rightHandIK.enabled = false;
        }

        private void RestoreSyncerChannels(int index)
        {
            if (syncer == null) return;

            if (gesture_lefthand[index])
            {
                syncer.leftArm = isInCar ? 0 : 1;
            }
            if (gesture_righthand[index])
            {
                syncer.rightArm = (isHoldingItem || isInCar) ? 0 : 1;
                if (rightHandIK != null)
                {
                    rightHandIK.enabled = isHoldingItem && !isInCar;
                }
            }
            if (index == 7)
            {
                syncer.head = 0;
            }
        }

        private void TriggerIdleAction()
        {
            currentIdleAction = UnityEngine.Random.Range(1, idleActionsLengths.Length);
            if (animator != null)
            {
                animator.SetInteger(HashIdleAction, currentIdleAction);
            }
            StartCoroutine(C_WaitIdleAction(currentIdleAction));
            idleActionTimer = UnityEngine.Random.Range(10f, 30f);
        }

        private IEnumerator C_WaitIdleAction(int action)
        {
            float duration = (action < idleActionsLengths.Length) ? idleActionsLengths[action] : 3f;
            yield return new WaitForSeconds(duration);

            if (currentIdleAction == action)
            {
                ResetIdleAction();
            }
        }

        private void ResetIdleAction()
        {
            currentIdleAction = 0;
            if (animator != null)
            {
                animator.SetInteger(HashIdleAction, 0);
            }
        }

        public void PlaySwearAudio(byte swearId)
        {
            PlaySpokenAudio("Swearing", (swearId > 0 ? swearId : (byte)UnityEngine.Random.Range(1, 10)).ToString());
        }

        public void PlaySpokenAudio(string soundGroup, string soundName)
        {
            try
            {
                var mainCam = Camera.main;
                if (mainCam != null)
                {
                    float dist = Vector3.Distance(mainCam.transform.position, transform.position);
                    // На расстоянии более 10 метров ругательства и речь напарника не слышны!
                    if (dist > 10.0f) return;
                }

                var res = MasterAudio.PlaySound3DAtTransform(soundGroup, transform, 1f, null, 0f, soundName);
                if (res != null && res.ActingVariation != null && res.ActingVariation.VarAudio != null)
                {
                    var a = res.ActingVariation.VarAudio;
                    a.spatialBlend = 1.0f; // 100% 3D пространственное позиционирование
                    a.rolloffMode = AudioRolloffMode.Linear;
                    a.minDistance = 1.0f;
                    a.maxDistance = 10.0f; // Полное затухание до нуля на расстоянии 10 метров
                    a.dopplerLevel = 0f;
                }
            }
            catch
            {
                // Игнорируем, если банк звуков временно не загружен
            }
        }

        private void PlayAudioSafe(string soundGroup, string soundName)
        {
            try
            {
                MasterAudio.PlaySound3DAndForget(soundGroup, transform, true, 1f, null, 0f, soundName);
            }
            catch
            {
                // Игнорируем, если банк звуков временно не загружен
            }
        }
    }
}
