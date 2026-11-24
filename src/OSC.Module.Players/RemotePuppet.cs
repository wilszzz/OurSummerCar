using System;
using System.Collections.Generic;
using OSC.Core.Bridges;
using UnityEngine;

namespace OSC.Module.Players
{
    public sealed class RemotePuppet
    {
        public int Id { get; }
        public GameObject RootObject { get; private set; }
        public Transform ItemHolder { get; private set; }
        public GameObject CurrentHeldItem { get; private set; }
        public string Nickname { get; private set; }

        private GameObject characterModel;
        private GameObject fallbackHead;
        private Animator animator;
        private Animator animatorGestures;
        private AnimatorSyncer animSyncer;
        private PuppetAnimationController animController;
        private FastIKFabric rightHandIK;
        private FastIKFabric leftHandIK;

        private Transform headPivot;
        private Transform handRightBone;
        private Transform handLeftBone;
        private Transform shoulderRightBone;
        private Transform shoulderLeftBone;
        private Transform spineUpperBone;
        private GameObject cigaretteObject;
        private GameObject beerBottleObject;
        private GameObject peeStreamObject;
        private ParticleSystem peeParticleSystem;

        private Vector3 targetPosition;
        private Quaternion targetRotation;
        private float targetHeadPitch;
        private float targetHeadYaw;
        private float targetLeanAngle;
        private bool isLeaning;
        private Vector3 lastPosition;
        private float smoothedSpeed;
        private byte lastAnimState;
        private byte lastCrouchLevel;
        private bool lastInCar;
        private bool lastSleepingInBed;
        private bool isAirborne;
        private bool isPeeing;

        // Вся посадка в транспорт управляется исключительно мостом от модулей транспорта
        private bool bridgeSeated;
        private bool bridgeIsDriver;
        private bool bridgeUsePassengerAnim;
        private Transform bridgeVehicleTransform;
        private Transform bridgeSteeringFork;
        private Vector3 bridgeSeatLocalPos;
        private Quaternion bridgeSeatLocalRot = Quaternion.identity;

        private static Camera cachedMainCam;
        private static Texture2D cachedPeeCircleTex;

        private GameObject nametagObject;
        private TextMesh nametagMesh;
        private TextMesh shadowMesh;
        private Color baseNametagColor;
        private readonly List<Collider> disabledColliders = new List<Collider>();
        private readonly Dictionary<GameObject, Vector3> itemOriginalScales = new Dictionary<GameObject, Vector3>();

        public RemotePuppet(int id, Vector3 initialPosition, Quaternion initialRotation, Action<string> log = null)
        {
            Id = id;
            targetPosition = initialPosition;
            lastPosition = initialPosition;
            targetRotation = initialRotation;
            CreateModel(initialPosition, initialRotation, log);
        }

        private void CreateModel(Vector3 pos, Quaternion rot, Action<string> log)
        {
            RootObject = new GameObject(Id == 0 ? "RemotePuppet_Host" : $"RemotePuppet_Client_{Id}");
            RootObject.transform.position = pos;
            RootObject.transform.rotation = rot;
            RootObject.layer = 0;

            var holderObj = new GameObject("ItemHolder");
            holderObj.transform.SetParent(RootObject.transform, false);
            holderObj.transform.localPosition = new Vector3(0.18f, 1.15f, 0.4f);
            holderObj.transform.localRotation = Quaternion.identity;
            holderObj.transform.localScale = Vector3.one;
            ItemHolder = holderObj.transform;

            characterModel = CharacterAssetLoader.InstantiateCharacter(Id, RootObject.transform, log);
            if (characterModel != null) SetupCharacter();
            else SetupFallback();

            baseNametagColor = (Id == 0) ? new Color(0.3f, 1f, 0.45f) : new Color(1f, 0.92f, 0.25f);
            Nickname = (Id == 0) ? "ХОСТ" : $"Игрок #{Id}";

            nametagObject = new GameObject("Nametag");
            nametagObject.transform.SetParent(RootObject.transform, false);
            nametagObject.transform.localPosition = new Vector3(0f, 2.15f, 0f);

            nametagMesh = nametagObject.AddComponent<TextMesh>();
            nametagMesh.text = Nickname;
            nametagMesh.fontSize = 32;
            nametagMesh.characterSize = 0.042f;
            nametagMesh.alignment = TextAlignment.Center;
            nametagMesh.anchor = TextAnchor.MiddleCenter;
            nametagMesh.color = baseNametagColor;

            var shadowObj = new GameObject("Shadow");
            shadowObj.transform.SetParent(nametagObject.transform, false);
            shadowObj.transform.localPosition = new Vector3(0.012f, -0.012f, 0.005f);
            shadowMesh = shadowObj.AddComponent<TextMesh>();
            shadowMesh.text = Nickname;
            shadowMesh.fontSize = 32;
            shadowMesh.characterSize = 0.042f;
            shadowMesh.alignment = TextAlignment.Center;
            shadowMesh.anchor = TextAnchor.MiddleCenter;
            shadowMesh.color = new Color(0f, 0f, 0f, 0.85f);
        }

