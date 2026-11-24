using System;
using System.Collections.Generic;
using OurSummerCar.Vehicles;
using UnityEngine;

namespace OurSummerCar.Player
{
    public class RemotePuppet
    {
        public int Id { get; }
        public GameObject RootObject { get; private set; }
        public Transform ItemHolder { get; private set; }
        public GameObject CurrentHeldItem { get; private set; }

        // Полноценная 3D-модель персонажа из WreckMP
        private GameObject characterModel;
        private Animator animator;
        private Animator animatorGestures;
        private AnimatorSyncer animSyncer;
        private PuppetAnimationController animController;

        private Transform headPivot;
        private Transform spineUpperBone;
        private Transform shoulderRightBone;
        private Transform shoulderLeftBone;
        private Transform handRightBone;
        private Transform handLeftBone;
        private FastIKFabric rightHandIK;
        private FastIKFabric leftHandIK;
        private GameObject cigaretteObject;
        private GameObject beerBottleObject;

        private GameObject peeStreamObject;
        private AudioSource peeAudioSource;
        private bool isPeeing = false;

        // Fallback-примитивы на случай отсутствия бандла
        private GameObject fallbackHead;

        private Vector3 targetPosition;
        private Quaternion targetRotation = Quaternion.identity;
        private float targetHeadYaw = 0f;
        private float targetHeadPitch = 0f;
        private float targetLeanAngle = 0f;
        private bool isLeaning = false;
        private Vector3 lastPosition;
        private float smoothedSpeed = 0f;
        private byte lastAnimState = 0;
        private byte lastCrouchLevel = 0;
        private bool lastInCar = false;
        private bool lastSleepingInBed = false;
        private bool isAirborne = false;
        private bool isExplicitPassenger = false;
        private Transform passengerVehicleTransform = null;
        private Vector3 passengerSeatOffset = Vector3.zero;
        private int passengerSeatIndex = 0;

        // Точные смещения посадки водителя относительно центра модели (откалиброваны под charTransform.y = -0.38f)
        // 0: SATSUMA, 1: HAYOSIKO, 2: RUSCKO, 3: FERNDALE, 4: KEKMET, 5: GIFU, 6: JONNEZ ES, 7: FLATBED, 8: BOAT
        private static readonly Vector3[] DriverSeatOffsets = new Vector3[]
        {
            new Vector3(-0.25f, 0.14f, 0f),        // 0: SATSUMA
            new Vector3(-0.42f, 0.15f, 0.98f),     // 1: HAYOSIKO
            new Vector3(-0.29f, 0.34f, -0.08f),    // 2: RUSCKO
            new Vector3(-0.4f, 0.48f, -0.04f),     // 3: FERNDALE
            new Vector3(0f, 1.19f, -0.6f),         // 4: KEKMET
            new Vector3(-0.75f, 1.84f, 2.74f),     // 5: GIFU
            new Vector3(0.02f, 0.20f, -0.44f),     // 6: JONNEZ ES (на сиденье мопеда)
            new Vector3(-0.5f, 1.0f, 0f),          // 7: FLATBED
            new Vector3(-0.20f, -0.42f, 1.25f)     // 8: BOAT (на корме у румпеля, +Z = корма)
        };

        // Точные смещения посадки пассажира (откалиброваны под charTransform.y = -0.38f)
        private static readonly Vector3[] PassengerSeatOffsets = new Vector3[]
        {
            new Vector3(0.28f, 0.14f, 0f),         // 0: SATSUMA (правое переднее)
            new Vector3(0.42f, 0.48f, 0.82f),      // 1: HAYOSIKO (переднее пассажирское кресло)
            new Vector3(0.26f, 0.34f, -0.08f),     // 2: RUSCKO (пассажирское)
            new Vector3(0.40f, 0.48f, -0.04f),     // 3: FERNDALE (правое переднее)
            new Vector3(0.50f, 1.19f, -0.6f),      // 4: KEKMET (пассажирское)
            new Vector3(0.70f, 1.84f, 2.74f),      // 5: GIFU (пассажирское)
            new Vector3(0.02f, 0.66f, -0.75f),     // 6: JONNEZ ES (задняя сидушка/багажник)
            new Vector3(0.50f, 1.0f, 0f),          // 7: FLATBED (пассажир)
            new Vector3(0f, -0.25f, 0.1f)          // 8: BOAT (пассажир 1)
        };

        public string Nickname { get; private set; }
        private static Camera cachedMainCam;
        private static Texture2D cachedPeeCircleTex;
        private ParticleSystem peeParticleSystem;

        private GameObject nametagObject;
        private TextMesh nametagMesh;
        private TextMesh shadowMesh;
        private Color baseNametagColor;
        private readonly List<Collider> disabledColliders = new List<Collider>();
        private readonly Dictionary<GameObject, Vector3> itemOriginalScales = new Dictionary<GameObject, Vector3>();

        public RemotePuppet(int id, Vector3 initialPosition, Quaternion initialRotation)
        {
            Id = id;
            targetPosition = initialPosition;
            lastPosition = initialPosition;
            targetRotation = initialRotation;

            CreatePuppetModel(initialPosition, initialRotation);
        }

