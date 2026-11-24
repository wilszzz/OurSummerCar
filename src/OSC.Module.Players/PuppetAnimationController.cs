using System;
using System.Collections;
using UnityEngine;

namespace OSC.Module.Players
{
    public sealed class PuppetAnimationController : MonoBehaviour
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

        private static readonly string[] gesture_state_names = { "Lift", "Hello", "Fist", "HandPush", "MiddleFinger", "Smoke", "click", "Drink" };
        private static readonly int[] gesture_hashes =
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
        private static readonly bool[] gesture_onetime = { false, true, true, false, true, false, true, true };
        private static readonly bool[] gesture_lefthand = { false, false, false, true, true, false, false, true };
        private static readonly bool[] gesture_righthand = { true, true, true, true, true, true, true, false };
        private static readonly float[] idleActionsLengths = { 0f, 2.5f, 2.25f, 4f, 3.25f, 1.7f, 3.1f, 4f, 0f, 2f, 6f };

        private static readonly int HashWalking = Animator.StringToHash("walking");
        private static readonly int HashRunning = Animator.StringToHash("running");
        private static readonly int HashCrouch = Animator.StringToHash("crouch");
        private static readonly int HashPassenger = Animator.StringToHash("passenger");
        private static readonly int HashIdleAction = Animator.StringToHash("idleAction");
        private static readonly int HashSmoke = Animator.StringToHash("smoke");

        public int CurrentGesture => currentGesture;
        public float CurrentTorsoPitch => currentTorsoPitch;
        public Action OnLateUpdateAction;

        private int currentGesture = -1;
        private float gestureStartTime;
        private Coroutine currentGestureCoroutine;

        private byte currentCrouchLevel;
        private float targetCharY = -0.38f;
        private float currentCharY = -0.38f;

