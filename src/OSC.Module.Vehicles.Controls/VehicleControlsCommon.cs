using System;
using HutongGames.PlayMaker;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Vehicles.Controls
{
    /// <summary>
    /// Действие PlayMaker для ежекадрового опроса (OnEnter + OnUpdate),
    /// необходимо для корректной обёртки GetMouseButtonUp / MousePickEvent без завершения состояния.
    /// </summary>
    internal sealed class EveryFrameFsmAction : FsmStateAction
    {
        private readonly Action tickAction;

        public EveryFrameFsmAction(Action action)
        {
            tickAction = action;
        }

        public override void OnEnter()
        {
            InvokeSafe();
        }

        public override void OnUpdate()
        {
            InvokeSafe();
        }

        private void InvokeSafe()
        {
            try
            {
                tickAction?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OSC Controls EveryFrameFsmAction] {ex.Message}");
            }
        }
    }

    internal enum VehicleSlot : byte
    {
        Satsuma = 0,
        Hayosiko = 1,
        Ruscko = 2,
        Ferndale = 3,
        Kekmet = 4,
        Gifu = 5,
        Jonnez = 6,
        Flatbed = 7,
        Boat = 8
    }

    internal sealed class VehicleCatalogEntry
    {
        public VehicleSlot Slot;
        public string DisplayName;
        public string[] SearchNames;
        public string FuelTankPath;
        public GameObject RootObject;
        public Rigidbody MainRigidbody;
        public Drivetrain Drivetrain;
        public SoundController SoundController;
        public int DriverPlayerId = -1;
        public bool IsDrivenLocally;
        public bool IsDrivenRemotely;
        public float SuppressCollisionUntilTime;

        public byte VehicleId => (byte)Slot;
        public bool IsBound => RootObject != null;
    }

    internal enum SystemToggleSubType : byte
    {
        Ignition = 0,
        DashboardButton = 1,
        DashboardLever = 2,
        DashboardKnob = 3,
        TurnSignals = 4,
        InteriorLight = 5,
        Horn = 6,
        GenericSwitch = 7,
        GifuRange = 8,
        FerndaleShifter = 9,
        KekmetRange = 10,
        KekmetFrontLoader = 11
    }

    internal enum TowingSubType : byte
    {
        RopeAttach = 0,
        RopeDetach = 1,
        RopeSnap = 2,
        GifuHose = 3,
        CaravanDetach = 4,
        FlatbedState = 5,
        GifuWinch = 6
    }

    internal enum DamageSubType : byte
    {
        BodyCrash = 0,
        BodyRepair = 1,
        GlassBreak = 2,
        GlassRepair = 3
    }

    internal enum FluidWearSubType : byte
    {
        SatsumaFluids = 0,
        PartBreakage = 1,
        GearGrind = 2,
        LicensePlates = 3,
        IdleThrottle = 4
    }

    internal enum RadioSubType : byte
    {
        StateSync = 0,
        KnobSync = 1,
        CdAction = 2
    }

    internal static class ControlsHelpers
    {
        public static VehicleCatalogEntry[] CreateCatalog()
        {
            return new[]
            {
                new VehicleCatalogEntry
                {
                    Slot = VehicleSlot.Satsuma,
                    DisplayName = "Satsuma",
                    SearchNames = new[] { "SATSUMA(557kg, 248)", "SATSUMA(580kg, 87)", "Satsuma", "SATSUMA" },
                    FuelTankPath = "Database/DatabaseMechanics/FuelTank"
                },
                new VehicleCatalogEntry
                {
                    Slot = VehicleSlot.Hayosiko,
                    DisplayName = "Hayosiko",
                    SearchNames = new[] { "HAYOSIKO(1500kg, 250)", "Hayosiko", "HAYOSIKO" },
                    FuelTankPath = "HAYOSIKO(1500kg, 250)/FuelTank"
                },
                new VehicleCatalogEntry
                {
                    Slot = VehicleSlot.Ruscko,
                    DisplayName = "Ruscko",
                    SearchNames = new[] { "RCO_RUSCKO12(270)", "RCO_RUSCKO12", "Ruscko", "RUSCKO" },
                    FuelTankPath = "RCO_RUSCKO12(270)/FuelTank"
                },
                new VehicleCatalogEntry
                {
                    Slot = VehicleSlot.Ferndale,
                    DisplayName = "Ferndale",
                    SearchNames = new[] { "FERNDALE(1630kg)", "FERNDALE", "Ferndale" },
                    FuelTankPath = "FERNDALE(1630kg)/FuelTank"
                },
                new VehicleCatalogEntry
                {
                    Slot = VehicleSlot.Kekmet,
                    DisplayName = "Kekmet",
                    SearchNames = new[] { "KEKMET(350-400psi)", "Kekmet", "KEKMET" },
                    FuelTankPath = "KEKMET(350-400psi)/FuelTank"
                },
                new VehicleCatalogEntry
                {
                    Slot = VehicleSlot.Gifu,
                    DisplayName = "Gifu",
                    SearchNames = new[] { "GIFU(750/450psi)", "Gifu", "GIFU" },
                    FuelTankPath = "GIFU(750/450psi)/FuelTank"
                },
                new VehicleCatalogEntry
                {
                    Slot = VehicleSlot.Jonnez,
                    DisplayName = "Jonnez",
                    SearchNames = new[] { "JONNEZ ES(Clone)", "JONNEZ ES", "Jonnez", "JONNEZ" },
                    FuelTankPath = "JONNEZ ES(Clone)/FuelTank"
                },
                new VehicleCatalogEntry
                {
                    Slot = VehicleSlot.Flatbed,
                    DisplayName = "Flatbed",
                    SearchNames = new[] { "FLATBED", "Flatbed" },
                    FuelTankPath = string.Empty
                },
                new VehicleCatalogEntry
                {
                    Slot = VehicleSlot.Boat,
                    DisplayName = "Boat",
                    SearchNames = new[] { "BOAT", "Boat" },
                    FuelTankPath = "BOAT/GFX/Motor/Pivot/FuelTank"
                }
            };
        }

        public static GameObject FindVehicleRoot(VehicleCatalogEntry entry)
        {
            if (entry == null || entry.SearchNames == null) return null;
            for (int i = 0; i < entry.SearchNames.Length; i++)
            {
                GameObject go = GameObject.Find(entry.SearchNames[i]);
                if (go != null) return go;
            }
            return null;
        }

        public static VehicleCatalogEntry FindVehicleByTransform(VehicleCatalogEntry[] catalog, Transform target)
        {
            if (catalog == null || target == null) return null;
            Transform root = target.root;
            for (int i = 0; i < catalog.Length; i++)
            {
                var entry = catalog[i];
                if (entry.RootObject == null) continue;
                Transform entryTr = entry.RootObject.transform;
                if (target == entryTr || root == entryTr || target.IsChildOf(entryTr))
                {
                    return entry;
                }
            }
            return null;
        }

        public static VehicleCatalogEntry FindVehicleById(VehicleCatalogEntry[] catalog, int vehicleId)
        {
            if (catalog == null) return null;
            for (int i = 0; i < catalog.Length; i++)
            {
                if (catalog[i].VehicleId == vehicleId)
                {
                    return catalog[i];
                }
            }
            return null;
        }

        public static string GetHierarchyPath(Transform current)
        {
            if (current == null) return string.Empty;
            string path = current.name;
            while (current.parent != null)
            {
                current = current.parent;
                path = current.name + "/" + path;
            }
            return path;
        }

        public static int GetControlKeyHash(PlayMakerFSM fsm)
        {
            if (fsm == null) return 0;
            string path = SceneHasher.GetScenePath(fsm.transform);
            return SceneHasher.GetStableHash(path + "::" + fsm.FsmName);
        }

        public static bool ContainsOrdinalIgnoreCase(string source, string value)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value)) return false;
            return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static PlayMakerFSM FindFsmSafe(GameObject go, string fsmName)
        {
            return PlayMakerBridge.FindFsm(go, fsmName);
        }
    }
}