        private void CreatePuppetModel(Vector3 pos, Quaternion rot)
        {
            RootObject = new GameObject(Id == 0 ? "RemotePuppet_Host" : $"RemotePuppet_Client_{Id}");
            RootObject.transform.position = pos;
            RootObject.transform.rotation = rot;
            RootObject.layer = 0;

            // Единый ItemHolder, ВСЕГДА привязанный к RootObject с неизменным масштабом (1, 1, 1).
            // Это полностью изолирует любые предметы от искажений масштаба костей скелета (FBX/3dsMax)!
            GameObject holderObj = new GameObject("ItemHolder");
            holderObj.transform.SetParent(RootObject.transform, false);
            holderObj.transform.localPosition = new Vector3(0.18f, 1.15f, 0.4f);
            holderObj.transform.localRotation = Quaternion.identity;
            holderObj.transform.localScale = Vector3.one;
            ItemHolder = holderObj.transform;

            // Попытка инстанцировать настоящую 3D-модель WreckMP (char.prefab)
            characterModel = CharacterAssetLoader.InstantiateCharacter(Id, RootObject.transform);

            if (characterModel != null)
            {
                SetupWreckMPCharacter();
            }
            else
            {
                SetupFallbackPrimitives();
            }

            // Табличка с именем над головой
            baseNametagColor = (Id == 0) ? new Color(0.3f, 1f, 0.45f) : new Color(1f, 0.92f, 0.25f);
            Nickname = (Id == 0) ? "ХОСТ" : $"Игрок #{Id}";

            nametagObject = new GameObject("Nametag");
            nametagObject.transform.SetParent(RootObject.transform, false);
            nametagObject.transform.localPosition = new Vector3(0, 2.15f, 0);

            nametagMesh = nametagObject.AddComponent<TextMesh>();
            nametagMesh.text = Nickname;
            nametagMesh.fontSize = 32;
            nametagMesh.characterSize = 0.042f;
            nametagMesh.alignment = TextAlignment.Center;
            nametagMesh.anchor = TextAnchor.MiddleCenter;
            nametagMesh.color = baseNametagColor;

            // Тень для четкой читаемости текста на любом фоне (небо, снег, лампы)
            GameObject shadowObj = new GameObject("Shadow");
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

        private void SetupWreckMPCharacter()
        {
            // Отключаем лишние физические коллайдеры
            var colliders = characterModel.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                UnityEngine.Object.Destroy(colliders[i]);
            }

            // Инициализация аниматоров
            var animLocomotionTr = characterModel.transform.Find("char/skeleton ANIMATOR");
            var animGesturesTr = characterModel.transform.Find("char/skeleton ANIMATOR gestures");
            var skeletonTr = characterModel.transform.Find("char/skeleton");

            if (animLocomotionTr != null) animator = animLocomotionTr.GetComponent<Animator>();
            if (animGesturesTr != null) animatorGestures = animGesturesTr.GetComponent<Animator>();

            if (skeletonTr != null && animLocomotionTr != null)
            {
                animSyncer = skeletonTr.gameObject.AddComponent<AnimatorSyncer>();
                animSyncer.sourceSkeleton = animLocomotionTr;
                animSyncer.sourceSkeleton2 = animGesturesTr;
                animSyncer.InitBones();
            }

            // Точка наклона головы (HeadRotationPivot)
            headPivot = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/headPivot/HeadRotationPivot");

            // Кости позвоночника и плеча для процедурного позиционирования удерживаемых предметов
            spineUpperBone = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper");
            shoulderRightBone = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/collar_right/shoulder_right");

            // Кость правой руки и инверсная кинематика IK (FastIKFabric)
            handRightBone = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/collar_right/shoulder_right/arm_right/hand_right");
            var fingersR = (handRightBone != null) ? handRightBone.Find("fingers_right") : null;
            var raHint = skeletonTr != null ? skeletonTr.Find("ra_hint") : null;
            if (raHint == null && skeletonTr != null)
            {
                var hintObj = new GameObject("ra_hint");
                hintObj.transform.SetParent(shoulderRightBone != null ? shoulderRightBone : skeletonTr, false);
                hintObj.transform.localPosition = (shoulderRightBone != null) ? new Vector3(0.15f, -0.1f, -0.35f) : new Vector3(0.35f, 1.15f, -0.3f);
                raHint = hintObj.transform;
            }

            if (fingersR != null)
            {
                rightHandIK = FastIKFabric.CreateInstance(fingersR, 3, raHint);
                if (rightHandIK != null)
                {
                    rightHandIK.enabled = false;
                }

                var cigTr = fingersR.Find("cigarette_shaft_0");
                if (cigTr != null)
                {
                    cigaretteObject = cigTr.gameObject;
                    cigaretteObject.SetActive(false);
                }
            }

            // Кость левой руки
            shoulderLeftBone = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/collar_left/shoulder_left");
            var handLeftTr = characterModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/collar_left/shoulder_left/arm_left/hand_left");
            handLeftBone = handLeftTr;
            var fingersL = (handLeftBone != null) ? (handLeftBone.Find("finger_left") ?? handLeftBone.Find("fingers_left")) : null;
            var laHint = skeletonTr != null ? skeletonTr.Find("la_hint") : null;
            if (laHint == null && skeletonTr != null)
            {
                var hintObj = new GameObject("la_hint");
                hintObj.transform.SetParent(shoulderLeftBone != null ? shoulderLeftBone : skeletonTr, false);
                hintObj.transform.localPosition = (shoulderLeftBone != null) ? new Vector3(-0.15f, -0.1f, -0.35f) : new Vector3(-0.35f, 1.15f, -0.3f);
                laHint = hintObj.transform;
            }

            if (fingersL != null)
            {
                leftHandIK = FastIKFabric.CreateInstance(fingersL, 3, laHint);
                if (leftHandIK != null)
                {
                    leftHandIK.enabled = false;
                }
            }

            // Подключение бутылки пива к левой руке для анимации питья (drinkbeer)
            beerBottleObject = GetOrCreateBeerBottle(handLeftBone);

            // Подключение централизованного контроллера анимаций WreckMP
            if (skeletonTr != null)
            {
                animController = skeletonTr.gameObject.AddComponent<PuppetAnimationController>();
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

        private void SetupFallbackPrimitives()
        {
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(RootObject.transform, false);
            body.transform.localPosition = new Vector3(0, 0.27f, 0);
            body.transform.localScale = new Vector3(0.5f, 0.65f, 0.5f);
            var bCol = body.GetComponent<Collider>();
            if (bCol != null) UnityEngine.Object.Destroy(bCol);

            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(RootObject.transform, false);
            head.transform.localPosition = new Vector3(0, 1.02f, 0);
            head.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
            var hCol = head.GetComponent<Collider>();
            if (hCol != null) UnityEngine.Object.Destroy(hCol);
            fallbackHead = head;

            Shader diffuseShader = Shader.Find("Diffuse") ?? Shader.Find("VertexLit") ?? Shader.Find("Standard");
            if (diffuseShader != null)
            {
                Color jacketColor = (Id == 0) ? new Color(0.15f, 0.8f, 0.3f) : new Color(0.15f, 0.55f, 0.95f);
                body.GetComponent<Renderer>().material = new Material(diffuseShader) { color = jacketColor };
                head.GetComponent<Renderer>().material = new Material(diffuseShader) { color = new Color(0.95f, 0.75f, 0.6f) };
            }
        }

        public void UpdateTarget(Vector3 pos, float rotY, float headPitch, byte animState, float leanAngle = 0f, bool isAirborne = false)
        {
            this.isAirborne = isAirborne;

            // Фильтр микро-колебаний по вертикали (не сглаживаем в прыжке)
            float diffY = Mathf.Abs(pos.y - targetPosition.y);
            if (!isAirborne && diffY < 0.04f)
            {
                pos.y = Mathf.Lerp(targetPosition.y, pos.y, 0.25f);
            }

            targetPosition = pos;
            targetHeadYaw = rotY;
            targetRotation = Quaternion.Euler(0, rotY, 0);
            targetHeadPitch = headPitch;
            targetLeanAngle = leanAngle;
            isLeaning = (animState & 64) != 0 || leanAngle >= 30f;

            byte prevAnimState = lastAnimState;
            lastAnimState = animState;

            bool isSmoking = (animState & 1) != 0;
            animController?.SetSmoking(isSmoking);

            // Потоковое непрерывное обновление мочеиспускания (бит 8)
            bool netPeeing = (animState & 8) != 0;
            if (netPeeing != isPeeing)
            {
                SetPeeing(netPeeing);
            }

            // Потоковый запуск анимации питья пива (бит 16)
            bool isDrinkingBeer = (animState & 16) != 0;
            bool wasDrinkingBeer = (prevAnimState & 16) != 0;
            if (isDrinkingBeer && !wasDrinkingBeer && !Items.ItemManager.IsBagUnpackCooldown)
            {
                TriggerGesture(7);
            }
        }

        public void TriggerGesture(int index, byte extraParam = 0)
        {
            animController?.TriggerGesture(index, extraParam);
        }

        public void StopGesture(int index = -1)
        {
            animController?.StopGesture(index);
        }

        public void PlaySwearAudio(byte swearId)
        {
            animController?.PlaySwearAudio(swearId);
        }

        public void SetSmoking(bool isSmoking)
        {
            animController?.SetSmoking(isSmoking);
        }

        public void SetPassengerState(bool passenger, Transform vehTransform = null, Vector3 seatOffset = default(Vector3), int seatIndex = 0)
        {
            isExplicitPassenger = passenger;
            passengerVehicleTransform = passenger ? vehTransform : null;
            passengerSeatOffset = passenger ? seatOffset : Vector3.zero;
            passengerSeatIndex = passenger ? seatIndex : 0;

            if (!passenger && RootObject != null && RootObject.transform.parent != null)
            {
                RootObject.transform.SetParent(null, true);
                if (leftHandIK != null && leftHandIK.enabled) leftHandIK.enabled = false;
                if (rightHandIK != null && rightHandIK.enabled && CurrentHeldItem == null) rightHandIK.enabled = false;
            }

            if (animController != null && animController.animator != null)
            {
                animController.animator.SetBool("passenger", passenger);
                if (passenger)
                {
                    animController.animator.Play("PassengerMode");
                }
            }
        }

        private int ResolveVehicleId(TrackedVehicle drivenVeh, Transform vehTransform)
        {
            if (drivenVeh != null && drivenVeh.Id >= 0) return drivenVeh.Id;
            if (vehTransform == null) return -1;
            string n = vehTransform.name.ToUpperInvariant();
            if (n.Contains("SATSU")) return 0;
            if (n.Contains("HAYO")) return 1;
            if (n.Contains("RUSCK")) return 2;
            if (n.Contains("FERN")) return 3;
            if (n.Contains("KEKM")) return 4;
            if (n.Contains("GIFU")) return 5;
            if (n.Contains("JONNEZ")) return 6;
            if (n.Contains("FLATBED")) return 7;
            if (n.Contains("BOAT")) return 8;
            return -1;
        }

        private void UpdateJonnezSteeringIK(TrackedVehicle drivenVeh)
        {
            if (drivenVeh == null || drivenVeh.Transform == null) return;

            Transform steeringPivot = drivenVeh.Transform.Find("LOD/Suspension/Steering/SteeringPivot") ??
                                      drivenVeh.Transform.Find("Suspension/Steering/SteeringPivot") ??
                                      (drivenVeh.JonnezSteeringFork != null ? drivenVeh.JonnezSteeringFork.Find("SteeringPivot") : null);

            Transform column = steeringPivot != null ? steeringPivot.Find("Column") : null;
            Transform throttle = steeringPivot != null ? steeringPivot.Find("Throttle") : null;

            if (leftHandIK != null && leftHandIK.Target != null)
            {
                if (column != null)
                {
                    leftHandIK.Target.position = column.TransformPoint(new Vector3(0.03f, -0.3f, 0.41f));
                    leftHandIK.Target.rotation = column.rotation * Quaternion.Euler(0f, 320f, 80f);
                }
                else if (drivenVeh.JonnezSteeringFork != null)
                {
                    leftHandIK.Target.position = drivenVeh.JonnezSteeringFork.TransformPoint(new Vector3(-0.25f, 0.38f, 0.05f));
                    leftHandIK.Target.rotation = drivenVeh.JonnezSteeringFork.rotation;
                }
                else
                {
                    leftHandIK.Target.position = drivenVeh.Transform.TransformPoint(new Vector3(-0.22f, 0.95f, 0.15f));
                    leftHandIK.Target.rotation = drivenVeh.Transform.rotation;
                }
                leftHandIK.enabled = true;
            }

            if (rightHandIK != null && rightHandIK.Target != null && CurrentHeldItem == null)
            {
                if (throttle != null)
                {
                    rightHandIK.Target.position = throttle.TransformPoint(new Vector3(0.07f, 0.02f, 0f));
                    rightHandIK.Target.rotation = throttle.rotation * Quaternion.Euler(0f, 10f, 340f);
                }
                else if (drivenVeh.JonnezSteeringFork != null)
                {
                    rightHandIK.Target.position = drivenVeh.JonnezSteeringFork.TransformPoint(new Vector3(0.25f, 0.38f, 0.05f));
                    rightHandIK.Target.rotation = drivenVeh.JonnezSteeringFork.rotation;
                }
                else
                {
                    rightHandIK.Target.position = drivenVeh.Transform.TransformPoint(new Vector3(0.22f, 0.95f, 0.15f));
                    rightHandIK.Target.rotation = drivenVeh.Transform.rotation;
                }
                rightHandIK.enabled = true;
            }
        }

        public void UpdateInterpolation(float deltaTime)
        {
            if (RootObject == null) return;

            // Извлечение параметров анимации из animState (бит 1 = присед, бит 5 = лежа/ползание, бит 7 = сон на кровати)
            byte crouchLevel = (byte)((lastAnimState & 32) != 0 ? 2 : ((lastAnimState & 2) != 0 ? 1 : 0));

            bool isDriver = false;
            TrackedVehicle drivenVeh = null;
            if (VehiclePhysicsManager.Instance != null && VehiclePhysicsManager.Instance.IsPlayerDrivingAnyVehicle(Id, out drivenVeh))
            {
                isDriver = true;
            }

            bool isPassenger = ((lastAnimState & 4) != 0 || isExplicitPassenger) && !isDriver;
            bool inCar = isDriver || isPassenger;
            bool isSleepingInBed = (lastAnimState & 128) != 0;

            if (inCar && drivenVeh == null && VehiclePhysicsManager.Instance != null)
            {
                if (isExplicitPassenger && passengerVehicleTransform != null)
                {
                    drivenVeh = VehiclePhysicsManager.Instance.GetVehicleByGameObject(passengerVehicleTransform.gameObject);
                }
                else
                {
                    drivenVeh = VehiclePhysicsManager.Instance.GetClosestVehicle(RootObject.transform.position, 4.0f);
                }
            }

            Transform vehTransform = null;
            if (inCar)
            {
                if (isExplicitPassenger && passengerVehicleTransform != null)
                {
                    vehTransform = passengerVehicleTransform;
                }
                else if (drivenVeh != null && drivenVeh.Transform != null)
                {
                    vehTransform = drivenVeh.Transform;
                }
            }

            // Посадка куклы в транспорт по архитектуре WreckMP:
            // 1. Физическое удочерение куклы к объекту автомобиля (RootObject.transform.SetParent(vehTransform, false))
            // 2. Локальная позиция и поворот задаются строго по сиденью из WreckMP
            // 3. Сетевой Lerp и проверка высоты Raycast ПОЛНОСТЬЮ ОТКЛЮЧАЮТСЯ во избежание левитации и борьбы координат
            if (inCar && vehTransform != null)
            {
                if (RootObject.transform.parent != vehTransform)
                {
                    RootObject.transform.SetParent(vehTransform, false);
                }

                int vehId = ResolveVehicleId(drivenVeh, vehTransform);
                Vector3 exactOffset;
                Quaternion exactRot;

                if (isPassenger)
                {
                    if (vehId >= 0 && vehId < PassengerSeatOffsets.Length && passengerSeatIndex == 0)
                    {
                        exactOffset = PassengerSeatOffsets[vehId];
                    }
                    else if (vehId == 1 && passengerSeatIndex == 1) // Hayosiko среднее кресло
                    {
                        exactOffset = new Vector3(0.04f, 0.48f, 0.82f);
                    }
                    else if (vehId == 8 && passengerSeatIndex == 1) // BOAT задний пассажир
                    {
                        exactOffset = new Vector3(0f, -0.25f, -1.0f);
                    }
                    else if (vehId >= 0 && vehId < PassengerSeatOffsets.Length)
                    {
                        exactOffset = PassengerSeatOffsets[vehId];
                    }
                    else if (passengerSeatOffset != Vector3.zero)
                    {
                        exactOffset = passengerSeatOffset;
                    }
                    else
                    {
                        exactOffset = new Vector3(0.42f, 0.48f, 0.82f);
                    }

                    if (vehId == 8) // BOAT: пассажиры сидят лицом вперед к носу (-Z)
                    {
                        exactRot = Quaternion.Euler(0f, 180f, 0f);
                    }
                    else
                    {
                        exactRot = Quaternion.identity;
                    }
                }
                else // Водитель
                {
                    if (vehId >= 0 && vehId < DriverSeatOffsets.Length)
                    {
                        exactOffset = DriverSeatOffsets[vehId];
                    }
                    else
                    {
                        exactOffset = new Vector3(-0.42f, 0.15f, 0.98f);
                    }

                    if (vehId == 8) // BOAT: водитель на корме, лицом вперед к носу (-Z) и румпелю
                    {
                        exactRot = Quaternion.Euler(0f, 160f, 0f);
                    }
                    else
                    {
                        exactRot = Quaternion.identity;
                    }
                }

                RootObject.transform.localPosition = exactOffset;
                RootObject.transform.localRotation = exactRot;

                // Руки на руле мопеда Jonnez ES (Id == 6)
                if (isDriver && vehId == 6 && drivenVeh != null)
                {
                    UpdateJonnezSteeringIK(drivenVeh);
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
                // Выход из машины: отсоединяем куклу от автомобиля обратно в мир
                if (RootObject.transform.parent != null)
                {
                    RootObject.transform.SetParent(null, true);
                    if (leftHandIK != null && leftHandIK.enabled) leftHandIK.enabled = false;
                    if (rightHandIK != null && rightHandIK.enabled && CurrentHeldItem == null) rightHandIK.enabled = false;
                }

                float distSq = (RootObject.transform.position - targetPosition).sqrMagnitude;
                Vector3 newPos;
                Quaternion newRot;

                if (distSq > 16f)
                {
                    newPos = targetPosition;
                    newRot = targetRotation;
                }
                else
                {
                    newPos = Vector3.Lerp(RootObject.transform.position, targetPosition, deltaTime * 12f);
                    newRot = Quaternion.Slerp(RootObject.transform.rotation, targetRotation, deltaTime * 12f);
                }

                // Точная привязка высоты к поверхности земли/пола (исключает левитацию, не применяется на кровати и в прыжке)
                if (!isSleepingInBed && !isAirborne)
                {
                    int groundMask = ~((1 << 20) | (1 << 23)); // Исключаем коллайдеры игроков и триггеры
                    RaycastHit groundHit;
                    if (Physics.Raycast(newPos + Vector3.up * 0.75f, Vector3.down, out groundHit, 2.0f, groundMask))
                    {
                        float distToGround = targetPosition.y - groundHit.point.y;
                        if (distToGround >= 0.05f && distToGround <= 1.2f)
                        {
                            newPos.y = groundHit.point.y + 0.38f;
                        }
                    }
                }

                // Расчет сглаженной скорости перемещения для реалистичной ходьбы/бега без рывков
                Vector3 horizontalMove = new Vector3(newPos.x - lastPosition.x, 0, newPos.z - lastPosition.z);
                float instantSpeed = horizontalMove.magnitude / Mathf.Max(deltaTime, 0.001f);
                lastPosition = newPos;

                smoothedSpeed = Mathf.Lerp(smoothedSpeed, instantSpeed, deltaTime * 8f);
                if (smoothedSpeed < 0.1f) smoothedSpeed = 0f;

                RootObject.transform.position = newPos;
                RootObject.transform.rotation = newRot;
            }

            // Плавный переход модельки в положение лёжа на кровати (на бок) и подъём на ноги
            if (characterModel != null)
            {
                if (isSleepingInBed)
                {
                    // Моделька плавно ложится на кровать вдоль спального места:
                    // Ноги строго направлены в сторону Yaw (+Z вдоль кровати), голова на подушке
                    Quaternion targetSleepRot = Quaternion.Euler(-90f, 0f, 0f);
                    Vector3 targetSleepOffset = new Vector3(0f, 0.12f, 1.45f);

                    characterModel.transform.localPosition = Vector3.Lerp(characterModel.transform.localPosition, targetSleepOffset, deltaTime * 6f);
                    characterModel.transform.localRotation = Quaternion.Slerp(characterModel.transform.localRotation, targetSleepRot, deltaTime * 6f);
                }
                else
                {
                    // Вне сна моделька плавно встает вертикально на ноги
                    if (characterModel.transform.localPosition.sqrMagnitude > 0.001f || Quaternion.Angle(characterModel.transform.localRotation, Quaternion.identity) > 0.5f)
                    {
                        characterModel.transform.localPosition = Vector3.Lerp(characterModel.transform.localPosition, Vector3.zero, deltaTime * 8f);
                        characterModel.transform.localRotation = Quaternion.Slerp(characterModel.transform.localRotation, Quaternion.identity, deltaTime * 8f);
                    }
                    else
                    {
                        characterModel.transform.localPosition = Vector3.zero;
                        characterModel.transform.localRotation = Quaternion.identity;
                    }
                }
            }

            if (animController != null)
            {
                // Во время сна и в автомобиле передаем animCrouch = 0, чтобы аниматор не включал позу ползания/приседа
                byte animCrouch = isSleepingInBed ? (byte)0 : (inCar ? (byte)0 : crouchLevel);
                animController.targetHeadYaw = targetHeadYaw;
                animController.UpdateLocomotion(smoothedSpeed, animCrouch, inCar, isPassenger, CurrentHeldItem != null, isSleepingInBed, isAirborne);
                if (isSleepingInBed)
                {
                    animController.SetPitchAndLean(0f, 0f, false);
                }
                else
                {
                    animController.SetPitchAndLean(targetHeadPitch, targetLeanAngle, isLeaning);
                }
            }
            else if (fallbackHead != null)
            {
                fallbackHead.transform.localRotation = isSleepingInBed
                    ? Quaternion.Euler(-90f, 0f, 0f)
                    : Quaternion.Euler(targetHeadPitch, 0, -targetLeanAngle);
            }

            lastCrouchLevel = crouchLevel;
            lastInCar = inCar;
            lastSleepingInBed = isSleepingInBed;

            // Позиционирование ItemHolder и руки строго с учетом положения тела (присед, положение лежа, наклон Q)
            UpdateHeldItemPosition();

            // Автоматическое восстановление рук в локомоторный режим при отсутствии предметов и жестов
            if ((CurrentHeldItem == null || !CurrentHeldItem.activeInHierarchy || (CurrentHeldItem.transform.parent != ItemHolder && CurrentHeldItem.transform.parent != null)) && !inCar && !isPeeing)
            {
                if (CurrentHeldItem != null)
                {
                    CurrentHeldItem = null;
                }
                if (rightHandIK != null && rightHandIK.enabled)
                {
                    rightHandIK.enabled = false;
                }
                if (animSyncer != null && (animController == null || animController.CurrentGesture == -1))
                {
                    if (animSyncer.rightArm == 0) animSyncer.rightArm = 1;
                    if (animSyncer.leftArm == 0) animSyncer.leftArm = 1;
                }
            }

            // Поза при мочеиспускании и обновление лужи
            if (isPeeing && !inCar)
            {
                if (animSyncer != null)
                {
                    animSyncer.leftArm = 0;
                    animSyncer.rightArm = 0;
                }
                if (handRightBone != null)
                {
                    handRightBone.localEulerAngles = new Vector3(-25f, 20f, -10f);
                }
                if (handLeftBone != null)
                {
                    handLeftBone.localEulerAngles = new Vector3(-25f, -20f, 10f);
                }
                UpdatePeeStream();
            }

            // Обновление таблички с именем (Billboard, дальность 10м и привязка к голове)
            if (nametagObject != null)
            {
                // Привязка высоты к фактической голове персонажа (присед/машина)
                if (headPivot != null)
                {
                    nametagObject.transform.position = headPivot.position + new Vector3(0f, 0.38f, 0f);
                }
                else if (fallbackHead != null)
                {
                    nametagObject.transform.position = fallbackHead.transform.position + new Vector3(0f, 0.38f, 0f);
                }
                else
                {
                    nametagObject.transform.position = RootObject.transform.position + new Vector3(0f, 2.15f, 0f);
                }

                if (cachedMainCam == null || !cachedMainCam.isActiveAndEnabled) cachedMainCam = Camera.main;
                if (cachedMainCam == null || !cachedMainCam.isActiveAndEnabled) cachedMainCam = Camera.current;

                if (cachedMainCam != null)
                {
                    float camDistSq = (cachedMainCam.transform.position - nametagObject.transform.position).sqrMagnitude;
                    const float maxDist = 10.0f; // Отображение строго в радиусе 10 метров
                    const float maxDistSq = maxDist * maxDist; // 100

                    if (camDistSq <= maxDistSq)
                    {
                        if (!nametagObject.activeSelf) nametagObject.SetActive(true);

                        // Всегда смотрит на игрока / камеру (Billboard)
                        nametagObject.transform.rotation = cachedMainCam.transform.rotation;

                        // Плавное затухание прозрачности от 8 до 10 метров
                        float dist = Mathf.Sqrt(camDistSq);
                        float alpha = (dist > 8f) ? Mathf.Clamp01((10f - dist) / 2f) : 1f;

                        Color c = baseNametagColor;
                        c.a = alpha;
                        if (nametagMesh != null) nametagMesh.color = c;
                        if (shadowMesh != null) shadowMesh.color = new Color(0f, 0f, 0f, 0.85f * alpha);
                    }
                    else
                    {
                        if (nametagObject.activeSelf) nametagObject.SetActive(false);
                    }
                }
            }
        }

        public void SetNickname(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                name = (Id == 0) ? "ХОСТ" : $"Игрок #{Id}";
            }
            Nickname = name;
            if (nametagMesh != null)
            {
                nametagMesh.text = Nickname;
            }
            if (shadowMesh != null)
            {
                shadowMesh.text = Nickname;
            }
        }

        /// <summary>
        /// Динамически позиционирует удерживаемый предмет (ItemHolder) и руку куклы (FastIKFabric) строго относительно груди,
        /// автоматически следуя за приседаниями (-0.5м), положением лежа (-0.7м), наклоном туловища вперед на 45° (Q)
        /// и поворотом головы. Исключает зависание руки и предмета в воздухе.
        /// </summary>
        public void UpdateHeldItemPosition()
        {
            if (ItemHolder == null || RootObject == null) return;

            bool isGestureActive = (animController != null && animController.CurrentGesture != -1);
            bool shouldIK = (CurrentHeldItem != null && !lastInCar && !isPeeing && !isGestureActive && !lastSleepingInBed);

            float torsoPitch = (animController != null) ? animController.CurrentTorsoPitch : (targetHeadPitch * 0.35f);
            Quaternion chestRot = RootObject.transform.rotation * Quaternion.Euler(torsoPitch, 0f, -targetLeanAngle);

            Vector3 chestForward = chestRot * Vector3.forward;
            Vector3 chestUp = chestRot * Vector3.up;
            Vector3 chestRight = chestRot * Vector3.right;

            Vector3 chestOrigin;
            if (shoulderRightBone != null)
            {
                chestOrigin = shoulderRightBone.position;
            }
            else if (spineUpperBone != null)
            {
                chestOrigin = spineUpperBone.position + chestRight * 0.18f;
            }
            else
            {
                float charDrop = (lastCrouchLevel == 0) ? 0f : ((lastCrouchLevel == 1) ? -0.50f : -0.70f);
                chestOrigin = RootObject.transform.position + RootObject.transform.up * (1.20f + charDrop) + chestRight * 0.18f;
            }

            Vector3 handTargetPos = chestOrigin + chestForward * 0.32f - chestRight * 0.04f - chestUp * 0.12f;

            ItemHolder.position = handTargetPos;
            ItemHolder.rotation = chestRot;
            ItemHolder.localScale = Vector3.one;

            if (rightHandIK != null)
            {
                if (rightHandIK.enabled != shouldIK) rightHandIK.enabled = shouldIK;

                if (shouldIK)
                {
                    rightHandIK.Target.position = ItemHolder.position;
                    // Естественный доворот кисти: направлена вперед вдоль корпуса с легким наклоном внутрь
                    rightHandIK.Target.rotation = chestRot * Quaternion.Euler(0f, -25f, 15f);
                }
            }
        }

        public void HoldItem(GameObject item)
        {
            if (item == null) return;

            if (CurrentHeldItem != null && CurrentHeldItem != item)
            {
                ReleaseItem(CurrentHeldItem);
            }

            CurrentHeldItem = item;
            disabledColliders.Clear();

            // Сохраняем исходный масштаб предмета или сбрасываем, если он уже был раздут багом
            Vector3 originalScale = item.transform.localScale;
            if (originalScale.sqrMagnitude > 3.5f || originalScale.sqrMagnitude < 0.2f)
            {
                originalScale = Vector3.one;
            }
            itemOriginalScales[item] = originalScale;

            var rb = item.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            var colliders = item.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i].enabled)
                {
                    colliders[i].enabled = false;
                    disabledColliders.Add(colliders[i]);
                }
            }

