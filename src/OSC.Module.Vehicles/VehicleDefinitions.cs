using System;
using UnityEngine;

namespace OSC.Module.Vehicles
{
    public enum VehicleKind : byte
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

    public sealed class PassengerSeatSpec
    {
        public int SeatIndex;
        public string DisplayName;
        public Vector3 TriggerLocalCenter;
        public Vector3 TriggerBoxSize;
        public Vector3 HeadPivotLocalPos;
        public float SeatLocalYaw;
        public float PreferredExitSide; // -1 = left, +1 = right, 0 = rear
        public bool HasExplicitPuppetOffset;
        public Vector3 ExplicitPuppetLocalOffset;

        public Vector3 ComputePuppetLocalOffset()
        {
            if (HasExplicitPuppetOffset) return ExplicitPuppetLocalOffset;
            Quaternion rot = Quaternion.Euler(0f, SeatLocalYaw, 0f);
            return HeadPivotLocalPos - (rot * new Vector3(0f, 1.21f, -0.04f));
        }
    }

    public sealed class VehicleSpec
    {
        public VehicleKind Kind;
        public string[] SearchNames;
        public Vector3 FallbackDriverHeadLocalPos;
        public float DriverSeatYaw;
        public string SteeringWheelPath;
        public string[] SteeringWheelNameFallbacks;
        public Vector3 FallbackSteeringGripLocalPos;
        public Vector3 SteeringRimLocalOffsetLeft;
        public Vector3 SteeringRimLocalOffsetRight;
        public float ExitSideHalfWidth;
        public float ExitRearHalfLength;
        public bool HasDriverSeat;
        public PassengerSeatSpec[] PassengerSeats;
    }