        private void SetupCharacter()
        {
            var colliders = characterModel.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) UnityEngine.Object.Destroy(colliders[i]);

            Transform animLoc = characterModel.transform.Find("char/skeleton ANIMATOR");
            Transform animGest = characterModel.transform.Find("char/skeleton ANIMATOR gestures");
            Transform skel = characterModel.transform.Find("char/skeleton");

            if (animLoc != null) animator = animLoc.GetComponent<Animator>();
            if (animGest != null) animatorGestures = animGest.GetComponent<Animator>();

            if (skel != null && animLoc != null)
            {
                animSyncer = skel.gameObject.AddComponent<AnimatorSyncer>();
                animSyncer.sourceSkeleton = animLoc;
                animSyncer.sourceSkeleton2 = animGest;
                animSyncer.InitBones();
            }

            headPivot = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/headPivot/HeadRotationPivot");
            spineUpperBone = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper");
            shoulderRightBone = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/collar_right/shoulder_right");
            shoulderLeftBone = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/collar_left/shoulder_left");
            handRightBone = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/collar_right/shoulder_right/arm_right/hand_right");
            handLeftBone = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/collar_left/shoulder_left/arm_left/hand_left");

            Transform fingersR = handRightBone?.Find("fingers_right");
            if (fingersR != null && skel != null)
            {
                Transform raHint = skel.Find("ra_hint");
                if (raHint == null)
                {
                    var h = new GameObject("ra_hint");
                    h.transform.SetParent(shoulderRightBone ?? skel, false);
                    h.transform.localPosition = shoulderRightBone != null ? new Vector3(0.15f, -0.1f, -0.35f) : new Vector3(0.35f, 1.15f, -0.3f);
                    raHint = h.transform;
                }
                rightHandIK = FastIKFabric.CreateInstance(fingersR, 3, raHint);
                if (rightHandIK != null) rightHandIK.enabled = false;

                Transform cigTr = fingersR.Find("cigarette_shaft_0");
                if (cigTr != null)
                {
                    cigaretteObject = cigTr.gameObject;
                    cigaretteObject.SetActive(false);
                }
            }

            Transform fingersL = handLeftBone != null ? (handLeftBone.Find("finger_left") ?? handLeftBone.Find("fingers_left")) : null;
            if (fingersL != null && skel != null)
            {
                Transform laHint = skel.Find("la_hint");
                if (laHint == null)
                {
                    var h = new GameObject("la_hint");
                    h.transform.SetParent(shoulderLeftBone ?? skel, false);
                    h.transform.localPosition = shoulderLeftBone != null ? new Vector3(-0.15f, -0.1f, -0.35f) : new Vector3(-0.35f, 1.15f, -0.3f);
                    laHint = h.transform;
                }
                leftHandIK = FastIKFabric.CreateInstance(fingersL, 3, laHint);
                if (leftHandIK != null) leftHandIK.enabled = false;
            }

            Transform nativeBottle = handLeftBone?.Find("beer_bottle");
            if (nativeBottle != null)
            {
                beerBottleObject = nativeBottle.gameObject;
                beerBottleObject.SetActive(false);
            }

