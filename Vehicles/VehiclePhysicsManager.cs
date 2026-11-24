using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using OurSummerCar.Player;
using UnityEngine;

namespace OurSummerCar.Vehicles
{
    public class ManagedRigidbodyState
    {
        public Rigidbody Rb;
        public bool DefaultKinematic;
        public bool DefaultUseGravity;
    }

    public class TrackedVehicle
    {
        public byte Id;
        public string Name;
        public GameObject GameObject;
        public Transform Transform;
        public Rigidbody Rigidbody;
        public bool DefaultKinematic;
        public bool DefaultUseGravity = true;
        public PlayMakerFSM DrivingModeFsm;

        public bool IsBeingDrivenLocally;
        public bool IsBeingDrivenRemotely;
        public int RemoteDriverId = -1;

        public bool IsBeingCarriedLocally;
        public bool IsBeingCarriedRemotely;
        public int RemoteCarrierId = -1;

        public bool IsBeingPushedLocally;
        public float LocalPushTimer;
        public float RemoteAuthorityTimer;

        public Vector3 TargetPosition;
        public Quaternion TargetRotation;
        public Vector3 TargetVelocity;
        public Vector3 TargetAngularVelocity;

        public float LastSendTime;
        public Vector3 LastSentPosition;
        public Quaternion LastSentRotation;

        public Transform JonnezSteeringFork;
        public Rigidbody[] AllRigidbodies;
        public readonly List<ManagedRigidbodyState> ManagedRigidbodies = new List<ManagedRigidbodyState>();
        public AudioSource[] JonnezCrashAudioSources;

        public void SetKinematic(bool kinematic)
        {
            if (ManagedRigidbodies.Count > 0)
            {
                for (int i = 0; i < ManagedRigidbodies.Count; i++)
                {
                    var state = ManagedRigidbodies[i];
                    if (state == null || state.Rb == null) continue;

                    if (kinematic)
                    {
                        state.Rb.isKinematic = true;
                        state.Rb.useGravity = false;
                    }
                    else
                    {
                        state.Rb.isKinematic = state.DefaultKinematic;
                        state.Rb.useGravity = state.DefaultUseGravity;
                    }
                }
            }
            else if (Rigidbody != null)
            {
                if (kinematic)
                {
                    Rigidbody.isKinematic = true;
                    Rigidbody.useGravity = false;
                }
                else
                {
                    Rigidbody.isKinematic = DefaultKinematic;
                    Rigidbody.useGravity = DefaultUseGravity;
                }
            }
        }

        public void WakeUpManaged()
        {
            if (ManagedRigidbodies.Count > 0)
            {
                for (int i = 0; i < ManagedRigidbodies.Count; i++)
                {
                    var state = ManagedRigidbodies[i];
                    if (state != null && state.Rb != null && !state.Rb.isKinematic)
                    {
                        state.Rb.WakeUp();
                    }
                }
            }
            else if (Rigidbody != null && !Rigidbody.isKinematic)
            {
                Rigidbody.WakeUp();
            }
        }
    }

    /// <summary>
    /// Сетевой менеджер непрерывной физики, вождения и толкания руками всех видов транспорта:
    /// Satsuma, Hayosiko, Ruscko, Ferndale, Kekmet, Gifu, Jonnez ES, Flatbed, Boat.
    /// 
    /// Реализует:
    /// 1. Передачу власти над Rigidbody при посадке за руль (Driving Mode).
    /// 2. Передачу власти над Rigidbody при толкании машины/мопеда руками (Hand Push).
    /// 3. Полноценную синхронизацию мопеда Jonnez ES (физика, руль, вождение, переноска).
    /// 4. Синхронизацию положения лодки (Boat) и прицепа (Flatbed).
    /// 5. Плавную физическую интерполяцию и удержание позиций покоя хостом.
    /// </summary>
    public class VehiclePhysicsManager : MonoBehaviour
    {
        public static VehiclePhysicsManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        public static readonly string[] VehicleNames = new string[]
        {
            "SATSUMA(557kg, 248)",
            "HAYOSIKO(1500kg, 250)",
            "RCO_RUSCKO12(270)",
            "FERNDALE(1630kg)",
            "KEKMET(350-400psi)",
            "GIFU(750/450psi)",
            "JONNEZ ES(Clone)",
            "FLATBED",
            "BOAT"
        };

        private readonly List<TrackedVehicle> vehicles = new List<TrackedVehicle>();
        private GameObject handPushObject;
        private Transform localPlayerTransform;
        private readonly List<Collider> ignoredVehicleColliders = new List<Collider>();
        private bool isLocalPlayerJonnezCollisionIgnored = false;
        private readonly HashSet<int> ignoredJonnezPuppetIds = new HashSet<int>();
        private bool isSeatedCollisionsIgnored = false;
        private float nextDiscoveryTime = 0f;
        private bool isInitialized = false;

