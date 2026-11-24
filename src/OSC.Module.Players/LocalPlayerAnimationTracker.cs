using System;
using System.Collections;
using HutongGames.PlayMaker;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Players
{
    public sealed class LocalPlayerAnimationTracker
    {
        public event Action<byte, bool, byte> OnGestureAction;
        public event Action<byte> OnSwearAction;
        public event Action<bool> OnCigaretteAction;

        public byte CurrentCrouchLevel { get; private set; }
        public bool IsPlayerInCar { get; set; }
        public bool IsSmoking { get; private set; }
        public bool IsLyingDown => isLyingDown;
        public bool IsLocalSeatedByBridge { get; set; }

        private bool isInitialized;
        private float nextSearchTime;
        private PlayMakerFSM crouchFsm;
        private FsmFloat playerHeightVar;
        private FsmBool playerInCarVar;
        private FsmInt middleFingerSwearTypeVar;
        private FsmInt speakDatabaseSwearVar;

        private float lastOneShotTime;
        private byte lastOneShotId = 255;
        private bool lastLiftState;
        private bool hasLiftWatcher;
        private bool hasPushWatcher;
        private bool isLyingDown;
        private float lastCtrlPressTime = -10f;

        private Transform cachedFpsCam;
        private CharacterController playerCc;

        public void Reset()
        {
            isInitialized = false;
            nextSearchTime = 0f;
            isLyingDown = false;
            lastCtrlPressTime = -10f;
            crouchFsm = null;
            playerHeightVar = null;
            playerInCarVar = null;
            middleFingerSwearTypeVar = null;
            speakDatabaseSwearVar = null;
            cachedFpsCam = null;
            playerCc = null;
            hasLiftWatcher = false;
            hasPushWatcher = false;
            IsSmoking = false;
            CurrentCrouchLevel = 0;
        }

        public void Tick(MonoBehaviour coroutineHost)
        {
            if (!isInitialized && Time.time >= nextSearchTime)
            {
                nextSearchTime = Time.time + 1.0f;
                TryInitialize();
            }

            UpdateLieDownInput();
            UpdateGestureKeybinds(coroutineHost);
            UpdateCrouchState();

            if (Input.GetMouseButtonDown(0) && !IsPlayerInCar && !Cursor.visible)
            {
                TriggerOneShot(6, 0);
            }
        }

        private void TriggerOneShot(byte idx, byte extra)
        {
            if (Time.time - lastOneShotTime < 0.35f && lastOneShotId == idx) return;
            lastOneShotTime = Time.time;
            lastOneShotId = idx;
            OnGestureAction?.Invoke(idx, true, extra);
        }

        private void UpdateLieDownInput()
        {
            if (IsPlayerInCar)
            {
                isLyingDown = false;
                return;
            }

            if (Input.GetKeyDown(KeyCode.LeftControl) || Input.GetKeyDown(KeyCode.RightControl))
            {
                float now = Time.time;
                if (now - lastCtrlPressTime < 0.50f)
                {
                    isLyingDown = !isLyingDown;
                    lastCtrlPressTime = -10f;
                }
                else
                {
                    lastCtrlPressTime = now;
                }
            }

            if (isLyingDown)
            {
                bool isMoving = (playerCc != null && playerCc.velocity.sqrMagnitude > 0.04f) ||
                                Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) ||
                                Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D) ||
                                Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.C);
                if (isMoving || (cachedFpsCam != null && cachedFpsCam.localPosition.y >= 1.10f))
                {
                    isLyingDown = false;
                }
            }
        }

        private void UpdateGestureKeybinds(MonoBehaviour host)
        {
            if (IsPlayerInCar) return;

            if (!hasLiftWatcher && Input.GetKeyDown(KeyCode.O))
            {
                lastLiftState = !lastLiftState;
                OnGestureAction?.Invoke(0, lastLiftState, 0);
            }
            if (Input.GetKeyDown(KeyCode.K)) TriggerOneShot(1, 0);
            if (Input.GetKeyDown(KeyCode.H)) TriggerOneShot(2, 0);
            if (!hasPushWatcher && Input.GetKeyDown(KeyCode.J) && host != null)
            {
                host.StartCoroutine(PushFallbackCoroutine());
            }
            if (Input.GetKeyDown(KeyCode.M))
            {
                byte swearType = (byte)(middleFingerSwearTypeVar != null ? middleFingerSwearTypeVar.Value : 0);
                TriggerOneShot(4, swearType);
            }
        }

        private IEnumerator PushFallbackCoroutine()
        {
            OnGestureAction?.Invoke(3, true, 0);
            yield return new WaitForSeconds(0.85f);
            OnGestureAction?.Invoke(3, false, 0);
        }

        private void TryInitialize()
        {
            GameObject player = GameObject.Find("PLAYER");
            if (player == null) return;

            playerCc = player.GetComponent<CharacterController>();

            crouchFsm = PlayMakerBridge.FindFsm(player, "Crouch");
            if (crouchFsm != null)
            {
                crouchFsm.InitializeSafe();
                playerHeightVar = crouchFsm.FsmVariables.FindFsmFloat("Position") ?? crouchFsm.FsmVariables.FindFsmFloat("PlayerHeight");
                playerInCarVar = crouchFsm.FsmVariables.FindFsmBool("PlayerInCar");
            }

            Transform fpsCamTr = player.transform.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera") ??
                                 player.transform.Find("Pivot/AnimPivot/Camera/FPSCamera");
            if (fpsCamTr == null)
            {
                Camera cam = player.GetComponentInChildren<Camera>();
                if (cam != null) fpsCamTr = cam.transform;
            }
            cachedFpsCam = fpsCamTr;

            if (fpsCamTr != null)
            {
                PlayMakerFSM pf = PlayMakerBridge.FindFsm(fpsCamTr.gameObject, "PlayerFunctions") ??
                                  (fpsCamTr.parent != null ? PlayMakerBridge.FindFsm(fpsCamTr.parent.gameObject, "PlayerFunctions") : null);
                if (pf != null)
                {
                    pf.InitializeSafe();
                    middleFingerSwearTypeVar = pf.FsmVariables.FindFsmInt("RandomInt");
                }
            }

            BindWatcher(fpsCamTr, player.transform, "Lift", active => { hasLiftWatcher = true; lastLiftState = active; OnGestureAction?.Invoke(0, active, 0); });
            BindWatcher(fpsCamTr, player.transform, "Hello", active => { if (active) TriggerOneShot(1, 0); });
            BindWatcher(fpsCamTr, player.transform, "Fist", active => { if (active) TriggerOneShot(2, 0); });
            BindWatcher(fpsCamTr, player.transform, "Hand Push", active => { hasPushWatcher = true; OnGestureAction?.Invoke(3, active, 0); });
            BindWatcher(fpsCamTr, player.transform, "MiddleFinger", active =>
            {
                if (active)
                {
                    byte swearType = (byte)(middleFingerSwearTypeVar != null ? middleFingerSwearTypeVar.Value : 0);
                    TriggerOneShot(4, swearType);
                }
            });

            Transform smokeTr = (fpsCamTr != null ? fpsCamTr.Find("Smoking") : null) ?? PlayMakerBridge.FindChildRecursive(player.transform, "Smoking");
            if (smokeTr != null)
            {
                var w = smokeTr.GetComponent<GameobjectToggleWatcher>() ?? smokeTr.gameObject.AddComponent<GameobjectToggleWatcher>();
                w.Toggled = active => { IsSmoking = active; OnCigaretteAction?.Invoke(active); };

                PlayMakerFSM smokeFsm = PlayMakerBridge.FindFsm(smokeTr.gameObject, "Start");
                if (smokeFsm != null)
                {
                    smokeFsm.InitializeSafe();
                    smokeFsm.InsertAction("Double", () => OnGestureAction?.Invoke(5, true, 0), -1, false);
                    smokeFsm.InsertAction("Anim 3", () => OnGestureAction?.Invoke(5, false, 0), -1, false);
                    smokeFsm.InsertAction("Put out 2", () => OnGestureAction?.Invoke(5, false, 0), -1, false);
                }
            }

            Transform speakDb = (fpsCamTr != null && fpsCamTr.parent != null ? fpsCamTr.parent.Find("SpeakDatabase") : null)
                ?? PlayMakerBridge.FindChildRecursive(player.transform, "SpeakDatabase");
            if (speakDb != null)
            {
                PlayMakerFSM speech = PlayMakerBridge.FindFsm(speakDb.gameObject, "Speech");
                if (speech != null)
                {
                    speech.InitializeSafe();
                    speakDatabaseSwearVar = speech.FsmVariables.FindFsmInt("RandomInt");
                    speech.InsertAction("Swear", () =>
                    {
                        byte id = (byte)(speakDatabaseSwearVar != null ? speakDatabaseSwearVar.Value : 0);
                        OnSwearAction?.Invoke(id);
                    }, -1, false);
                }
            }

            isInitialized = true;
        }

        private static void BindWatcher(Transform fpsCam, Transform root, string name, Action<bool> callback)
        {
            Transform tr = (fpsCam != null ? fpsCam.Find(name) : null) ?? PlayMakerBridge.FindChildRecursive(root, name);
            if (tr == null) return;
            var watcher = tr.GetComponent<GameobjectToggleWatcher>() ?? tr.gameObject.AddComponent<GameobjectToggleWatcher>();
            watcher.Toggled = callback;
        }

        private void UpdateCrouchState()
        {
            bool inCar = (playerInCarVar != null && playerInCarVar.Value) || IsLocalSeatedByBridge;
            IsPlayerInCar = inCar;

            if (inCar)
            {
                CurrentCrouchLevel = 2;
                return;
            }

            if (cachedFpsCam != null && cachedFpsCam.localPosition.y >= 1.15f)
            {
                isLyingDown = false;
                CurrentCrouchLevel = 0;
                return;
            }

            if (isLyingDown)
            {
                CurrentCrouchLevel = 2;
                return;
            }

            byte level = 0;
            if (crouchFsm != null)
            {
                string state = crouchFsm.ActiveStateName;
                if (state == "Wait key") level = 0;
                else if (state == "Crouched 2" || state == "Move down 2") level = 2;
                else if (state == "Crouched 1" || state == "Move down 1" || state == "Crouch" || state == "Move down") level = 1;
                else if (cachedFpsCam != null)
                {
                    float y = cachedFpsCam.localPosition.y;
                    level = y >= 1.15f ? (byte)0 : (y >= 0.55f ? (byte)1 : (byte)2);
                }
                else if (playerHeightVar != null)
                {
                    float h = playerHeightVar.Value;
                    level = h >= 1.0f ? (byte)0 : (h >= 0.45f ? (byte)1 : (byte)2);
                }
            }
            CurrentCrouchLevel = level;
        }
    }
}