            if (skel != null)
            {
                animController = skel.gameObject.AddComponent<PuppetAnimationController>();
                animController.animator = animator;
                animController.animatorGestures = animatorGestures;
                animController.syncer = animSyncer;
                animController.headPivot = headPivot;
                animController.rotationBendPivot = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot");
                animController.charTransform = characterModel.transform.Find("char");
                if (animController.charTransform != null)
                {
                    Vector3 lp = animController.charTransform.localPosition;
                    animController.charTransform.localPosition = new Vector3(lp.x, -0.38f, lp.z);
                }
                animController.cigaretteObject = cigaretteObject;
                animController.beerBottleObject = beerBottleObject;
                animController.rightHandIK = rightHandIK;
                animController.OnLateUpdateAction = UpdateHeldItemPosition;
            }
        }

        private void SetupFallback()
        {
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.transform.SetParent(RootObject.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.27f, 0f);
            body.transform.localScale = new Vector3(0.5f, 0.65f, 0.5f);
            UnityEngine.Object.Destroy(body.GetComponent<Collider>());

            fallbackHead = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            fallbackHead.transform.SetParent(RootObject.transform, false);
            fallbackHead.transform.localPosition = new Vector3(0f, 1.02f, 0f);
            fallbackHead.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
            UnityEngine.Object.Destroy(fallbackHead.GetComponent<Collider>());
        }

        public void ApplySeatEvent(RemotePlayerSeatEvent evt)
        {
            bridgeSeated = evt.IsSeated && evt.VehicleTransform != null;
            bridgeIsDriver = evt.IsDriver;
            bridgeUsePassengerAnim = evt.UsePassengerAnimation;
            bridgeVehicleTransform = evt.VehicleTransform;
            bridgeSteeringFork = evt.SteeringForkTransform;
            bridgeSeatLocalPos = evt.LocalSeatPosition;
            bridgeSeatLocalRot = evt.LocalSeatRotation;

            if (!bridgeSeated && RootObject != null && RootObject.transform.parent != null)
            {
                RootObject.transform.SetParent(null, true);
                if (leftHandIK != null) leftHandIK.enabled = false;
                if (rightHandIK != null && CurrentHeldItem == null) rightHandIK.enabled = false;
            }
        }

        public void UpdateTarget(Vector3 pos, float rotY, float headPitch, byte animState, float leanAngle, bool airborne)
        {
            isAirborne = airborne;
            if (!airborne && Mathf.Abs(pos.y - targetPosition.y) < 0.04f)
            {
                pos.y = Mathf.Lerp(targetPosition.y, pos.y, 0.25f);
            }

            targetPosition = pos;
            targetHeadYaw = rotY;
            targetRotation = Quaternion.Euler(0f, rotY, 0f);
            targetHeadPitch = headPitch;
            targetLeanAngle = leanAngle;
            isLeaning = (animState & 64) != 0 || leanAngle >= 30f;

            byte prevAnimState = lastAnimState;
            lastAnimState = animState;

            animController?.SetSmoking((animState & 1) != 0);

            bool netPeeing = (animState & 8) != 0;
            if (netPeeing != isPeeing) SetPeeing(netPeeing);

            bool isDrinking = (animState & 16) != 0;
            if (isDrinking && (prevAnimState & 16) == 0) TriggerGesture(7, 0);
        }

        public void TriggerGesture(int idx, byte extra) => animController?.TriggerGesture(idx, extra);
        public void StopGesture(int idx) => animController?.StopGesture(idx);
        public void PlaySwear(byte swearId) => animController?.PlaySwearAudio(swearId);