    public static class VehicleDefinitions
    {
        public static readonly VehicleSpec[] All = new VehicleSpec[]
        {
            new VehicleSpec
            {
                Kind = VehicleKind.Satsuma,
                SearchNames = new[] { "SATSUMA(557kg, 248)", "SATSUMA(580kg, 87)", "Satsuma", "SATSUMA" },
                FallbackDriverHeadLocalPos = new Vector3(-0.25f, 0.532f, 0.02f),
                DriverSeatYaw = 0f,
                SteeringWheelPath = "Dashboard/Steering/CarSteeringPivot",
                SteeringWheelNameFallbacks = new[] { "CarSteeringPivot" },
                FallbackSteeringGripLocalPos = new Vector3(0f, 0.22f, 0.82f),
                SteeringRimLocalOffsetLeft = new Vector3(-0.15f, 0f, 0f),
                SteeringRimLocalOffsetRight = new Vector3(0.15f, 0f, 0f),
                ExitSideHalfWidth = 1.45f,
                ExitRearHalfLength = 2.65f,
                HasDriverSeat = true,
                PassengerSeats = new[]
                {
                    new PassengerSeatSpec
                    {
                        SeatIndex = 0,
                        DisplayName = "Переднее пассажирское сиденье",
                        TriggerLocalCenter = new Vector3(0.282f, 0.28f, -0.067f),
                        TriggerBoxSize = new Vector3(0.50f, 0.65f, 0.65f),
                        HeadPivotLocalPos = new Vector3(0.300f, 0.532f, 0.020f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = 1f
                    },
                    new PassengerSeatSpec
                    {
                        SeatIndex = 1,
                        DisplayName = "Заднее сиденье (левое)",
                        TriggerLocalCenter = new Vector3(-0.282f, 0.28f, -0.567f),
                        TriggerBoxSize = new Vector3(0.48f, 0.65f, 0.55f),
                        HeadPivotLocalPos = new Vector3(-0.300f, 0.532f, -0.480f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = -1f
                    },
                    new PassengerSeatSpec
                    {
                        SeatIndex = 2,
                        DisplayName = "Заднее сиденье (правое)",
                        TriggerLocalCenter = new Vector3(0.282f, 0.28f, -0.567f),
                        TriggerBoxSize = new Vector3(0.48f, 0.65f, 0.55f),
                        HeadPivotLocalPos = new Vector3(0.300f, 0.532f, -0.480f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = 1f
                    }
                }
            },
            new VehicleSpec
            {
                Kind = VehicleKind.Hayosiko,
                SearchNames = new[] { "HAYOSIKO(1500kg, 250)", "Hayosiko", "HAYOSIKO" },
                FallbackDriverHeadLocalPos = new Vector3(-0.40f, 1.291f, 0.98f),
                DriverSeatYaw = 0f,
                SteeringWheelPath = "LOD/Dashboard/Steering/VanSteeringPivot",
                SteeringWheelNameFallbacks = new[] { "VanSteeringPivot" },
                FallbackSteeringGripLocalPos = new Vector3(0f, -0.2f, -0.09f),
                SteeringRimLocalOffsetLeft = new Vector3(-0.17f, 0f, 0f),
                SteeringRimLocalOffsetRight = new Vector3(0.17f, 0f, 0f),
                ExitSideHalfWidth = 1.65f,
                ExitRearHalfLength = 3.10f,
                HasDriverSeat = true,
                PassengerSeats = new[]
                {
                    new PassengerSeatSpec
                    {
                        SeatIndex = 0,
                        DisplayName = "Переднее правое сиденье",
                        TriggerLocalCenter = new Vector3(0.436f, 0.876f, 0.779f),
                        TriggerBoxSize = new Vector3(0.52f, 0.75f, 0.65f),
                        HeadPivotLocalPos = new Vector3(0.420f, 1.291f, 0.791f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = 1f,
                        HasExplicitPuppetOffset = true,
                        ExplicitPuppetLocalOffset = new Vector3(0.42f, 0.08f, 0.82f)
                    },
                    new PassengerSeatSpec
                    {
                        SeatIndex = 1,
                        DisplayName = "Переднее центральное сиденье",
                        TriggerLocalCenter = new Vector3(0.036f, 0.876f, 0.779f),
                        TriggerBoxSize = new Vector3(0.40f, 0.75f, 0.65f),
                        HeadPivotLocalPos = new Vector3(0.036f, 1.291f, 0.791f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = 1f,
                        HasExplicitPuppetOffset = true,
                        ExplicitPuppetLocalOffset = new Vector3(0.04f, 0.08f, 0.82f)
                    },
                    new PassengerSeatSpec
                    {
                        SeatIndex = 2,
                        DisplayName = "Лавка в кузове (левая)",
                        TriggerLocalCenter = new Vector3(-0.52f, 0.68f, -1.25f),
                        TriggerBoxSize = new Vector3(0.55f, 0.75f, 1.15f),
                        HeadPivotLocalPos = new Vector3(-0.52f, 1.26f, -1.25f),
                        SeatLocalYaw = 90f,
                        PreferredExitSide = 0f,
                        HasExplicitPuppetOffset = true,
                        ExplicitPuppetLocalOffset = new Vector3(-0.52f, 0.05f, -1.25f)
                    },
                    new PassengerSeatSpec
                    {
                        SeatIndex = 3,
                        DisplayName = "Лавка в кузове (правая)",
                        TriggerLocalCenter = new Vector3(0.52f, 0.68f, -1.25f),
                        TriggerBoxSize = new Vector3(0.55f, 0.75f, 1.15f),
                        HeadPivotLocalPos = new Vector3(0.52f, 1.26f, -1.25f),
                        SeatLocalYaw = -90f,
                        PreferredExitSide = 0f,
                        HasExplicitPuppetOffset = true,
                        ExplicitPuppetLocalOffset = new Vector3(0.52f, 0.05f, -1.25f)
                    }
                }
            },
            new VehicleSpec
            {
                Kind = VehicleKind.Ruscko,
                SearchNames = new[] { "RCO_RUSCKO12(270)", "RCO_RUSCKO12", "Ruscko", "RUSCKO" },
                FallbackDriverHeadLocalPos = new Vector3(-0.29f, 0.855f, -0.363f),
                DriverSeatYaw = 0f,
                SteeringWheelPath = "LOD/Dashboard/Steering/RusckoSteeringPivot",
                SteeringWheelNameFallbacks = new[] { "RusckoSteeringPivot" },
                FallbackSteeringGripLocalPos = new Vector3(0f, -0.22f, -0.1f),
                SteeringRimLocalOffsetLeft = new Vector3(-0.15f, 0f, 0f),
                SteeringRimLocalOffsetRight = new Vector3(0.15f, 0f, 0f),
                ExitSideHalfWidth = 1.45f,
                ExitRearHalfLength = 2.65f,
                HasDriverSeat = true,
                PassengerSeats = new[]
                {
                    new PassengerSeatSpec
                    {
                        SeatIndex = 0,
                        DisplayName = "Пассажирское сиденье",
                        TriggerLocalCenter = new Vector3(0.26f, 0.36f, -0.087f),
                        TriggerBoxSize = new Vector3(0.52f, 0.65f, 0.65f),
                        HeadPivotLocalPos = new Vector3(0.224f, 0.855f, -0.363f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = 1f
                    }
                }
            },
            new VehicleSpec
            {
                Kind = VehicleKind.Ferndale,
                SearchNames = new[] { "FERNDALE(1630kg)", "FERNDALE", "Ferndale" },
                FallbackDriverHeadLocalPos = new Vector3(-0.40f, 0.805f, -0.217f),
                DriverSeatYaw = 0f,
                SteeringWheelPath = "LOD/Dashboard/Steering/MuscleSteeringPivot",
                SteeringWheelNameFallbacks = new[] { "MuscleSteeringPivot" },
                FallbackSteeringGripLocalPos = new Vector3(0.2f, 0f, 0.05f),
                SteeringRimLocalOffsetLeft = new Vector3(-0.16f, 0f, 0f),
                SteeringRimLocalOffsetRight = new Vector3(0.16f, 0f, 0f),
                ExitSideHalfWidth = 1.55f,
                ExitRearHalfLength = 2.95f,
                HasDriverSeat = true,
                PassengerSeats = new[]
                {
                    new PassengerSeatSpec
                    {
                        SeatIndex = 0,
                        DisplayName = "Переднее правое сиденье",
                        TriggerLocalCenter = new Vector3(0.513f, 0.62f, -0.259f),
                        TriggerBoxSize = new Vector3(0.55f, 0.65f, 0.65f),
                        HeadPivotLocalPos = new Vector3(0.394f, 0.805f, -0.217f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = 1f
                    },
                    new PassengerSeatSpec
                    {
                        SeatIndex = 1,
                        DisplayName = "Заднее левое сиденье",
                        TriggerLocalCenter = new Vector3(-0.45f, 0.62f, -0.96f),
                        TriggerBoxSize = new Vector3(0.50f, 0.65f, 0.55f),
                        HeadPivotLocalPos = new Vector3(-0.394f, 0.805f, -1.05f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = -1f
                    },
                    new PassengerSeatSpec
                    {
                        SeatIndex = 2,
                        DisplayName = "Заднее правое сиденье",
                        TriggerLocalCenter = new Vector3(0.45f, 0.62f, -0.96f),
                        TriggerBoxSize = new Vector3(0.50f, 0.65f, 0.55f),
                        HeadPivotLocalPos = new Vector3(0.394f, 0.805f, -1.05f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = 1f
                    }
                }
            },
            new VehicleSpec
            {
                Kind = VehicleKind.Kekmet,
                SearchNames = new[] { "KEKMET(350-400psi)", "Kekmet", "KEKMET" },
                FallbackDriverHeadLocalPos = new Vector3(0f, 1.95f, -0.58f),
                DriverSeatYaw = 0f,
                SteeringWheelPath = "LOD/Dashboard/Steering/TractorSteeringPivot/valmet_steering",
                SteeringWheelNameFallbacks = new[] { "valmet_steering", "TractorSteeringPivot" },
                FallbackSteeringGripLocalPos = new Vector3(0.2f, 0f, 0f),
                SteeringRimLocalOffsetLeft = new Vector3(-0.16f, 0f, 0f),
                SteeringRimLocalOffsetRight = new Vector3(0.16f, 0f, 0f),
                ExitSideHalfWidth = 1.65f,
                ExitRearHalfLength = 2.60f,
                HasDriverSeat = true,
                PassengerSeats = new[]
                {
                    new PassengerSeatSpec
                    {
                        SeatIndex = 0,
                        DisplayName = "Боковое место кабины",
                        TriggerLocalCenter = new Vector3(0.46f, 1.38f, -0.55f),
                        TriggerBoxSize = new Vector3(0.46f, 0.68f, 0.62f),
                        HeadPivotLocalPos = new Vector3(0.46f, 1.88f, -0.55f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = 1f
                    }
                }
            },
            new VehicleSpec
            {
                Kind = VehicleKind.Gifu,
                SearchNames = new[] { "GIFU(750/450psi)", "Gifu", "GIFU" },
                FallbackDriverHeadLocalPos = new Vector3(-0.70f, 2.24f, 2.74f),
                DriverSeatYaw = 0f,
                SteeringWheelPath = "LOD/Dashboard/Steering/TruckSteeringPivot",
                SteeringWheelNameFallbacks = new[] { "TruckSteeringPivot" },
                FallbackSteeringGripLocalPos = new Vector3(0f, 0.05f, 0.22f),
                SteeringRimLocalOffsetLeft = new Vector3(-0.20f, 0f, 0f),
                SteeringRimLocalOffsetRight = new Vector3(0.20f, 0f, 0f),
                ExitSideHalfWidth = 1.95f,
                ExitRearHalfLength = 4.60f,
                HasDriverSeat = true,
                PassengerSeats = new[]
                {
                    new PassengerSeatSpec
                    {
                        SeatIndex = 0,
                        DisplayName = "Переднее пассажирское сиденье",
                        TriggerLocalCenter = new Vector3(0.696f, 1.759f, 2.831f),
                        TriggerBoxSize = new Vector3(0.58f, 0.78f, 0.65f),
                        HeadPivotLocalPos = new Vector3(0.700f, 2.240f, 2.740f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = 1f
                    },
                    new PassengerSeatSpec
                    {
                        SeatIndex = 1,
                        DisplayName = "Спальное место (правое)",
                        TriggerLocalCenter = new Vector3(0.65f, 1.759f, 2.28f),
                        TriggerBoxSize = new Vector3(0.55f, 0.72f, 0.55f),
                        HeadPivotLocalPos = new Vector3(0.65f, 2.20f, 2.28f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = 1f
                    },
                    new PassengerSeatSpec
                    {
                        SeatIndex = 2,
                        DisplayName = "Спальное место (центр)",
                        TriggerLocalCenter = new Vector3(0f, 1.759f, 2.28f),
                        TriggerBoxSize = new Vector3(0.55f, 0.72f, 0.55f),
                        HeadPivotLocalPos = new Vector3(0f, 2.20f, 2.28f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = 1f
                    }
                }
            },
            new VehicleSpec
            {
                Kind = VehicleKind.Jonnez,
                SearchNames = new[] { "JONNEZ ES(Clone)", "JONNEZ ES", "Jonnez", "JONNEZ" },
                FallbackDriverHeadLocalPos = new Vector3(0.02f, 1.18f, -0.40f),
                DriverSeatYaw = 0f,
                SteeringWheelPath = "LOD/Suspension/Steering",
                SteeringWheelNameFallbacks = new[] { "Steering", "Fork", "Handlebars" },
                FallbackSteeringGripLocalPos = Vector3.zero,
                SteeringRimLocalOffsetLeft = Vector3.zero,
                SteeringRimLocalOffsetRight = Vector3.zero,
                ExitSideHalfWidth = 0.95f,
                ExitRearHalfLength = 1.35f,
                HasDriverSeat = true,
                PassengerSeats = new PassengerSeatSpec[0]
            },
            new VehicleSpec
            {
                Kind = VehicleKind.Flatbed,
                SearchNames = new[] { "FLATBED", "Flatbed" },
                FallbackDriverHeadLocalPos = Vector3.zero,
                DriverSeatYaw = 0f,
                SteeringWheelPath = string.Empty,
                SteeringWheelNameFallbacks = new string[0],
                FallbackSteeringGripLocalPos = Vector3.zero,
                SteeringRimLocalOffsetLeft = Vector3.zero,
                SteeringRimLocalOffsetRight = Vector3.zero,
                ExitSideHalfWidth = 1.75f,
                ExitRearHalfLength = 3.20f,
                HasDriverSeat = false,
                PassengerSeats = new[]
                {
                    new PassengerSeatSpec
                    {
                        SeatIndex = 0,
                        DisplayName = "Платформа прицепа",
                        TriggerLocalCenter = new Vector3(0f, 0.92f, 1.80f),
                        TriggerBoxSize = new Vector3(1.50f, 0.68f, 1.40f),
                        HeadPivotLocalPos = new Vector3(0f, 1.54f, 1.80f),
                        SeatLocalYaw = 0f,
                        PreferredExitSide = 1f
                    }
                }
            },
            new VehicleSpec
            {
                Kind = VehicleKind.Boat,
                SearchNames = new[] { "BOAT", "Boat" },
                FallbackDriverHeadLocalPos = new Vector3(-0.20f, 0.57f, 1.25f),
                DriverSeatYaw = 165f,
                SteeringWheelPath = "GFX/Outboard/SteeringPivot",
                SteeringWheelNameFallbacks = new[] { "Tiller", "Rudder", "Handle", "SteeringPivot" },
                FallbackSteeringGripLocalPos = new Vector3(-0.18f, 0.35f, 1.65f),
                SteeringRimLocalOffsetLeft = new Vector3(-0.08f, 0f, 0f),
                SteeringRimLocalOffsetRight = new Vector3(0.08f, 0f, 0f),
                ExitSideHalfWidth = 1.45f,
                ExitRearHalfLength = 2.65f,
                HasDriverSeat = true,
                PassengerSeats = new[]
                {
                    new PassengerSeatSpec
                    {
                        SeatIndex = 0,
                        DisplayName = "Средняя скамья лодки",
                        TriggerLocalCenter = new Vector3(0f, 0.22f, 0.10f),
                        TriggerBoxSize = new Vector3(0.96f, 0.65f, 0.56f),
                        HeadPivotLocalPos = new Vector3(0f, 0.74f, 0.10f),
                        SeatLocalYaw = 180f,
                        PreferredExitSide = 1f,
                        HasExplicitPuppetOffset = true,
                        ExplicitPuppetLocalOffset = new Vector3(0f, -0.38f, 0.10f)
                    },
                    new PassengerSeatSpec
                    {
                        SeatIndex = 1,
                        DisplayName = "Носовая скамья лодки",
                        TriggerLocalCenter = new Vector3(0f, 0.22f, -1.00f),
                        TriggerBoxSize = new Vector3(0.90f, 0.65f, 0.56f),
                        HeadPivotLocalPos = new Vector3(0f, 0.74f, -1.00f),
                        SeatLocalYaw = 180f,
                        PreferredExitSide = -1f,
                        HasExplicitPuppetOffset = true,
                        ExplicitPuppetLocalOffset = new Vector3(0f, -0.38f, -1.00f)
                    }
                }
            }
        };

        public static GameObject FindVehicleObject(VehicleSpec spec)
        {
            if (spec == null || spec.SearchNames == null) return null;
            for (int i = 0; i < spec.SearchNames.Length; i++)
            {
                GameObject go = GameObject.Find(spec.SearchNames[i]);
                if (go != null) return go;
            }
            return null;
        }
    }
}