            if (ItemHolder != null)
            {
                item.transform.SetParent(ItemHolder, false);
                if (item.name.StartsWith("JONNEZ"))
                {
                    // Для мопеда смещаем положение вперед-вниз, чтобы он естественно лежал в руках на уровне руля/рамы,
                    // а не проваливался внутрь груди персонажа
                    item.transform.localPosition = new Vector3(-0.05f, -0.35f, 0.35f);
                    item.transform.localRotation = Quaternion.Euler(0f, 10f, 0f);
                }
                else
                {
                    item.transform.localPosition = Vector3.zero;
                    item.transform.localRotation = Quaternion.identity;
                }
                item.transform.localScale = originalScale;
                UpdateHeldItemPosition();
            }
        }

        public void DropItem(GameObject item = null)
        {
            GameObject toDrop = item ?? CurrentHeldItem;
            CurrentHeldItem = null;

            if (toDrop != null)
            {
                ReleaseItem(toDrop);
            }

            if (rightHandIK != null)
            {
                rightHandIK.enabled = false;
            }
            if (animSyncer != null)
            {
                animSyncer.rightArm = 1;
                animSyncer.leftArm = 1;
            }
            if (handRightBone != null)
            {
                handRightBone.localRotation = Quaternion.identity;
            }
            animController?.ForceResetArms(false);
        }