        public void UpdateInterpolation(float deltaTime)
        {
            if (RootObject == null) return;

            byte crouchLevel = (byte)((lastAnimState & 32) != 0 ? 2 : ((lastAnimState & 2) != 0 ? 1 : 0));
            bool isSleeping = (lastAnimState & 128) != 0;
            bool inVehicle = bridgeSeated && bridgeVehicleTransform != null;

            if (inVehicle)
            {
                if (RootObject.transform.parent != bridgeVehicleTransform)
                {
                    RootObject.transform.SetParent(bridgeVehicleTransform, false);
                }
                RootObject.transform.localPosition = bridgeSeatLocalPos;
                RootObject.transform.localRotation = bridgeSeatLocalRot;

                if (bridgeIsDriver && bridgeSteeringFork != null)
                {
                    UpdateForkSteeringIK();
                }
                else
                {
                    if (leftHandIK != null && leftHandIK.enabled) leftHandIK.enabled = false;
                    if (rightHandIK != null && rightHandIK.enabled && CurrentHeldItem == null) rightHandIK.enabled = false;
                }

                smoothedSpeed = 0f;
                lastPosition = RootObject.transform.position;
            }
            else
            {
                if (RootObject.transform.parent != null)
                {
                    RootObject.transform.SetParent(null, true);
                    if (leftHandIK != null) leftHandIK.enabled = false;
                    if (rightHandIK != null && CurrentHeldItem == null) rightHandIK.enabled = false;
                }

                float distSq = (RootObject.transform.position - targetPosition).sqrMagnitude;
                Vector3 newPos = distSq > 16f ? targetPosition : Vector3.Lerp(RootObject.transform.position, targetPosition, deltaTime * 12f);
                Quaternion newRot = distSq > 16f ? targetRotation : Quaternion.Slerp(RootObject.transform.rotation, targetRotation, deltaTime * 12f);

                if (!isSleeping && !isAirborne)
                {
                    int groundMask = ~((1 << 20) | (1 << 23));
                    if (Physics.Raycast(newPos + Vector3.up * 0.75f, Vector3.down, out RaycastHit hit, 2.0f, groundMask))
                    {
                        float d = targetPosition.y - hit.point.y;
                        if (d >= 0.05f && d <= 1.2f)
                        {
                            newPos.y = hit.point.y + 0.38f;
                        }
                    }
                }

                Vector3 horiz = new Vector3(newPos.x - lastPosition.x, 0f, newPos.z - lastPosition.z);
                float instantSpeed = horiz.magnitude / Mathf.Max(deltaTime, 0.001f);
                lastPosition = newPos;

                smoothedSpeed = Mathf.Lerp(smoothedSpeed, instantSpeed, deltaTime * 8f);
                if (smoothedSpeed < 0.1f) smoothedSpeed = 0f;

                RootObject.transform.position = newPos;
                RootObject.transform.rotation = newRot;
            }

            if (characterModel != null)
            {
                if (isSleeping)
                {
                    characterModel.transform.localPosition = Vector3.Lerp(characterModel.transform.localPosition, new Vector3(0f, 0.12f, 1.45f), deltaTime * 6f);
                    characterModel.transform.localRotation = Quaternion.Slerp(characterModel.transform.localRotation, Quaternion.Euler(-90f, 0f, 0f), deltaTime * 6f);
                }
                else
                {
                    characterModel.transform.localPosition = Vector3.zero;
                    characterModel.transform.localRotation = Quaternion.identity;
                }
            }

            if (animController != null)
            {
                byte animCrouch = (isSleeping || inVehicle) ? (byte)0 : crouchLevel;
                animController.targetHeadYaw = targetHeadYaw;
                animController.UpdateLocomotion(smoothedSpeed, animCrouch, inVehicle, bridgeUsePassengerAnim, CurrentHeldItem != null, isSleeping, isAirborne);
                animController.SetPitchAndLean(isSleeping ? 0f : targetHeadPitch, isSleeping ? 0f : targetLeanAngle, !isSleeping && isLeaning);
            }
            else if (fallbackHead != null)
            {
                fallbackHead.transform.localRotation = isSleeping ? Quaternion.Euler(-90f, 0f, 0f) : Quaternion.Euler(targetHeadPitch, 0f, -targetLeanAngle);
            }

            lastCrouchLevel = crouchLevel;
            lastInCar = inVehicle;
            lastSleepingInBed = isSleeping;

            UpdateHeldItemPosition();
            if (isPeeing && !inVehicle) UpdatePeeStream();
            UpdateNametag();
        }