        public void Reset()
        {
            SetDrivingCollisions(null, false);
            isInitialized = false;
            vehicles.Clear();
            handPushObject = null;
            localPlayerTransform = null;
            ignoredVehicleColliders.Clear();
            isLocalPlayerJonnezCollisionIgnored = false;
            ignoredJonnezPuppetIds.Clear();
            isSeatedCollisionsIgnored = false;
        }

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            InitVehicles();
            isInitialized = true;
            OnLog?.Invoke("[Транспорт] Менеджер синхронизации физики и толкания инициализирован");
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
            }
        }

        public void InitVehicles()
        {
            vehicles.Clear();

            for (byte i = 0; i < VehicleNames.Length; i++)
            {
                string vehName = VehicleNames[i];
                GameObject vehGo = FindVehicleGameObject(vehName);

                var tracked = new TrackedVehicle
                {
                    Id = i,
                    Name = vehName,
                    GameObject = vehGo
                };

                if (vehGo != null)
                {
                    tracked.Transform = vehGo.transform;
                    tracked.Rigidbody = vehGo.GetComponent<Rigidbody>();
                    tracked.AllRigidbodies = vehGo.GetComponentsInChildren<Rigidbody>(true);
                    CacheManagedRigidbodies(tracked);
                    HookDrivingFsm(tracked);

                    if (vehName.StartsWith("JONNEZ"))
                    {
                        var fork = vehGo.transform.Find("LOD/Suspension/Steering") ??
                                   vehGo.transform.Find("Suspension/Steering") ??
                                   vehGo.transform.Find("Steering") ??
                                   vehGo.transform.Find("Fork") ??
                                   vehGo.transform.Find("Pivot/Handlebars");
                        tracked.JonnezSteeringFork = fork;
                        HookJonnezCrashFsms(tracked);
                    }
                }

                vehicles.Add(tracked);
            }
        }

        private static void CacheManagedRigidbodies(TrackedVehicle tracked)
        {
            if (tracked == null) return;
            tracked.ManagedRigidbodies.Clear();

            // Управляем ТОЛЬКО основным Rigidbody транспорта (и GFX Rigidbody для лодки BOAT),
            // не затрагивая двери (doorl/doorr/doorear), DriverHeadPivot, DeadBody, стекла, MassDriver/Passenger и FuelTank
            if (tracked.Rigidbody != null)
            {
                tracked.DefaultKinematic = tracked.Rigidbody.isKinematic;
                tracked.DefaultUseGravity = tracked.Rigidbody.useGravity;
                tracked.ManagedRigidbodies.Add(new ManagedRigidbodyState
                {
                    Rb = tracked.Rigidbody,
                    DefaultKinematic = tracked.Rigidbody.isKinematic,
                    DefaultUseGravity = tracked.Rigidbody.useGravity
                });
                Environment.PhysicsOptimizer.Instance?.OptimizeRigidbody(tracked.Rigidbody, false);
            }

            if (tracked.GameObject != null && tracked.Name.StartsWith("BOAT", StringComparison.OrdinalIgnoreCase))
            {
                Transform gfxTr = tracked.GameObject.transform.Find("GFX");
                if (gfxTr != null)
                {
                    Rigidbody gfxRb = gfxTr.GetComponent<Rigidbody>();
                    if (gfxRb != null && gfxRb != tracked.Rigidbody)
                    {
                        tracked.ManagedRigidbodies.Add(new ManagedRigidbodyState
                        {
                            Rb = gfxRb,
                            DefaultKinematic = gfxRb.isKinematic,
                            DefaultUseGravity = gfxRb.useGravity
                        });
                        Environment.PhysicsOptimizer.Instance?.OptimizeRigidbody(gfxRb, false);
                    }
                }
            }
        }

        private static GameObject FindVehicleGameObject(string vehName)
        {
            GameObject vehGo = GameObject.Find(vehName);
            if (vehGo != null) return vehGo;

            string[] fallbacks = null;
            if (vehName.StartsWith("JONNEZ")) fallbacks = new[] { "JONNEZ ES(Clone)", "JONNEZ ES", "Jonnez", "JONNEZ" };
            else if (vehName.StartsWith("FERNDALE")) fallbacks = new[] { "FERNDALE(1630kg)", "FERNDALE", "Ferndale" };
            else if (vehName.StartsWith("RCO_RUSCKO")) fallbacks = new[] { "RCO_RUSCKO12(270)", "RCO_RUSCKO12", "Ruscko", "RUSCKO" };
            else if (vehName.StartsWith("BOAT")) fallbacks = new[] { "BOAT", "Boat" };
            else if (vehName.StartsWith("HAYOSIKO")) fallbacks = new[] { "HAYOSIKO(1500kg, 250)", "Hayosiko", "HAYOSIKO" };
            else if (vehName.StartsWith("KEKMET")) fallbacks = new[] { "KEKMET(350-400psi)", "Kekmet", "KEKMET" };
            else if (vehName.StartsWith("GIFU")) fallbacks = new[] { "GIFU(750/450psi)", "Gifu", "GIFU" };
            else if (vehName.StartsWith("FLATBED")) fallbacks = new[] { "FLATBED", "Flatbed" };

            if (fallbacks != null)
            {
                for (int i = 0; i < fallbacks.Length; i++)
                {
                    vehGo = GameObject.Find(fallbacks[i]);
                    if (vehGo != null) return vehGo;
                }
            }

            return null;
        }

        private void HookDrivingFsm(TrackedVehicle veh)
        {
            if (veh.GameObject == null) return;

            // Находим DriverHeadPivot рекурсивно и нейтрализуем FSM Death, суставы и физику
            Transform dhp = null;
            var allTransforms = veh.GameObject.GetComponentsInChildren<Transform>(true);
            for (int t = 0; t < allTransforms.Length; t++)
            {
                if (allTransforms[t] != null && allTransforms[t].name == "DriverHeadPivot")
                {
                    dhp = allTransforms[t];
                    break;
                }
            }

            if (dhp != null)
            {
                var deathFsms = dhp.GetComponents<PlayMakerFSM>();
                for (int d = 0; d < deathFsms.Length; d++)
                {
                    var df = deathFsms[d];
                    if (df != null)
                    {
                        df.enabled = false;
                        try { UnityEngine.Object.Destroy(df); } catch { }
                    }
                }

                var joint = dhp.GetComponent<ConfigurableJoint>();
                if (joint == null && dhp.parent != null)
                {
                    joint = dhp.parent.GetComponentInChildren<ConfigurableJoint>();
                }
                if (joint != null)
                {
                    joint.breakForce = float.PositiveInfinity;
                    joint.breakTorque = float.PositiveInfinity;
                }
                var rb = dhp.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = true;
                }
            }

            PlayMakerFSM drivingFsm = null;
            var fsms = veh.GameObject.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                var fsm = fsms[i];
                if (fsm == null) continue;
                if (fsm.gameObject.name == "DriverHeadPivot" || fsm.FsmName == "Death")
                {
                    fsm.enabled = false;
                    try { UnityEngine.Object.Destroy(fsm); } catch { }
                    continue; // Блокируем Death FSM на голове!
                }

                if (fsm.gameObject.name == "PassengerTrigger" || fsm.gameObject.name.StartsWith("MPPlayerFunctions"))
                {
                    continue;
                }

                bool hasEnter = fsm.HasState("Reset view") || fsm.HasState("Player in car");
                bool hasExit = fsm.HasState("Create player");

                // Предпочитаем DriveTrigger; если нет — резервный поиск по (hasEnter && hasExit)
                if (fsm.gameObject.name == "DriveTrigger" && (hasEnter || hasExit))
                {
                    drivingFsm = fsm;
                    break;
                }
                if (drivingFsm == null && hasEnter && hasExit)
                {
                    drivingFsm = fsm;
                }
            }

            if (drivingFsm != null)
            {
                drivingFsm.Initialize();
                veh.DrivingModeFsm = drivingFsm;

                byte id = veh.Id;
                if (drivingFsm.HasState("Reset view"))
                {
                    drivingFsm.InsertAction("Reset view", () => { SendDrivingMode(id, true); }, 0, false);
                }
                else if (drivingFsm.HasState("Player in car"))
                {
                    drivingFsm.InsertAction("Player in car", () => { SendDrivingMode(id, true); }, 0, false);
                }

                if (drivingFsm.HasState("Create player"))
                {
                    drivingFsm.InsertAction("Create player", () => { SendDrivingMode(id, false); }, 0, false);
                }
            }
        }

        private void Update()
        {
            if (!isInitialized || !OurSummerCar.IsInGameWorld) return;

            // Периодическое обнаружение транспорта и камеры
            if (Time.time > nextDiscoveryTime)
            {
                nextDiscoveryTime = Time.time + 2.0f;
                DiscoverVehiclesAndPlayer();
            }

            float dt = Time.deltaTime;

            // Детект толкания руками (Hand Push)
            CheckHandPush();

            // Отключение взаимных коллизий коллайдеров мопеда с обоими игроками
            UpdatePlayerVehicleCollisions();

            // Блокировка воспроизведения звуков удара CarHit / Crash при скорости мопеда ниже 1.5 м/с
            UpdateJonnezCrashSounds();

            // Отключение коллизий с лобовыми стеклами, куклами игроков и головой во время вождения
            UpdateSeatedCollisions();

            // Плавная интерполяция движения транспорта при удаленном вождении
            UpdateRemotelyDrivenInterpolation(dt);

            // Таймеры авторитета
            for (int i = 0; i < vehicles.Count; i++)
            {
                var veh = vehicles[i];
                if (veh.LocalPushTimer > 0f)
                {
                    veh.LocalPushTimer -= dt;
                    if (veh.LocalPushTimer <= 0f)
                    {
                        veh.IsBeingPushedLocally = false;
                        // Отправляем финальный пакет фиксации позиции
                        SendPhysicsSync(veh, true);
                    }
                }

                if (veh.RemoteAuthorityTimer > 0f)
                {
                    veh.RemoteAuthorityTimer -= dt;
                    if (veh.RemoteAuthorityTimer <= 0f)
                    {
                        veh.IsBeingCarriedRemotely = false;
                        veh.RemoteCarrierId = -1;
                    }
                }
            }

            // Сетевой стриминг физики (20 Гц для активных машин, 1 Гц для покоя хоста)
            SyncVehiclesNetwork();
        }

        private void DiscoverVehiclesAndPlayer()
        {
            if (localPlayerTransform == null)
            {
                if (Camera.main != null)
                {
                    localPlayerTransform = Camera.main.transform;
                }
                else
                {
                    var player = GameObject.Find("PLAYER");
                    if (player != null) localPlayerTransform = player.transform;
                }
            }

            if (handPushObject == null)
            {
                var player = GameObject.Find("PLAYER") ?? GameObject.FindWithTag("Player");
                if (player != null)
                {
                    var hp = player.transform.Find("Pivot/AnimPivot/Camera/FPSCamera/Hand Push") ??
                             player.transform.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/Hand Push");
                    if (hp != null) handPushObject = hp.gameObject;
                }
                if (handPushObject == null)
                {
                    handPushObject = GameObject.Find("Hand Push");
                }
            }

            for (int i = 0; i < vehicles.Count; i++)
            {
                var veh = vehicles[i];
                if (veh.GameObject == null)
                {
                    GameObject found = FindVehicleGameObject(veh.Name);

                    if (found != null)
                    {
                        veh.GameObject = found;
                        veh.Transform = found.transform;
                        veh.Rigidbody = found.GetComponent<Rigidbody>();
                        veh.AllRigidbodies = found.GetComponentsInChildren<Rigidbody>(true);
                        CacheManagedRigidbodies(veh);
                        HookDrivingFsm(veh);

                        if (veh.Name.StartsWith("JONNEZ"))
                        {
                            var fork = found.transform.Find("LOD/Suspension/Steering") ??
                                       found.transform.Find("Suspension/Steering") ??
                                       found.transform.Find("Steering") ??
                                       found.transform.Find("Fork") ??
                                       found.transform.Find("Pivot/Handlebars");
                            veh.JonnezSteeringFork = fork;
                            HookJonnezCrashFsms(veh);
                        }
                    }
                }
            }
        }

        private void CheckHandPush()
        {
            if (handPushObject == null || localPlayerTransform == null) return;

            bool isPushMode = handPushObject.activeInHierarchy;
            if (!isPushMode) return;

            Vector3 playerPos = localPlayerTransform.position;
            Transform camTr = (Camera.main != null) ? Camera.main.transform : localPlayerTransform;

            for (int i = 0; i < vehicles.Count; i++)
            {
                var veh = vehicles[i];
                if (veh.Transform == null || veh.Rigidbody == null) continue;

                float dist = Vector3.Distance(playerPos, veh.Transform.position);
                bool isNear = dist < 4.8f; // Дистанция до центра кузова (с запасом для длинных машин)

                // Когда игрок находится ближе 3.5м к Сацуме (ID 0) с активным режимом рук:
                if (veh.Id == 0 && dist < 3.5f)
                {
                    isNear = true;
                    if (veh.Rigidbody.isKinematic)
                    {
                        veh.SetKinematic(false);
                    }
                    veh.Rigidbody.WakeUp();
                }

                if (!isNear && camTr != null)
                {
                    // Проверяем лучом от камеры при взгляде на длинные машины (Gifu, Flatbed, Hayosiko)
                    if (Physics.Raycast(camTr.position, camTr.forward, out RaycastHit hit, 4.5f))
                    {
                        if (hit.collider != null && (hit.collider.transform == veh.Transform || hit.collider.transform.IsChildOf(veh.Transform)))
                        {
                            isNear = true;
                        }
                    }
                }

                if (isNear)
                {
                    if (veh.Rigidbody != null)
                    {
                        if (veh.Rigidbody.isKinematic)
                        {
                            veh.SetKinematic(false);
                        }
                        veh.Rigidbody.WakeUp();
                    }

                    if (veh.LocalPushTimer <= 0.5f)
                    {
                        veh.IsBeingPushedLocally = true;
                        veh.LocalPushTimer = 2.0f;
                        OnLog?.Invoke($"[Толкание] Захвачена физика машины {veh.Name}");
                    }
                    else
                    {
                        veh.LocalPushTimer = 2.0f;
                    }

                    // Передаем пакет с флагом isPushed = true на хост, чтобы хост не глушил движение в точку покоя
                    SendPhysicsSync(veh, false);
                }
            }
        }

        private void SyncVehiclesNetwork()
        {
            float now = Time.time;

            for (int i = 0; i < vehicles.Count; i++)
            {
                var veh = vehicles[i];
                if (veh.Transform == null || veh.Rigidbody == null) continue;

                bool shouldSend = false;
                bool isResting = veh.Rigidbody.velocity.sqrMagnitude < 0.0005f && veh.Rigidbody.angularVelocity.sqrMagnitude < 0.0005f;

                // Если машину толкают руками, она обязана бодрствовать и не спать
                if (veh.LocalPushTimer > 0f)
                {
                    isResting = false;
                    if (veh.Rigidbody.isKinematic)
                    {
                        veh.SetKinematic(false);
                    }
                    veh.Rigidbody.WakeUp();
                }

                // 0. Локальный игрок несет транспорт в руках: 20 Гц
                if (veh.IsBeingCarriedLocally)
                {
                    if (now - veh.LastSendTime >= 0.05f)
                    {
                        shouldSend = true;
                        isResting = false;
                    }
                }
                // 1. Локальный игрок за рулем: 20 Гц
                else if (veh.IsBeingDrivenLocally)
                {
                    if (now - veh.LastSendTime >= 0.05f)
                    {
                        shouldSend = true;
                    }
                }
                // 2. Локальный игрок толкает руками: 20-30 Гц
                else if (veh.LocalPushTimer > 0f)
                {
                    isResting = false;
                    if (now - veh.LastSendTime >= 0.033f)
                    {
                        shouldSend = true;
                    }
                }
                // 3. Хост авторитарен для незанятого транспорта
                else if (transport.IsServer && veh.RemoteAuthorityTimer <= 0f && !veh.IsBeingDrivenRemotely && !veh.IsBeingCarriedRemotely)
                {
                    // Если движется: 20 Гц
                    if (!isResting)
                    {
                        if (now - veh.LastSendTime >= 0.05f)
                        {
                            shouldSend = true;
                        }
                    }
                    // Если в покое: 1 Гц heartbeat для идеального выравнивания
                    else
                    {
                        if (now - veh.LastSendTime >= 1.5f)
                        {
                            shouldSend = true;
                        }
                    }
                }

                if (shouldSend)
                {
                    SendPhysicsSync(veh, isResting);
                }
            }
        }

        private void SendPhysicsSync(TrackedVehicle veh, bool isResting)
        {
            veh.LastSendTime = Time.time;
            veh.LastSentPosition = veh.Transform.position;
            veh.LastSentRotation = veh.Transform.rotation;

            byte flags = 0;
            if (veh.IsBeingDrivenLocally) flags |= 1;
            if (veh.LocalPushTimer > 0f) flags |= 2;
            if (isResting) flags |= 4;
            if (veh.IsBeingCarriedLocally) flags |= 8;

            using (var packet = new PacketWriter(PacketType.VehiclePhysicsSync))
            {
                packet.WriteByte(veh.Id);
                packet.WriteByte(flags);

                Vector3 pos = veh.Transform.position;
                Quaternion rot = veh.Transform.rotation;
                Vector3 vel = veh.Rigidbody.velocity;
                Vector3 angVel = veh.Rigidbody.angularVelocity;

                packet.WriteSingle(pos.x); packet.WriteSingle(pos.y); packet.WriteSingle(pos.z);
                packet.WriteSingle(rot.x); packet.WriteSingle(rot.y); packet.WriteSingle(rot.z); packet.WriteSingle(rot.w);
                packet.WriteSingle(vel.x); packet.WriteSingle(vel.y); packet.WriteSingle(vel.z);
                packet.WriteSingle(angVel.x); packet.WriteSingle(angVel.y); packet.WriteSingle(angVel.z);

                if (veh.Id == 6 && veh.JonnezSteeringFork != null)
                {
                    packet.WriteSingle(veh.JonnezSteeringFork.localEulerAngles.y);
                }

                transport.Send(packet);
            }
        }

        public void SendDrivingMode(byte vehId, bool entering)
        {
            var veh = GetVehicle(vehId);
            if (veh != null)
            {
                veh.IsBeingDrivenLocally = entering;
                if (entering)
                {
                    veh.IsBeingDrivenRemotely = false;
                    veh.RemoteDriverId = -1;
                    veh.RemoteAuthorityTimer = 0f;
                    veh.SetKinematic(false);
                    if (veh.Rigidbody != null)
                    {
                        veh.Rigidbody.isKinematic = false;
                        veh.Rigidbody.useGravity = veh.DefaultUseGravity;
                        veh.Rigidbody.WakeUp();
                    }
                    veh.WakeUpManaged();
                    OnLog?.Invoke($"[Транспорт] Сел за руль {veh.Name}");
                }
                else
                {
                    veh.SetKinematic(false);
                    veh.WakeUpManaged();
                    OnLog?.Invoke($"[Транспорт] Вышел из-за руля {veh.Name}");
                }
            }

            SetDrivingCollisions(veh, entering);

            int myId = transport.IsServer ? 0 : 1;
            using (var packet = new PacketWriter(PacketType.VehicleDrivingMode))
            {
                packet.WriteByte(vehId);
                packet.WriteBoolean(entering);
                packet.WriteInt32(myId);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.VehiclePhysicsSync)
            {
                HandleVehiclePhysicsSync(reader, connectionId);
            }
            else if (reader.Type == PacketType.VehicleDrivingMode)
            {
                HandleVehicleDrivingMode(reader, connectionId);
            }
        }

        private void HandleVehiclePhysicsSync(PacketReader reader, int connectionId)
        {
            byte vehId = reader.ReadByte();
            byte flags = reader.ReadByte();

            float px = reader.ReadSingle(); float py = reader.ReadSingle(); float pz = reader.ReadSingle();
            float rx = reader.ReadSingle(); float ry = reader.ReadSingle(); float rz = reader.ReadSingle(); float rw = reader.ReadSingle();
            float vx = reader.ReadSingle(); float vy = reader.ReadSingle(); float vz = reader.ReadSingle();
            float ax = reader.ReadSingle(); float ay = reader.ReadSingle(); float az = reader.ReadSingle();

            float forkRotY = 0f;
            bool hasFork = false;
            if (vehId == 6 && reader.UnreadBytes >= 4)
            {
                forkRotY = reader.ReadSingle();
                hasFork = true;
            }

            // Серверный релей
            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.VehiclePhysicsSync))
                {
                    relay.WriteByte(vehId);
                    relay.WriteByte(flags);
                    relay.WriteSingle(px); relay.WriteSingle(py); relay.WriteSingle(pz);
                    relay.WriteSingle(rx); relay.WriteSingle(ry); relay.WriteSingle(rz); relay.WriteSingle(rw);
                    relay.WriteSingle(vx); relay.WriteSingle(vy); relay.WriteSingle(vz);
                    relay.WriteSingle(ax); relay.WriteSingle(ay); relay.WriteSingle(az);
                    if (hasFork) relay.WriteSingle(forkRotY);
                    transport.Send(relay, connectionId);
                }
            }

            var veh = GetVehicle(vehId);
            if (veh == null || veh.Transform == null || veh.Rigidbody == null) return;

            // Если локальный игрок сам за рулем, толкает или держит в руках — не перезаписываем
            if (veh.IsBeingDrivenLocally || veh.LocalPushTimer > 0f || veh.IsBeingCarriedLocally) return;

            // Если транспорт удерживается удаленным игроком в руках (привязан к ItemHolder куклы) — не сбиваем мировую позицию
            if (veh.IsBeingCarriedRemotely) return;

            bool isRemotePushed = (flags & 2) != 0;
            bool isRemoteDriving = (flags & 1) != 0;
            bool isRestingPacket = (flags & 4) != 0;
            bool isRemoteCarried = (flags & 8) != 0;

            if (isRemotePushed || isRemoteDriving || isRemoteCarried)
            {
                veh.RemoteAuthorityTimer = 2.5f;
                if (isRemoteCarried)
                {
                    veh.IsBeingCarriedRemotely = true;
                }
            }

            Vector3 pos = new Vector3(px, py, pz);
            Quaternion rot = new Quaternion(rx, ry, rz, rw);
            Vector3 vel = new Vector3(vx, vy, vz);
            Vector3 angVel = new Vector3(ax, ay, az);

            veh.TargetPosition = pos;
            veh.TargetRotation = rot;
            veh.TargetVelocity = vel;
            veh.TargetAngularVelocity = angVel;

            if (isRemoteDriving)
            {
                veh.IsBeingDrivenRemotely = true;
                veh.SetKinematic(true);

                // При большом отставании (телепортация) выравниваем сразу
                if ((veh.Transform.position - pos).sqrMagnitude > 25f)
                {
                    veh.Transform.position = pos;
                    veh.Transform.rotation = rot;
                }
            }
            else
            {
                if (!veh.IsBeingDrivenRemotely && veh.Rigidbody != null && veh.Rigidbody.isKinematic && !veh.IsBeingCarriedLocally && !veh.IsBeingCarriedRemotely)
                {
                    veh.SetKinematic(false);
                }

                // Применяем позицию и ориентацию
                veh.Transform.position = pos;
                veh.Transform.rotation = rot;
                if (!veh.Rigidbody.isKinematic)
                {
                    veh.Rigidbody.velocity = vel;
                    veh.Rigidbody.angularVelocity = angVel;

                    if (isRestingPacket && veh.LocalPushTimer <= 0f)
                    {
                        veh.Rigidbody.velocity = Vector3.zero;
                        veh.Rigidbody.angularVelocity = Vector3.zero;
                        veh.Rigidbody.Sleep();
                    }
                }
            }

            // Для мопеда при передаче физики клиенту не трогаем вращение передней вилки через сетевой пакет:
            // клиент локально рулит вилкой, а по сети передается только вращение основной рамы JONNEZ ES(Clone)
        }

        private void HandleVehicleDrivingMode(PacketReader reader, int connectionId)
        {
            byte vehId = reader.ReadByte();
            bool entering = reader.ReadBoolean();
            int playerId = reader.ReadInt32();

            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.VehicleDrivingMode))
                {
                    relay.WriteByte(vehId);
                    relay.WriteBoolean(entering);
                    relay.WriteInt32(playerId);
                    transport.Send(relay, connectionId);
                }
            }

            var veh = GetVehicle(vehId);
            if (veh != null)
            {
                // Пока локальный игрок за рулем — игнорируем удаленные пакеты для этого транспорта
                if (veh.IsBeingDrivenLocally) return;

                veh.IsBeingDrivenRemotely = entering;
                veh.RemoteDriverId = entering ? playerId : -1;
                if (entering)
                {
                    veh.RemoteAuthorityTimer = 5.0f;
                    string who = (playerId == 0) ? "Хост" : $"Игрок #{playerId}";
                    OnLog?.Invoke($"[Транспорт] {who} сел за руль {veh.Name}");
                    veh.SetKinematic(true);
                }
                else
                {
                    veh.RemoteAuthorityTimer = 2.0f;
                    string who = (playerId == 0) ? "Хост" : $"Игрок #{playerId}";
                    OnLog?.Invoke($"[Транспорт] {who} вышел из-за руля {veh.Name}");

                    veh.SetKinematic(false);
                    veh.WakeUpManaged();
                }
            }
        }

        public TrackedVehicle GetVehicle(byte id)
        {
            if (id >= 0 && id < vehicles.Count) return vehicles[id];
            return null;
        }

        public TrackedVehicle GetVehicleByName(string name)
        {
            for (int i = 0; i < vehicles.Count; i++)
            {
                if (vehicles[i].Name == name) return vehicles[i];
            }
            return null;
        }

        public TrackedVehicle GetVehicleByGameObject(GameObject go)
        {
            if (go == null) return null;
            GameObject rootGo = go.transform.root.gameObject;
            for (int i = 0; i < vehicles.Count; i++)
            {
                if (vehicles[i].GameObject == go || vehicles[i].GameObject == rootGo)
                    return vehicles[i];
            }
            for (int i = 0; i < vehicles.Count; i++)
            {
                if (vehicles[i].GameObject != null && vehicles[i].GameObject.name == rootGo.name)
                    return vehicles[i];
            }
            return null;
        }

        public void SetVehicleCarriedLocally(GameObject go, bool carried)
        {
            var veh = GetVehicleByGameObject(go);
            if (veh != null)
            {
                veh.IsBeingCarriedLocally = carried;
                veh.SetKinematic(carried);
                if (carried)
                {
                    veh.RemoteAuthorityTimer = 0f;
                }
                else
                {
                    veh.LocalPushTimer = 2.5f;
                    veh.Rigidbody?.WakeUp();
                }
            }
        }

        public void SetVehicleCarriedRemotely(GameObject go, bool carried, int playerId)
        {
            var veh = GetVehicleByGameObject(go);
            if (veh != null)
            {
                veh.IsBeingCarriedRemotely = carried;
                veh.RemoteCarrierId = carried ? playerId : -1;
                veh.SetKinematic(carried);
                if (carried)
                {
                    veh.RemoteAuthorityTimer = 5.0f;
                }
                else
                {
                    veh.RemoteAuthorityTimer = 3.0f;
                    veh.Rigidbody?.WakeUp();
                }
            }
        }

        private void UpdateRemotelyDrivenInterpolation(float dt)
        {
            float lerpSpeed = dt * 25f;
            for (int i = 0; i < vehicles.Count; i++)
            {
                var veh = vehicles[i];
                if (veh.Transform == null || veh.Rigidbody == null) continue;

                if (veh.IsBeingDrivenRemotely && veh.TargetPosition != Vector3.zero)
                {
                    float distSq = (veh.Transform.position - veh.TargetPosition).sqrMagnitude;
                    if (distSq > 25f)
                    {
                        veh.Transform.position = veh.TargetPosition;
                        veh.Transform.rotation = veh.TargetRotation;
                    }
                    else
                    {
                        veh.Transform.position = Vector3.Lerp(veh.Transform.position, veh.TargetPosition, lerpSpeed);

                        Quaternion targetRot = veh.TargetRotation;
                        if (veh.Id == 6) // Jonnez ES
                        {
                            // Ограничиваем предельный угол завала (roll/tilt) мопеда при поворотах во время езды,
                            // чтобы мопед не ложился на бок под 60-90 градусов
                            Vector3 e = targetRot.eulerAngles;
                            float roll = e.z;
                            if (roll > 180f) roll -= 360f;
                            if (Mathf.Abs(roll) > 35f)
                            {
                                roll = Mathf.Clamp(roll, -35f, 35f);
                                targetRot = Quaternion.Euler(e.x, e.y, roll);
                            }
                        }

                        veh.Transform.rotation = Quaternion.Slerp(veh.Transform.rotation, targetRot, lerpSpeed);
                    }
                }
            }
        }

        private void UpdatePlayerVehicleCollisions()
        {
            var jonnez = GetVehicle(6);
            if (jonnez == null || jonnez.GameObject == null) return;

            var jCols = jonnez.GameObject.GetComponentsInChildren<Collider>(true);
            if (jCols == null || jCols.Length == 0) return;

            // 1. Отключение коллизий с локальным игроком (однократно)
            if (!isLocalPlayerJonnezCollisionIgnored)
            {
                var player = GameObject.Find("PLAYER") ?? GameObject.FindWithTag("Player");
                if (player != null)
                {
                    var pCols = player.GetComponentsInChildren<Collider>(true);
                    if (pCols != null && pCols.Length > 0)
                    {
                        for (int j = 0; j < jCols.Length; j++)
                        {
                            var jc = jCols[j];
                            if (jc == null) continue;

                            for (int p = 0; p < pCols.Length; p++)
                            {
                                var pc = pCols[p];
                                if (pc != null && pc.enabled && jc.enabled)
                                {
                                    try { Physics.IgnoreCollision(pc, jc, true); } catch { }
                                }
                            }
                        }
                        isLocalPlayerJonnezCollisionIgnored = true;
                    }
                }
            }

            // 2. Отключение коллизий с удаленными игроками (повторно при появлении новых кукол)
            if (PlayerManager.Instance?.AllPuppets != null)
            {
                foreach (var puppet in PlayerManager.Instance.AllPuppets)
                {
                    if (puppet == null || puppet.RootObject == null) continue;
                    int rootId = puppet.RootObject.GetInstanceID();
                    if (ignoredJonnezPuppetIds.Contains(rootId)) continue;

                    var pupCols = puppet.RootObject.GetComponentsInChildren<Collider>(true);
                    if (pupCols != null && pupCols.Length > 0)
                    {
                        for (int j = 0; j < jCols.Length; j++)
                        {
                            var jc = jCols[j];
                            if (jc == null) continue;

                            for (int pc = 0; pc < pupCols.Length; pc++)
                            {
                                var pupc = pupCols[pc];
                                if (pupc != null && pupc.enabled && jc.enabled)
                                {
                                    try { Physics.IgnoreCollision(pupc, jc, true); } catch { }
                                }
                            }
                        }
                        ignoredJonnezPuppetIds.Add(rootId);
                    }
                }
            }
        }

        private void UpdateJonnezCrashSounds()
        {
            var jonnez = GetVehicle(6);
            if (jonnez == null || jonnez.GameObject == null || jonnez.Rigidbody == null) return;
            if (jonnez.JonnezCrashAudioSources == null) return;

            float speed = jonnez.Rigidbody.velocity.magnitude;
            bool shouldMute = speed < 1.5f;

            for (int i = 0; i < jonnez.JonnezCrashAudioSources.Length; i++)
            {
                var src = jonnez.JonnezCrashAudioSources[i];
                if (src == null) continue;

                if (shouldMute)
                {
                    if (src.isPlaying)
                    {
                        src.Stop();
                    }
                    src.mute = true;
                }
                else
                {
                    src.mute = false;
                }
            }
        }

        private void HookJonnezCrashFsms(TrackedVehicle tracked)
        {
            if (tracked == null || tracked.GameObject == null) return;

            // Кэшируем компоненты звуков удара один раз
            var sources = tracked.GameObject.GetComponentsInChildren<AudioSource>(true);
            var crashList = new List<AudioSource>();
            for (int i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                if (src == null) continue;

                string clipName = src.clip != null ? src.clip.name.ToLower() : "";
                string goName = src.gameObject.name.ToLower();

                bool isCrashAudio = clipName.Contains("hit") || clipName.Contains("crash") || 
                                    goName.Contains("hit") || goName.Contains("crash") || 
                                    clipName.Contains("carhit") || goName.Contains("carhit");

                if (isCrashAudio)
                {
                    crashList.Add(src);
                }
            }
            tracked.JonnezCrashAudioSources = crashList.ToArray();

            var fsms = tracked.GameObject.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                var fsm = fsms[i];
                if (fsm == null) continue;
                if (fsm.HasState("Crash"))
                {
                    fsm.InsertAction("Crash", () =>
                    {
                        if (tracked.Rigidbody != null && tracked.Rigidbody.velocity.magnitude < 1.5f)
                        {
                            fsm.SendEvent("FINISHED");
                        }
                    }, 0, false);
                }
                if (fsm.HasState("Car hit"))
                {
                    fsm.InsertAction("Car hit", () =>
                    {
                        if (tracked.Rigidbody != null && tracked.Rigidbody.velocity.magnitude < 1.5f)
                        {
                            fsm.SendEvent("FINISHED");
                        }
                    }, 0, false);
                }
            }
        }

        private void SetDrivingCollisions(TrackedVehicle veh, bool entering)
        {
            try
            {
                var player = GameObject.Find("PLAYER") ?? GameObject.FindWithTag("Player");
                if (player == null) return;

                var playerColliders = player.GetComponentsInChildren<Collider>(true);
                if (playerColliders == null || playerColliders.Length == 0) return;

                if (entering)
                {
                    // 1. Находим DriverHeadPivot и стекла/коллайдеры окон на объекте машины
                    if (veh != null && veh.GameObject != null)
                    {
                        var vehColliders = veh.GameObject.GetComponentsInChildren<Collider>(true);
                        for (int i = 0; i < vehColliders.Length; i++)
                        {
                            var c = vehColliders[i];
                            if (c == null) continue;

                            string name = c.gameObject.name.ToLower();
                            bool isWindshield = name.Contains("windshield") || 
                                                name.Contains("windscreen") || 
                                                name.Contains("glass") || 
                                                name.Contains("window") ||
                                                c.gameObject.name == "DriverHeadPivot";

                            if (!isWindshield && c is MeshCollider mc && mc.sharedMesh != null)
                            {
                                string meshName = mc.sharedMesh.name.ToLower();
                                if (meshName.Contains("windshield") || meshName.Contains("windscreen") || meshName.Contains("glass") || meshName.Contains("window"))
                                {
                                    isWindshield = true;
                                }
                            }

                            if (isWindshield)
                            {
                                if (!ignoredVehicleColliders.Contains(c))
                                    ignoredVehicleColliders.Add(c);

                                for (int p = 0; p < playerColliders.Length; p++)
                                {
                                    if (playerColliders[p] != null && playerColliders[p].enabled && c.enabled)
                                    {
                                        Physics.IgnoreCollision(playerColliders[p], c, true);
                                    }
                                }
                            }
                        }
                    }

                    // 2. Отключаем коллизию между телом игрока и куклами удаленных игроков
                    IgnorePuppetCollisions(playerColliders);
                    isSeatedCollisionsIgnored = true;
                }
                else
                {
                    isSeatedCollisionsIgnored = false;
                    // Восстанавливаем коллизии при выходе
                    for (int i = 0; i < ignoredVehicleColliders.Count; i++)
                    {
                        var c = ignoredVehicleColliders[i];
                        if (c == null) continue;

                        for (int p = 0; p < playerColliders.Length; p++)
                        {
                            if (playerColliders[p] != null)
                            {
                                try { Physics.IgnoreCollision(playerColliders[p], c, false); } catch { }
                            }
                        }
                    }
                    ignoredVehicleColliders.Clear();
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Транспорт] Ошибка переключения коллизий посадки: {ex.Message}");
            }
        }

        private void IgnorePuppetCollisions(Collider[] playerColliders)
        {
            if (PlayerManager.Instance?.AllPuppets == null) return;

            foreach (var puppet in PlayerManager.Instance.AllPuppets)
            {
                if (puppet == null || puppet.RootObject == null) continue;
                var puppetColliders = puppet.RootObject.GetComponentsInChildren<Collider>(true);
                for (int pc = 0; pc < puppetColliders.Length; pc++)
                {
                    var c = puppetColliders[pc];
                    if (c == null) continue;

                    if (!ignoredVehicleColliders.Contains(c))
                        ignoredVehicleColliders.Add(c);

                    for (int p = 0; p < playerColliders.Length; p++)
                    {
                        if (playerColliders[p] != null && playerColliders[p].enabled && c.enabled)
                        {
                            try { Physics.IgnoreCollision(playerColliders[p], c, true); } catch { }
                        }
                    }
                }
            }
        }

        private void UpdateSeatedCollisions()
        {
            if (!IsLocalPlayerDriving())
            {
                isSeatedCollisionsIgnored = false;
                return;
            }

            if (isSeatedCollisionsIgnored) return;

            var player = GameObject.Find("PLAYER") ?? GameObject.FindWithTag("Player");
            if (player != null)
            {
                var pCols = player.GetComponentsInChildren<Collider>(true);
                if (pCols != null && pCols.Length > 0)
                {
                    IgnorePuppetCollisions(pCols);
                    isSeatedCollisionsIgnored = true;
                }
            }
        }

        public bool IsLocalPlayerDriving()
        {
            for (int i = 0; i < vehicles.Count; i++)
            {
                if (vehicles[i].IsBeingDrivenLocally) return true;
            }
            return false;
        }

        public TrackedVehicle GetLocalDrivenVehicle()
        {
            for (int i = 0; i < vehicles.Count; i++)
            {
                if (vehicles[i].IsBeingDrivenLocally) return vehicles[i];
            }
            return null;
        }

        public TrackedVehicle GetClosestVehicle(Vector3 pos, float maxDist = 5f)
        {
            TrackedVehicle best = null;
            float bestDistSq = maxDist * maxDist;
            for (int i = 0; i < vehicles.Count; i++)
            {
                if (vehicles[i].Transform == null) continue;
                float distSq = (vehicles[i].Transform.position - pos).sqrMagnitude;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = vehicles[i];
                }
            }
            return best;
        }

        public bool IsPlayerDrivingAnyVehicle(int playerId, out TrackedVehicle drivingVeh)
        {
            drivingVeh = null;
            for (int i = 0; i < vehicles.Count; i++)
            {
                if (vehicles[i].IsBeingDrivenRemotely && vehicles[i].RemoteDriverId == playerId)
                {
                    drivingVeh = vehicles[i];
                    return true;
                }
            }
            return false;
        }
    }
}