        public void ReleaseItem(GameObject item)
        {
            if (item == null)
            {
                CurrentHeldItem = null;
                return;
            }

            if (CurrentHeldItem == item)
            {
                CurrentHeldItem = null;
            }

            item.transform.SetParent(null, true);

            // Восстанавливаем оригинальный масштаб (гарантированно 1,1,1)
            if (itemOriginalScales.TryGetValue(item, out Vector3 orig))
            {
                item.transform.localScale = orig;
                itemOriginalScales.Remove(item);
            }
            else
            {
                item.transform.localScale = Vector3.one;
            }

            for (int i = 0; i < disabledColliders.Count; i++)
            {
                if (disabledColliders[i] != null)
                {
                    disabledColliders[i].enabled = true;
                }
            }
            disabledColliders.Clear();

            var rb = item.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.useGravity = true;
            }

            // Гарантированный сброс IK руки и восстановление локомоторных каналов анимации
            if (rightHandIK != null)
            {
                rightHandIK.enabled = false;
            }
            if (animSyncer != null)
            {
                animSyncer.rightArm = 1;
                animSyncer.leftArm = 1;
            }
            if (handRightBone != null)
            {
                handRightBone.localRotation = Quaternion.identity;
            }
            animController?.ForceResetArms(false);
        }

        public void SetPeeing(bool peeing)
        {
            isPeeing = peeing;

            if (isPeeing)
            {
                if (peeStreamObject == null)
                {
                    CreatePeeStream();
                }
                if (peeStreamObject != null)
                {
                    peeStreamObject.SetActive(true);
                }
                if (peeAudioSource == null)
                {
                    InitPeeAudio();
                }
                if (peeAudioSource != null && !peeAudioSource.isPlaying)
                {
                    peeAudioSource.Play();
                }
            }
            else
            {
                if (peeParticleSystem != null)
                {
                    peeParticleSystem.Clear();
                }
                if (peeStreamObject != null)
                {
                    peeStreamObject.SetActive(false);
                }
                if (peeAudioSource != null && peeAudioSource.isPlaying)
                {
                    peeAudioSource.Stop();
                }
                if (animSyncer != null)
                {
                    animSyncer.leftArm = 1;
                    animSyncer.rightArm = (CurrentHeldItem != null) ? 0 : 1;
                }
                if (handRightBone != null)
                {
                    handRightBone.localRotation = Quaternion.identity;
                }
                if (handLeftBone != null)
                {
                    handLeftBone.localRotation = Quaternion.identity;
                }
            }
        }

        private void CreatePeeStream()
        {
            peeStreamObject = new GameObject("RemotePeeStream");
            peeStreamObject.transform.SetParent(RootObject.transform, false);

            Shader s = FindSafeShader("Particles/Alpha Blended", "Legacy Shaders/Particles/Alpha Blended", "Mobile/Particles/Alpha Blended", "Transparent/Diffuse", "Diffuse");
            Material pMat = new Material(s);
            pMat.mainTexture = GetPeeTexture();
            Color peeColor = new Color(0.96f, 0.90f, 0.15f, 0.85f);
            pMat.color = peeColor;
            if (pMat.HasProperty("_Color")) pMat.SetColor("_Color", peeColor);
            if (pMat.HasProperty("_TintColor")) pMat.SetColor("_TintColor", peeColor);

            // Частицы-капли, летящие по баллистической траектории
            peeParticleSystem = peeStreamObject.AddComponent<ParticleSystem>();
            peeParticleSystem.startLifetime = 0.85f;
            peeParticleSystem.startSpeed = 4.6f;
            peeParticleSystem.startSize = 0.045f;
            peeParticleSystem.startColor = peeColor;
            peeParticleSystem.gravityModifier = 1.5f;
            peeParticleSystem.simulationSpace = ParticleSystemSimulationSpace.World;
            peeParticleSystem.enableEmission = false;
            peeParticleSystem.maxParticles = 120;
            peeParticleSystem.playOnAwake = false;

            var rend = peeStreamObject.GetComponent<ParticleSystemRenderer>();
            if (rend != null)
            {
                rend.material = pMat;
            }
        }

        private static Shader FindSafeShader(params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                Shader s = Shader.Find(names[i]);
                if (s != null) return s;
            }
            return Shader.Find("Diffuse") ?? Shader.Find("Unlit/Texture") ?? Shader.Find("VertexLit");
        }

        private static Texture2D GetPeeTexture()
        {
            if (cachedPeeCircleTex != null) return cachedPeeCircleTex;
            cachedPeeCircleTex = new Texture2D(32, 32, TextureFormat.ARGB32, false);
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(15.5f, 15.5f)) / 15.5f;
                    float alpha = Mathf.Clamp01(1f - dist);
                    cachedPeeCircleTex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha * alpha));
                }
            }
            cachedPeeCircleTex.Apply();
            return cachedPeeCircleTex;
        }

        private void UpdatePeeStream()
        {
            if (RootObject == null) return;
            if (peeStreamObject == null) CreatePeeStream();
            if (peeParticleSystem == null) return;

            // Вылет мочи строго из области паха/мошонки (y = 0.62f), а не из живота
            Vector3 origin = RootObject.transform.position + RootObject.transform.up * 0.62f + RootObject.transform.forward * 0.14f;
            Vector3 v0 = (RootObject.transform.forward * 0.94f + RootObject.transform.up * 0.34f).normalized * 4.6f;

            // Эмитируем реалистичные капли вдоль баллистической параболы струи (без статичной полосы и без лужи)
            for (int d = 0; d < 3; d++)
            {
                Vector3 dropVel = v0 + UnityEngine.Random.insideUnitSphere * 0.12f;
                peeParticleSystem.Emit(origin + UnityEngine.Random.insideUnitSphere * 0.012f, dropVel, UnityEngine.Random.Range(0.040f, 0.060f), 0.85f, new Color32(245, 230, 40, 220));
            }
        }

        private GameObject GetOrCreateBeerBottle(Transform handLeft)
        {
            if (handLeft == null) return null;

            // 1. Сначала ищем нативную бутылку в модели префаба clothes.unity3d (как в WreckMP: hand_left/beer_bottle)
            var nativeBottleTr = handLeft.Find("beer_bottle");
            if (nativeBottleTr != null)
            {
                var nativeBottle = nativeBottleTr.gameObject;
                var rends = nativeBottle.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rends.Length; i++)
                {
                    rends[i].enabled = true;
                    rends[i].gameObject.layer = 0;
                }
                nativeBottle.SetActive(false);
                return nativeBottle;
            }

            // 2. Если нативная бутылка не найдена, ищем в ресурсах/сцене, включая bottle(Clone) и bottle
            GameObject sourceBottle = null;
            try
            {
                var allGos = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allGos.Length; i++)
                {
                    if (allGos[i] == null) continue;
                    string n = allGos[i].name;
                    if (n == "BeerBottle" || n == "beer bottle(itemx)" || n == "beer(itemx)" || n == "BottleBeerFly" ||
                        n == "bottle(Clone)" || n == "bottle" || n.StartsWith("bottle(Clone)"))
                    {
                        var mrs = allGos[i].GetComponentsInChildren<MeshRenderer>(true);
                        var mfs = allGos[i].GetComponentsInChildren<MeshFilter>(true);
                        if (mrs.Length > 0 && mfs.Length > 0 && mfs[0].sharedMesh != null)
                        {
                            sourceBottle = allGos[i];
                            break;
                        }
                    }
                }
            }
            catch { }

            GameObject bottleObj;
            if (sourceBottle != null)
            {
                bottleObj = UnityEngine.Object.Instantiate(sourceBottle);
                bottleObj.name = "RemoteBeerBottle";
                var cols = bottleObj.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < cols.Length; i++) UnityEngine.Object.Destroy(cols[i]);
                var rbs = bottleObj.GetComponentsInChildren<Rigidbody>(true);
                for (int i = 0; i < rbs.Length; i++) UnityEngine.Object.Destroy(rbs[i]);
                var fsms = bottleObj.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < fsms.Length; i++) UnityEngine.Object.Destroy(fsms[i]);

                // Включаем все рендереры, выставляем нормальный видимый слой
                var rends = bottleObj.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rends.Length; i++)
                {
                    rends[i].enabled = true;
                    rends[i].gameObject.layer = 0;
                }

                bottleObj.transform.SetParent(handLeft, false);
                bottleObj.transform.localPosition = new Vector3(0.04f, 0.08f, 0.015f);
                bottleObj.transform.localRotation = Quaternion.Euler(0f, 90f, 180f);
                bottleObj.transform.localScale = Vector3.one;
            }
            else
            {
                bottleObj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                bottleObj.name = "RemoteBeerBottle";
                var col = bottleObj.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col);

                Renderer sampleRend = characterModel != null ? characterModel.GetComponentInChildren<Renderer>() : null;
                Shader s = sampleRend != null && sampleRend.material != null ? sampleRend.material.shader : FindSafeShader("Diffuse", "Standard", "VertexLit");
                Material bMat = new Material(s);
                bMat.color = new Color(0.24f, 0.14f, 0.03f, 1f);
                var rend = bottleObj.GetComponent<Renderer>();
                if (rend != null) rend.material = bMat;
                bottleObj.layer = 0;

                bottleObj.transform.SetParent(handLeft, false);
                bottleObj.transform.localPosition = new Vector3(0.04f, 0.08f, 0.015f);
                bottleObj.transform.localRotation = Quaternion.Euler(0f, 90f, 180f);
                bottleObj.transform.localScale = new Vector3(0.065f, 0.12f, 0.065f);
            }

            bottleObj.SetActive(false);
            return bottleObj;
        }

        private void InitPeeAudio()
        {
            if (RootObject == null) return;
            var localPeeSound = GameObject.Find("PLAYER/PeeSound");
            AudioClip clip = null;
            if (localPeeSound != null)
            {
                var src = localPeeSound.GetComponent<AudioSource>();
                if (src != null) clip = src.clip;
            }

            if (clip != null)
            {
                peeAudioSource = RootObject.AddComponent<AudioSource>();
                peeAudioSource.clip = clip;
                peeAudioSource.loop = true;
                peeAudioSource.spatialBlend = 1.0f;
                peeAudioSource.minDistance = 2f;
                peeAudioSource.maxDistance = 14f;
                peeAudioSource.rolloffMode = AudioRolloffMode.Linear;
                peeAudioSource.volume = 0.8f;
                peeAudioSource.playOnAwake = false;
            }
        }

        public void Destroy()
        {
            if (CurrentHeldItem != null)
            {
                ReleaseItem(CurrentHeldItem);
                CurrentHeldItem = null;
            }

            if (peeAudioSource != null)
            {
                peeAudioSource.Stop();
            }

            if (peeStreamObject != null)
            {
                UnityEngine.Object.Destroy(peeStreamObject);
                peeStreamObject = null;
                peeParticleSystem = null;
            }

            if (rightHandIK != null && rightHandIK.Target != null)
            {
                UnityEngine.Object.Destroy(rightHandIK.Target.gameObject);
                rightHandIK = null;
            }

            if (leftHandIK != null && leftHandIK.Target != null)
            {
                UnityEngine.Object.Destroy(leftHandIK.Target.gameObject);
                leftHandIK = null;
            }

            if (ItemHolder != null)
            {
                UnityEngine.Object.Destroy(ItemHolder.gameObject);
                ItemHolder = null;
            }

            if (RootObject != null)
            {
                UnityEngine.Object.Destroy(RootObject);
                RootObject = null;
            }
        }
    }
}