        private void UpdateForkSteeringIK()
        {
            if (bridgeSteeringFork == null) return;
            Transform pivot = bridgeSteeringFork.Find("SteeringPivot") ?? bridgeSteeringFork;
            Transform col = pivot.Find("Column");
            Transform thr = pivot.Find("Throttle");

            if (leftHandIK?.Target != null)
            {
                if (col != null)
                {
                    leftHandIK.Target.position = col.TransformPoint(new Vector3(0.03f, -0.3f, 0.41f));
                    leftHandIK.Target.rotation = col.rotation * Quaternion.Euler(0f, 320f, 80f);
                }
                else
                {
                    leftHandIK.Target.position = bridgeSteeringFork.TransformPoint(new Vector3(-0.25f, 0.38f, 0.05f));
                    leftHandIK.Target.rotation = bridgeSteeringFork.rotation;
                }
                leftHandIK.enabled = true;
            }
            if (rightHandIK?.Target != null && CurrentHeldItem == null)
            {
                if (thr != null)
                {
                    rightHandIK.Target.position = thr.TransformPoint(new Vector3(0.07f, 0.02f, 0f));
                    rightHandIK.Target.rotation = thr.rotation * Quaternion.Euler(0f, 10f, 340f);
                }
                else
                {
                    rightHandIK.Target.position = bridgeSteeringFork.TransformPoint(new Vector3(0.25f, 0.38f, 0.05f));
                    rightHandIK.Target.rotation = bridgeSteeringFork.rotation;
                }
                rightHandIK.enabled = true;
            }
        }

        public void SetNickname(string name)
        {
            Nickname = string.IsNullOrEmpty(name) ? (Id == 0 ? "ХОСТ" : $"Игрок #{Id}") : name;
            if (nametagMesh != null) nametagMesh.text = Nickname;
            if (shadowMesh != null) shadowMesh.text = Nickname;
        }

        private void UpdateNametag()
        {
            if (nametagObject == null) return;
            if (headPivot != null) nametagObject.transform.position = headPivot.position + new Vector3(0f, 0.38f, 0f);
            else if (fallbackHead != null) nametagObject.transform.position = fallbackHead.transform.position + new Vector3(0f, 0.38f, 0f);
            else nametagObject.transform.position = RootObject.transform.position + new Vector3(0f, 2.15f, 0f);

            if (cachedMainCam == null || !cachedMainCam.isActiveAndEnabled) cachedMainCam = Camera.main;
            if (cachedMainCam == null) return;

            float distSq = (cachedMainCam.transform.position - nametagObject.transform.position).sqrMagnitude;
            if (distSq <= 100f)
            {
                if (!nametagObject.activeSelf) nametagObject.SetActive(true);
                nametagObject.transform.rotation = cachedMainCam.transform.rotation;
                float alpha = distSq > 64f ? Mathf.Clamp01((10f - Mathf.Sqrt(distSq)) / 2f) : 1f;
                Color c = baseNametagColor;
                c.a = alpha;
                if (nametagMesh != null) nametagMesh.color = c;
                if (shadowMesh != null) shadowMesh.color = new Color(0f, 0f, 0f, 0.85f * alpha);
            }
            else if (nametagObject.activeSelf)
            {
                nametagObject.SetActive(false);
            }
        }

        public void UpdateHeldItemPosition()
        {
            if (ItemHolder == null || RootObject == null) return;
            bool isGesture = animController != null && animController.CurrentGesture != -1;
            bool shouldIK = CurrentHeldItem != null && !lastInCar && !isPeeing && !isGesture && !lastSleepingInBed;

            float torsoPitch = animController != null ? animController.CurrentTorsoPitch : (targetHeadPitch * 0.35f);
            Quaternion chestRot = RootObject.transform.rotation * Quaternion.Euler(torsoPitch, 0f, -targetLeanAngle);
            Vector3 origin = shoulderRightBone != null ? shoulderRightBone.position :
                             (spineUpperBone != null ? spineUpperBone.position + (chestRot * Vector3.right) * 0.18f :
                             RootObject.transform.position + RootObject.transform.up * 1.2f);

            ItemHolder.position = origin + (chestRot * Vector3.forward) * 0.32f - (chestRot * Vector3.right) * 0.04f - (chestRot * Vector3.up) * 0.12f;
            ItemHolder.rotation = chestRot;
            ItemHolder.localScale = Vector3.one;

            if (rightHandIK != null)
            {
                if (rightHandIK.enabled != shouldIK) rightHandIK.enabled = shouldIK;
                if (shouldIK)
                {
                    rightHandIK.Target.position = ItemHolder.position;
                    rightHandIK.Target.rotation = chestRot * Quaternion.Euler(0f, -25f, 15f);
                }
            }
        }

