using System;
using UnityEngine;

namespace OSC.Module.Vehicles.Boat
{
    internal enum BoatEngineSubPacket : byte
    {
        LegacyMotor = 0,
        StarterIgnition = 1,
        GearShift = 2,
        ShutOff = 3,
        ContinuousMotorAndTiller = 4,
        FullEngineSnapshot = 5
    }

    internal enum BoatGearState : byte
    {
        Neutral = 0,
        First = 1,
        Reverse = 2
    }

    internal enum BoatMooringAction : byte
    {
        Attach = 1,
        Detach = 2,
        Snap = 3,
        Snapshot = 4
    }

    internal static class BoatConstants
    {
        public const int BoatVehicleId = 8;

        public const float TeleportGuardDuration = 0.45f;
        public const float ContinuousSendInterval = 0.05f; // 20 Hz
        public const float MoorSearchRadius = 6.5f;
        public const float MoorSnapExtraDistance = 3.5f;
        public const float MoorTeleportDetachDistance = 12.0f;
        public const float PlayerInteractDistance = 5.5f;

        public static readonly Vector3 BowCleatLocalOffset = new Vector3(0f, 0.35f, 2.15f);
        public static readonly Vector3 SternCleatLocalOffset = new Vector3(0f, 0.35f, -1.95f);

        public static readonly string[] BoatNames = { "BOAT", "Boat", "Vene" };
        public static readonly string[] TillerPaths =
        {
            "GFX/Outboard/SteeringPivot",
            "GFX/Motor/Pivot/SteeringPivot",
            "GFX/Motor/Pivot/Tiller",
            "GFX/Outboard/Tiller"
        };
        public static readonly string[] TillerFallbackNames =
        {
            "SteeringPivot",
            "Tiller",
            "Rudder",
            "Handle"
        };
    }

    internal static class BoatHelpers
    {
        public static float NormalizeAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            else if (angle < -180f) angle += 360f;
            return angle;
        }

        public static bool ContainsIgnoreCase(string source, string token)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(token)) return false;
            return source.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static GameObject FindBoatObject()
        {
            for (int i = 0; i < BoatConstants.BoatNames.Length; i++)
            {
                GameObject go = GameObject.Find(BoatConstants.BoatNames[i]);
                if (go != null) return go;
            }
            return null;
        }

        public static Transform FindFirstPath(Transform root, string[] paths)
        {
            if (root == null || paths == null) return null;
            for (int i = 0; i < paths.Length; i++)
            {
                Transform tr = root.Find(paths[i]);
                if (tr != null) return tr;
            }
            return null;
        }
    }
}
