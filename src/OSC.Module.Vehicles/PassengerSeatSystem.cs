using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OSC.Module.Vehicles
{
    /// <summary>
    /// Кастомный нуль-коллайдерный триггер посадки на пассажирское место или скамью.
    /// Не добавляет физических коллайдеров на кузов, не искажает CarDynamics и не перехватывает клики по дверям/приборам.
    /// </summary>
    public sealed class PassengerTrigger : MonoBehaviour
    {
        public PassengerSeatRuntime Seat { get; private set; }
        public Vector3 BoxSize { get; private set; }

        public void Configure(PassengerSeatRuntime seat, Vector3 boxSize)
        {
            Seat = seat;
            BoxSize = boxSize;
        }

        public bool TryRayHit(Ray worldRay, float maxDistance, out float worldDistance)
        {
            worldDistance = float.MaxValue;
            if (Seat == null || Seat.Vehicle == null || !Seat.Vehicle.IsBound) return false;

            Vector3 localOrigin = transform.InverseTransformPoint(worldRay.origin);
            Vector3 localDir = transform.InverseTransformDirection(worldRay.direction);
            float dirMag = localDir.magnitude;
            if (dirMag < 0.0001f) return false;
            localDir /= dirMag;

            var localBounds = new Bounds(Vector3.zero, BoxSize);
            var localRay = new Ray(localOrigin, localDir);
            if (!localBounds.IntersectRay(localRay, out float localDist))
            {
                return false;
            }

            Vector3 worldHit = transform.TransformPoint(localOrigin + localDir * localDist);
            worldDistance = Vector3.Distance(worldRay.origin, worldHit);
            return worldDistance >= 0f && worldDistance <= maxDistance;
        }
    }

    public sealed class PassengerSeatSystem
    {
        private const float MaxInteractDistance = 2.25f;
        private const float ToggleCooldownSeconds = 0.25f;

        private readonly List<PassengerTrigger> triggers = new List<PassengerTrigger>();
        private readonly List<Behaviour> disabledBehaviours = new List<Behaviour>();
        private readonly Dictionary<AxisCarController, bool> suppressedCarControllers = new Dictionary<AxisCarController, bool>();

        private CollisionAndExitGuard collisionGuard;
        private Action<string> log;

        private Transform playerTr;
        private CharacterController playerCc;
        private Transform pivotTr;
        private Transform animPivotTr;
        private Transform camNodeTr;
        private Transform fpsCamTr;
        private Transform mainCamChildTr;
        private PlayMakerFSM crouchFsm;
        private bool crouchFsmWasEnabled;

        private FsmString guiInteractionVar;
        private FsmBool guiUseVar;
        private FsmBool playerStopVar;
        private bool showingPrompt;

        private PassengerSeatRuntime activeLocalSeat;
        private float seatYaw;
        private float seatPitch;
        private float seatLeanRoll;
        private float nextToggleAllowedTime;
        private Vector3 savedFpsCamLocalPos = new Vector3(0f, 1.4f, 0f);
        private bool hasSavedFpsCamPos;

        public bool IsLocalSeated => activeLocalSeat != null;
        public PassengerSeatRuntime ActiveLocalSeat => activeLocalSeat;

        public event Action<PassengerSeatRuntime, bool> OnLocalPassengerStateChanged;

        public void Initialize(CollisionAndExitGuard guard, Action<string> logger)
        {
            collisionGuard = guard;
            log = logger;
        }

        public void Reset()
        {
            if (activeLocalSeat != null)
            {
                ForceExitLocal(teleportToSafeExit: false);
            }
            ClearPrompt();
            triggers.Clear();
            disabledBehaviours.Clear();
            suppressedCarControllers.Clear();
            playerTr = null;
            playerCc = null;
            pivotTr = null;
            animPivotTr = null;
            camNodeTr = null;
            fpsCamTr = null;
            mainCamChildTr = null;
            crouchFsm = null;
            guiInteractionVar = null;
            guiUseVar = null;
            playerStopVar = null;
            showingPrompt = false;
            hasSavedFpsCamPos = false;
        }

        public void EnsureSeatsForVehicle(TrackedVehicle veh)
        {
            if (veh == null || !veh.IsBound || veh.Spec.PassengerSeats == null) return;
            if (veh.PassengerSeats.Count > 0) return;

            for (int i = 0; i < veh.Spec.PassengerSeats.Length; i++)
            {
                PassengerSeatSpec spec = veh.Spec.PassengerSeats[i];

                // Очистка на случай переинициализации на той же сцене
                string seatName = $"OSC_PassengerSeat_{spec.SeatIndex}";
                Transform existing = veh.Transform.Find(seatName);
                if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);

                var seatRoot = new GameObject(seatName);
                seatRoot.transform.SetParent(veh.Transform, false);
                seatRoot.transform.localPosition = spec.TriggerLocalCenter;
                seatRoot.transform.localRotation = Quaternion.identity;
                seatRoot.transform.localScale = Vector3.one;

                // Выравниваем высоту переднего пассажира по откалиброванному DriverHeadPivot, если он есть у авто
                Vector3 headLocal = spec.HeadPivotLocalPos;
                if (spec.SeatIndex == 0 && veh.Spec.HasDriverSeat && veh.Kind != VehicleKind.Boat && veh.Kind != VehicleKind.Kekmet)
                {
                    if (Mathf.Abs(veh.DriverHeadLocalPos.y - spec.HeadPivotLocalPos.y) < 0.25f)
                    {
                        headLocal.y = veh.DriverHeadLocalPos.y;
                    }
                }

                var headPivotGo = new GameObject("SeatHeadPivot");
                headPivotGo.transform.SetParent(veh.Transform, false);
                headPivotGo.transform.localPosition = headLocal;
                headPivotGo.transform.localRotation = Quaternion.Euler(0f, spec.SeatLocalYaw, 0f);
                headPivotGo.transform.localScale = Vector3.one;

                Quaternion puppetRot = Quaternion.Euler(0f, spec.SeatLocalYaw, 0f);
                Vector3 puppetOffset = spec.HasExplicitPuppetOffset
                    ? spec.ExplicitPuppetLocalOffset
                    : TrackedVehicle.ComputePuppetOffsetFromHead(headLocal, puppetRot);

                var runtime = new PassengerSeatRuntime
                {
                    Vehicle = veh,
                    Spec = spec,
                    SeatRootObject = seatRoot,
                    HeadPivotTransform = headPivotGo.transform,
                    CalibratedHeadLocalPos = headLocal,
                    CalibratedPuppetLocalOffset = puppetOffset,
                    CalibratedPuppetLocalRotation = puppetRot
                };

                var trigger = seatRoot.AddComponent<PassengerTrigger>();
                trigger.Configure(runtime, spec.TriggerBoxSize);
                runtime.TriggerComponent = trigger;

                veh.PassengerSeats.Add(runtime);
                triggers.Add(trigger);
            }

            log?.Invoke($"[Пассажиры] Создано мест ({veh.PassengerSeats.Count}) для {veh.GameObject.name}");
        }

        public void TickUpdate(bool isLocalDriver, IList<TrackedVehicle> allVehicles)
        {
            EnsurePlayerReferences();

            if (activeLocalSeat != null)
            {
                if (activeLocalSeat.Vehicle == null || !activeLocalSeat.Vehicle.IsBound || isLocalDriver)
                {
                    ForceExitLocal(teleportToSafeExit: true);
                    return;
                }

                UpdateSeatLookInput();
                SuppressVehicleControlsWhilePassenger(activeLocalSeat.Vehicle);

                if (Time.time >= nextToggleAllowedTime && !Cursor.visible && IsEnterButtonDown())
                {
                    ExitLocalSeat();
                }
                return;
            }

            // Не перехватываем Enter, если игрок уже за рулём или открыто меню/курсор
            if (isLocalDriver || Cursor.visible)
            {
                ClearPrompt();
                return;
            }

            UpdateLookAndEnterTrigger();
        }

        public void TickFixedUpdate()
        {
            if (activeLocalSeat == null || activeLocalSeat.HeadPivotTransform == null || playerTr == null) return;

            // Синхронизируем физическое положение узла PLAYER с транспортом на каждом физическом шаге
            playerTr.position = activeLocalSeat.HeadPivotTransform.position;
            SuppressVehicleControlsWhilePassenger(activeLocalSeat.Vehicle);
        }

        public void TickLateUpdate()
        {
            if (activeLocalSeat == null || activeLocalSeat.HeadPivotTransform == null) return;
            ApplyCameraLock();
        }

        private void EnsurePlayerReferences()
        {
            if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
            {
                if (guiInteractionVar == null) guiInteractionVar = PlayMakerGlobals.Instance.Variables.FindFsmString("GUIinteraction");
                if (guiUseVar == null) guiUseVar = PlayMakerGlobals.Instance.Variables.FindFsmBool("GUIuse");
                if (playerStopVar == null) playerStopVar = PlayMakerGlobals.Instance.Variables.FindFsmBool("PlayerStop");
            }

            if (playerTr != null && playerTr.gameObject.activeInHierarchy) return;

            GameObject playerObj = GameObject.Find("PLAYER") ?? GameObject.FindWithTag("Player");
            if (playerObj == null) return;

            playerTr = playerObj.transform;
            playerCc = playerObj.GetComponent<CharacterController>();
            pivotTr = playerTr.Find("Pivot");
            animPivotTr = playerTr.Find("Pivot/AnimPivot");
            camNodeTr = playerTr.Find("Pivot/AnimPivot/Camera");
            fpsCamTr = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera");
            mainCamChildTr = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera");

            var fsms = playerObj.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < fsms.Length; i++)
            {
                if (fsms[i] != null && fsms[i].FsmName == "Crouch")
                {
                    crouchFsm = fsms[i];
                    break;
                }
            }
        }

        private void UpdateLookAndEnterTrigger()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                ClearPrompt();
                return;
            }

            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            PassengerTrigger bestTrigger = null;
            float bestDist = MaxInteractDistance;

            for (int i = 0; i < triggers.Count; i++)
            {
                PassengerTrigger tr = triggers[i];
                if (tr == null || tr.Seat == null || tr.Seat.Vehicle == null || !tr.Seat.Vehicle.IsBound) continue;

                // Предварительная отсечка по расстоянию до центра сиденья
                if ((tr.transform.position - ray.origin).sqrMagnitude > 16f) continue;

                if (tr.TryRayHit(ray, bestDist, out float dist))
                {
                    if (!IsRayOccluded(ray, dist, tr.Seat.Vehicle.Transform))
                    {
                        bestDist = dist;
                        bestTrigger = tr;
                    }
                }
            }

            if (bestTrigger == null)
            {
                ClearPrompt();
                return;
            }

            PassengerSeatRuntime seat = bestTrigger.Seat;
            bool occupied = seat.RemoteOccupantId >= 0;

            if (guiInteractionVar != null)
            {
                guiInteractionVar.Value = occupied ? "SEAT TAKEN" : "ENTER PASSENGER MODE";
                showingPrompt = true;
            }
            if (guiUseVar != null)
            {
                guiUseVar.Value = !occupied;
            }

            if (!occupied && Time.time >= nextToggleAllowedTime && IsEnterButtonDown())
            {
                EnterLocalSeat(seat);
            }
        }

        private bool IsRayOccluded(Ray ray, float hitDistance, Transform vehicleRoot)
        {
            if (hitDistance <= 0.1f) return false;

            RaycastHit[] hits = Physics.RaycastAll(ray.origin, ray.direction, hitDistance - 0.06f);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null || col.isTrigger) continue;
                if (playerTr != null && (col.transform == playerTr || col.transform.IsChildOf(playerTr))) continue;

                // Не даём самому коллайдеру сидушки блокировать посадку в это сиденье
                string nameLower = col.gameObject.name.ToLowerInvariant();
                if (col.transform.IsChildOf(vehicleRoot) && nameLower.Contains("seat")) continue;

                return true;
            }
            return false;
        }

        private void EnterLocalSeat(PassengerSeatRuntime seat)
        {
            if (seat == null || seat.Vehicle == null || !seat.Vehicle.IsBound || playerTr == null) return;

            ClearPrompt();
            nextToggleAllowedTime = Time.time + ToggleCooldownSeconds;
            activeLocalSeat = seat;
            seat.IsOccupiedLocally = true;

            seatYaw = 0f;
            seatPitch = 0f;
            seatLeanRoll = 0f;

            // Включаем интерполяцию Rigidbody (если локальная физика активна), чтобы минимизировать микро-дрожание камеры
            if (!seat.Vehicle.IsDrivenRemotely)
            {
                seat.Vehicle.SetInterpolationActive(true);
            }

            // Отключаем коллизии игрока с кузовом автомобиля на время поездки
            collisionGuard?.SetLocalVehicleCollisionsIgnored(seat.Vehicle, playerTr, true);

            // Блокируем ходьбу, CharacterController, Crouch FSM и нативные MouseLook-скрипты
            if (playerCc != null) playerCc.enabled = false;
            if (playerStopVar != null) playerStopVar.Value = true;
            if (crouchFsm != null)
            {
                crouchFsmWasEnabled = crouchFsm.enabled;
                crouchFsm.enabled = false;
            }

            if (fpsCamTr != null)
            {
                savedFpsCamLocalPos = fpsCamTr.localPosition;
                hasSavedFpsCamPos = true;
            }

            disabledBehaviours.Clear();
            var behaviours = playerTr.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour mb = behaviours[i];
                if (mb == null || !mb.enabled) continue;
                string typeName = mb.GetType().Name;
                if (typeName == "MouseLook" || typeName == "SmoothMouseLook" || typeName == "SimpleSmoothMouseLook" ||
                    typeName == "CharacterMotor" || typeName == "FPSInputController" || typeName == "FirstPersonController")
                {
                    mb.enabled = false;
                    disabledBehaviours.Add(mb);
                }
            }

            SuppressVehicleControlsWhilePassenger(seat.Vehicle);
            ApplyCameraLock();

            OnLocalPassengerStateChanged?.Invoke(seat, true);
            log?.Invoke($"[Пассажиры] Локальный игрок сел на '{seat.Spec.DisplayName}' ({seat.Vehicle.GameObject.name})");
        }

        public void ExitLocalSeat()
        {
            if (activeLocalSeat == null) return;
            ForceExitLocal(teleportToSafeExit: true);
        }

        public void ForceExitLocal(bool teleportToSafeExit)
        {
            PassengerSeatRuntime exitingSeat = activeLocalSeat;
            if (exitingSeat == null) return;

            activeLocalSeat = null;
            exitingSeat.IsOccupiedLocally = false;
            nextToggleAllowedTime = Time.time + ToggleCooldownSeconds;

            ClearPrompt();
            RestoreSuppressedCarControllers();

            if (exitingSeat.Vehicle != null)
            {
                exitingSeat.Vehicle.SetInterpolationActive(false);
            }

            // Восстанавливаем компоненты игрока
            for (int i = 0; i < disabledBehaviours.Count; i++)
            {
                if (disabledBehaviours[i] != null)
                {
                    disabledBehaviours[i].enabled = true;
                }
            }
            disabledBehaviours.Clear();

            if (crouchFsm != null)
            {
                crouchFsm.enabled = crouchFsmWasEnabled;
            }
            if (playerStopVar != null)
            {
                playerStopVar.Value = false;
            }

            if (playerTr != null)
            {
                if (teleportToSafeExit && exitingSeat.Vehicle != null && exitingSeat.Vehicle.IsBound)
                {
                    Vector3 safePos = collisionGuard != null
                        ? collisionGuard.FindSafeExitPoint(exitingSeat.Vehicle, playerTr, exitingSeat.Spec.PreferredExitSide, playerTr.position)
                        : exitingSeat.Vehicle.Transform.position + Vector3.up * 2.0f;

                    playerTr.position = safePos;
                    float exitYaw = exitingSeat.HeadPivotTransform != null
                        ? exitingSeat.HeadPivotTransform.eulerAngles.y + seatYaw
                        : exitingSeat.Vehicle.Transform.eulerAngles.y;
                    playerTr.rotation = Quaternion.Euler(0f, exitYaw, 0f);
                }

                CollisionAndExitGuard.EnforcePlayerUprightAndCameraReset(playerTr);
                if (fpsCamTr != null && hasSavedFpsCamPos)
                {
                    fpsCamTr.localPosition = new Vector3(0f, Mathf.Max(1.35f, savedFpsCamLocalPos.y), 0f);
                }

                if (playerCc != null) playerCc.enabled = true;

                // Плавно возвращаем коллизии через 0.65с и включаем защиту от провала под дорогу на 0.45с
                collisionGuard?.SetLocalVehicleCollisionsIgnored(exitingSeat.Vehicle, playerTr, false, 0.65f);
                if (teleportToSafeExit)
                {
                    collisionGuard?.StartPostExitGroundGuard(playerTr, exitingSeat.Vehicle, 0.45f);
                }
            }

            seatYaw = 0f;
            seatPitch = 0f;
            seatLeanRoll = 0f;

            OnLocalPassengerStateChanged?.Invoke(exitingSeat, false);
            log?.Invoke($"[Пассажиры] Локальный игрок вышел с места '{exitingSeat.Spec.DisplayName}'");
        }

        private void UpdateSeatLookInput()
        {
            if (Cursor.visible) return;

            float sensX = 2.5f;
            float sensY = 2.5f;
            float dx = Input.GetAxisRaw("Mouse X") * sensX;
            float dy = Input.GetAxisRaw("Mouse Y") * sensY;

            seatYaw = Mathf.Clamp(seatYaw + dx, -120f, 120f);
            seatPitch = Mathf.Clamp(seatPitch - dy, -70f, 75f);

            float targetLean = 0f;
            if (Input.GetKey(KeyCode.E)) targetLean = -15f;
            seatLeanRoll = Mathf.Lerp(seatLeanRoll, targetLean, Time.deltaTime * 10f);
        }

        private void ApplyCameraLock()
        {
            if (activeLocalSeat == null || activeLocalSeat.HeadPivotTransform == null || playerTr == null) return;

            Transform headAnchor = activeLocalSeat.HeadPivotTransform;
            Vector3 targetHeadPos = headAnchor.position;
            Quaternion targetHeadYawRot = headAnchor.rotation * Quaternion.Euler(0f, seatYaw, 0f);

            // Сбрасываем промежуточные локальные углы/смещения, чтобы камера двигалась жёстко синхронно с кузовом
            if (pivotTr != null) pivotTr.localRotation = Quaternion.identity;
            if (animPivotTr != null)
            {
                animPivotTr.localPosition = Vector3.zero;
                animPivotTr.localRotation = Quaternion.identity;
            }
            if (camNodeTr != null) camNodeTr.localRotation = Quaternion.identity;
            if (fpsCamTr != null)
            {
                fpsCamTr.localPosition = Vector3.zero;
                fpsCamTr.localRotation = Quaternion.identity;
            }

            playerTr.position = targetHeadPos;
            playerTr.rotation = targetHeadYawRot;

            Transform activeCamTr = mainCamChildTr ?? (Camera.main != null ? Camera.main.transform : null);
            if (activeCamTr != null)
            {
                if (activeCamTr != fpsCamTr && activeCamTr.IsChildOf(playerTr))
                {
                    activeCamTr.localPosition = Vector3.zero;
                }
                activeCamTr.localRotation = Quaternion.Euler(seatPitch, 0f, seatLeanRoll);

                // Точная компенсация любого смещения промежуточных узлов скелета игрока
                Vector3 drift = targetHeadPos - activeCamTr.position;
                if (drift.sqrMagnitude > 0.000001f)
                {
                    playerTr.position += drift;
                }
            }
        }

        private void SuppressVehicleControlsWhilePassenger(TrackedVehicle veh)
        {
            if (veh == null || veh.IsDrivenLocally || veh.CarController == null) return;

            if (!suppressedCarControllers.ContainsKey(veh.CarController))
            {
                suppressedCarControllers[veh.CarController] = veh.CarController.enabled;
            }

            if (veh.CarController.enabled)
            {
                veh.CarController.steerInput = 0f;
                veh.CarController.throttleInput = 0f;
                veh.CarController.brakeInput = 0f;
                veh.CarController.clutchInput = 0f;
                veh.CarController.enabled = false;
            }
        }

        private void RestoreSuppressedCarControllers()
        {
            foreach (var kv in suppressedCarControllers)
            {
                if (kv.Key != null)
                {
                    kv.Key.enabled = kv.Value;
                }
            }
            suppressedCarControllers.Clear();
        }

        private void ClearPrompt()
        {
            if (!showingPrompt) return;
            showingPrompt = false;
            if (guiInteractionVar != null) guiInteractionVar.Value = string.Empty;
            if (guiUseVar != null) guiUseVar.Value = false;
        }

        private static bool IsEnterButtonDown()
        {
            bool pressed = false;
            try { pressed = cInput.GetButtonDown("Enter"); } catch { }
            if (!pressed)
            {
                pressed = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
            }
            return pressed;
        }
    }
}