        public void HoldItem(GameObject item)
        {
            if (item == null) return;
            if (CurrentHeldItem != null && CurrentHeldItem != item) DropItem(CurrentHeldItem);

            CurrentHeldItem = item;
            disabledColliders.Clear();
            Vector3 origScale = item.transform.localScale;
            if (origScale.sqrMagnitude > 3.5f || origScale.sqrMagnitude < 0.2f) origScale = Vector3.one;
            itemOriginalScales[item] = origScale;

            var rb = item.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            if (ItemHolder != null)
            {
                item.transform.SetParent(ItemHolder, false);
                item.transform.localPosition = item.name.StartsWith("JONNEZ") ? new Vector3(-0.05f, -0.35f, 0.35f) : Vector3.zero;
                item.transform.localRotation = item.name.StartsWith("JONNEZ") ? Quaternion.Euler(0f, 10f, 0f) : Quaternion.identity;
                item.transform.localScale = origScale;
                UpdateHeldItemPosition();
            }
        }

        public void DropItem(GameObject item = null)
        {
            GameObject toDrop = item ?? CurrentHeldItem;
            CurrentHeldItem = null;
            if (toDrop != null)
            {
                toDrop.transform.SetParent(null, true);
                if (itemOriginalScales.TryGetValue(toDrop, out Vector3 orig))
                {
                    toDrop.transform.localScale = orig;
                    itemOriginalScales.Remove(toDrop);
                }
            }
            if (rightHandIK != null) rightHandIK.enabled = false;
            animController?.ForceResetArms(false);
        }

        public void SetPeeing(bool peeing)
        {
            isPeeing = peeing;
            if (peeStreamObject != null) peeStreamObject.SetActive(peeing);
            if (!peeing && peeParticleSystem != null) peeParticleSystem.Clear();
        }

        private void UpdatePeeStream()
        {
            if (RootObject == null) return;
            if (peeStreamObject == null) CreatePeeStream();
            if (peeParticleSystem == null) return;

            Vector3 origin = RootObject.transform.position + RootObject.transform.up * 0.62f + RootObject.transform.forward * 0.14f;
            Vector3 v0 = (RootObject.transform.forward * 0.94f + RootObject.transform.up * 0.34f).normalized * 4.6f;
            for (int d = 0; d < 3; d++)
            {
                peeParticleSystem.Emit(origin + UnityEngine.Random.insideUnitSphere * 0.012f, v0 + UnityEngine.Random.insideUnitSphere * 0.12f, 0.05f, 0.85f, new Color32(245, 230, 40, 220));
            }
        }

        private void CreatePeeStream()
        {
            peeStreamObject = new GameObject("RemotePeeStream");
            peeStreamObject.transform.SetParent(RootObject.transform, false);
            if (cachedPeeCircleTex == null)
            {
                cachedPeeCircleTex = new Texture2D(16, 16, TextureFormat.ARGB32, false);
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                        cachedPeeCircleTex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(1f - Vector2.Distance(new Vector2(x, y), new Vector2(7.5f, 7.5f)) / 7.5f)));
                cachedPeeCircleTex.Apply();
            }

            Shader s = Shader.Find("Particles/Alpha Blended") ?? Shader.Find("Diffuse");
            var mat = new Material(s) { mainTexture = cachedPeeCircleTex, color = new Color(0.96f, 0.90f, 0.15f, 0.85f) };
            peeParticleSystem = peeStreamObject.AddComponent<ParticleSystem>();
            peeParticleSystem.simulationSpace = ParticleSystemSimulationSpace.World;
            peeParticleSystem.enableEmission = false;
            peeParticleSystem.playOnAwake = false;
            var rend = peeStreamObject.GetComponent<ParticleSystemRenderer>();
            if (rend != null) rend.material = mat;
        }

        public void Destroy()
        {
            if (CurrentHeldItem != null) DropItem(CurrentHeldItem);
            if (peeStreamObject != null) UnityEngine.Object.Destroy(peeStreamObject);
            if (rightHandIK?.Target != null) UnityEngine.Object.Destroy(rightHandIK.Target.gameObject);
            if (leftHandIK?.Target != null) UnityEngine.Object.Destroy(leftHandIK.Target.gameObject);
            if (RootObject != null) UnityEngine.Object.Destroy(RootObject);
        }
    }
}