        private bool isInCar;
        private bool wasPassenger;
        private bool wasAirborne;
        private bool isHoldingItem;
        private bool isSmoking;
        private Coroutine stopSmokingCoroutine;
        private bool isLeaningForward;
        private float currentLeanRoll;
        private float currentTorsoPitch;
        private float idleActionTimer = 15f;
        private int currentIdleAction;

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
            if (!isInCar && rotationBendPivot != null)
            {
                float rollX = isLeaningForward ? 0f : -currentLeanRoll;
                rotationBendPivot.localEulerAngles = new Vector3(rollX, 0f, -currentTorsoPitch);
            }
            OnLateUpdateAction?.Invoke();
        }

        public void UpdateLocomotion(float speed, byte crouchLevel, bool inCar, bool usePassengerPose, bool holdingItem, bool isSleeping = false, bool isAirborne = false)
        {
            isInCar = inCar;
            isHoldingItem = holdingItem;
            SetCrouchLevel(crouchLevel);

            if (animator == null) return;

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
                if (currentIdleAction != 0) ResetIdleAction();
            }
            else if (!inCar && syncer != null && syncer.lockStraightLegs)
            {
                syncer.lockStraightLegs = false;
                syncer.SetLegChannels(1, 1);
            }

            if (inCar && usePassengerPose)
            {
                animator.SetBool(HashPassenger, true);
                animator.SetBool(HashWalking, false);
                animator.SetBool(HashRunning, false);
                animator.SetInteger(HashCrouch, 0);
                if (!wasPassenger && !animator.GetCurrentAnimatorStateInfo(0).IsName("PassengerMode"))
                {
                    animator.Play("PassengerMode");
                }
                if (syncer != null)
                {
                    syncer.lockStraightLegs = false;
                    syncer.SetLegChannels(1, 1);
                    if (currentGesture == -1 && !isSmoking) syncer.ResetChannels();
                }
                wasPassenger = true;
                wasAirborne = false;
                return;
            }

            if (inCar)
            {
                animator.SetBool(HashPassenger, false);
                animator.SetBool(HashWalking, false);
                animator.SetBool(HashRunning, false);
                animator.SetInteger(HashCrouch, 0);
                if (rotationBendPivot != null) rotationBendPivot.localEulerAngles = Vector3.forward * -16f;
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

            wasPassenger = false;
            animator.SetBool(HashPassenger, false);

            bool isWalking = speed > 0.15f;
            bool isRunning = speed > 3.5f;

            animator.SetBool(HashWalking, isWalking);
            animator.SetBool(HashRunning, isRunning);
            animator.SetInteger(HashCrouch, crouchLevel);

            if (isAirborne && !wasAirborne && !isSleeping)
            {
                animator.CrossFade("jump", 0.08f);
            }
            else if (!isAirborne && !animator.IsInTransition(0))
            {
                AnimatorStateInfo curState = animator.GetCurrentAnimatorStateInfo(0);
                bool exitingJump = curState.IsName("jump") || wasAirborne;

                if (crouchLevel == 2)
                {
                    if (isWalking || isRunning)
                    {
                        if (exitingJump || (!curState.IsName("crouch_floor_walk") && !curState.IsName("crouch_floor_walk3")))
                            animator.CrossFade("crouch_floor_walk", exitingJump ? 0.12f : 0.2f);
                    }
                    else if (exitingJump || (!curState.IsName("crouch_floor_idle") && !curState.IsName("crouch_floor")))
                    {
                        animator.CrossFade("crouch_floor_idle", exitingJump ? 0.12f : 0.2f);
                    }
                }
                else if (crouchLevel == 1)
                {
                    if (isWalking || isRunning)
                    {
                        if (exitingJump || !curState.IsName("crouch_walk"))
                            animator.CrossFade("crouch_walk", exitingJump ? 0.12f : 0.2f);
                    }
                    else if (exitingJump || (!curState.IsName("crouch_idle") && !curState.IsName("crouch_idleH") && !curState.IsName("crouch")))
                    {
                        animator.CrossFade("crouch_idle", exitingJump ? 0.12f : 0.2f);
                    }
                }
                else
                {
                    bool stuckInCrouch = curState.IsName("crouch_idle") || curState.IsName("crouch_idleH") || curState.IsName("crouch") ||
                                         curState.IsName("crouch_walk") || curState.IsName("crouch_floor_idle") || curState.IsName("crouch_floor") ||
                                         curState.IsName("crouch_floor_walk") || curState.IsName("crouch_floor_walk3");

                    if (exitingJump)
                    {
                        animator.CrossFade(isWalking || isRunning ? (isRunning ? "sprint" : "walk") : "breath", 0.12f);
                    }
                    else if (stuckInCrouch)
                    {
                        animator.CrossFade(isWalking || isRunning ? (isRunning ? "sprint" : "walk") : "crouch_standup", 0.2f);
                    }
                }
            }

            wasAirborne = isAirborne && !isSleeping;

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
                if (currentIdleAction != 0) ResetIdleAction();
                idleActionTimer = UnityEngine.Random.Range(8f, 25f);
            }
        }

        public void SetPitchAndLean(float headPitch, float leanRoll, bool leaningForward)
        {
            isLeaningForward = leaningForward;
            currentLeanRoll = leaningForward ? 0f : Mathf.Lerp(currentLeanRoll, leanRoll, Time.deltaTime * 10f);

            if (!isInCar)
            {
                float baseTorso = leaningForward ? 45f : Mathf.Clamp(headPitch * 0.15f, -5f, 15f);
                currentTorsoPitch = Mathf.Lerp(currentTorsoPitch, baseTorso, Time.deltaTime * 10f);

                if (rotationBendPivot != null)
                {
                    float rollX = leaningForward ? 0f : -currentLeanRoll;
                    rotationBendPivot.localEulerAngles = new Vector3(rollX, 0f, -currentTorsoPitch);
                }

                if (headPivot != null && (syncer == null || syncer.head == 0))
                {
                    float headRem = headPitch - (leaningForward ? (currentTorsoPitch * 0.4f) : currentTorsoPitch);
                    headPivot.localEulerAngles = Vector3.forward * -headRem;
                }
            }
            else
            {
                if (rotationBendPivot != null)
                {
                    rotationBendPivot.localEulerAngles = new Vector3(-currentLeanRoll, 0f, -16f);
                }

                if (headPivot != null && (syncer == null || syncer.head == 0))
                {
                    float rootYaw = (charTransform != null && charTransform.parent != null)
                        ? charTransform.parent.eulerAngles.y
                        : (transform.root != null ? transform.root.eulerAngles.y : 0f);
                    float relYaw = Mathf.Clamp(Mathf.DeltaAngle(rootYaw, targetHeadYaw), -95f, 95f);
                    headPivot.eulerAngles = new Vector3(0f, rootYaw + relYaw - 90f, -headPitch);
                }
            }
        }

        public void SetCrouchLevel(byte level)
        {
            if (currentCrouchLevel == level) return;
            currentCrouchLevel = level;
            targetCharY = (level == 0) ? -0.38f : ((level == 1) ? -0.88f : -1.08f);

            if (animator != null && animator.isActiveAndEnabled)
            {
                if (level == 2) animator.CrossFade("crouch_floor_idle", 0.2f);
                else if (level == 1) animator.CrossFade("crouch_idle", 0.2f);
                else animator.CrossFade("crouch_standup", 0.2f);
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
                if (cigaretteObject != null) cigaretteObject.SetActive(true);
                if (animatorGestures != null) animatorGestures.SetBool(HashSmoke, true);
                if (syncer != null && currentGesture == -1) syncer.rightArm = 2;
            }
            else
            {
                stopSmokingCoroutine = StartCoroutine(C_StopSmokingSmooth());
            }
        }

        private IEnumerator C_StopSmokingSmooth()
        {
            if (animatorGestures != null) animatorGestures.SetBool(HashSmoke, false);
            yield return new WaitForSeconds(0.45f);
            if (cigaretteObject != null) cigaretteObject.SetActive(false);
            if (syncer != null && currentGesture == -1 && !isSmoking)
            {
                syncer.rightArm = (isHoldingItem || isInCar) ? 0 : 1;
            }
            stopSmokingCoroutine = null;
        }

        public void TriggerGesture(int index, byte extraParam = 0)
        {
            if (index < 0 || index >= gesture_hashes.Length) return;
            if (currentGesture == index && !gesture_onetime[index]) return;
            if (currentIdleAction != 0) ResetIdleAction();

            if (currentGestureCoroutine != null) StopCoroutine(currentGestureCoroutine);
            currentGestureCoroutine = StartCoroutine(C_TriggerGesture(index, extraParam));
        }

        public void StopGesture(int index = -1)
        {
            int g = (index >= 0) ? index : currentGesture;
            if (g < 0 || g >= gesture_hashes.Length || gesture_onetime[g] || currentGesture != g) return;

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
                if (isOneTime) animatorGestures.SetTrigger(hash);
                else animatorGestures.SetBool(hash, true);

                if (!string.IsNullOrEmpty(gesture_state_names[index]))
                {
                    animatorGestures.Play(gesture_state_names[index], 0, 0f);
                }
            }

            if (syncer != null)
            {
                if (gesture_lefthand[index]) syncer.leftArm = 2;
                if (gesture_righthand[index]) syncer.rightArm = 2;
                if (index == 7) syncer.head = 2;
            }

            if (index == 7 && beerBottleObject != null) beerBottleObject.SetActive(true);

            if (index == 4 && extraParam > 0) PlaySwearAudio(extraParam);

            if (isOneTime)
            {
                yield return new WaitForSeconds(index == 7 ? 1.85f : 0.85f);
                if (index == 7 && beerBottleObject != null) beerBottleObject.SetActive(false);
                RestoreSyncerChannels(index);
                currentGesture = -1;
            }

            currentGestureCoroutine = null;
        }

        private IEnumerator C_StopGesture(int index)
        {
            if (index == 7 && beerBottleObject != null) beerBottleObject.SetActive(false);

            float minHold = (index == 3) ? 0.70f : 0.40f;
            float elapsed = Time.time - gestureStartTime;
            if (elapsed < minHold) yield return new WaitForSeconds(minHold - elapsed);

            if (animatorGestures != null)
            {
                animatorGestures.SetBool(gesture_hashes[index], false);
                if (index == 3) animatorGestures.Play("HandPushOver", 0, 0f);
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
            if (gesture_lefthand[index]) syncer.leftArm = isInCar ? 0 : 1;
            if (gesture_righthand[index])
            {
                syncer.rightArm = (isHoldingItem || isInCar) ? 0 : 1;
                if (rightHandIK != null) rightHandIK.enabled = isHoldingItem && !isInCar;
            }
            if (index == 7) syncer.head = 0;
        }

        private void TriggerIdleAction()
        {
            currentIdleAction = UnityEngine.Random.Range(1, idleActionsLengths.Length);
            if (animator != null) animator.SetInteger(HashIdleAction, currentIdleAction);
            StartCoroutine(C_WaitIdleAction(currentIdleAction));
            idleActionTimer = UnityEngine.Random.Range(10f, 30f);
        }

        private IEnumerator C_WaitIdleAction(int action)
        {
            float duration = (action < idleActionsLengths.Length) ? idleActionsLengths[action] : 3f;
            yield return new WaitForSeconds(duration);
            if (currentIdleAction == action) ResetIdleAction();
        }

        private void ResetIdleAction()
        {
            currentIdleAction = 0;
            if (animator != null) animator.SetInteger(HashIdleAction, 0);
        }

        public void PlaySwearAudio(byte swearId)
        {
            PlaySpokenAudio("Swearing", (swearId > 0 ? swearId : (byte)UnityEngine.Random.Range(1, 10)).ToString());
        }

        private void PlaySpokenAudio(string soundGroup, string soundName)
        {
            try
            {
                Camera cam = Camera.main;
                if (cam != null && Vector3.Distance(cam.transform.position, transform.position) > 10.0f) return;

                var res = MasterAudio.PlaySound3DAtTransform(soundGroup, transform, 1f, null, 0f, soundName);
                if (res?.ActingVariation?.VarAudio != null)
                {
                    var a = res.ActingVariation.VarAudio;
                    a.spatialBlend = 1.0f;
                    a.rolloffMode = AudioRolloffMode.Linear;
                    a.minDistance = 1.0f;
                    a.maxDistance = 10.0f;
                    a.dopplerLevel = 0f;
                }
            }
            catch { }
        }
    }
}
