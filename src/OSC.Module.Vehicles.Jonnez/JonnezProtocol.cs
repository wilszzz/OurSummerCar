using System;
using UnityEngine;

namespace OSC.Module.Vehicles.Jonnez
{
    internal enum JonnezSubPacket : byte
    {
        KickstandState = 0,
        CenterOfMassState = 1,
        HelmetState = 2,
        CargoHayosikoState = 3,
        FullSnapshot = 4
    }

    internal static class JonnezConstants
    {
        public const int JonnezVehicleId = 6;
        public const int HayosikoVehicleId = 1;

        public const float MaxRemoteRollDegrees = 35f;
        public const float MountUprightClampDegrees = 14f;
        public const float MountStabilizeDuration = 0.85f;
        public const float TeleportGuardDuration = 0.50f;
        public const float LowSpeedCrashThreshold = 1.5f;

        // Локальные границы грузового отсека Hayosiko (с гистерезисом входа/выхода)
        public static readonly Vector3 HayosikoBayEnterMin = new Vector3(-0.85f, 0.25f, -2.25f);
        public static readonly Vector3 HayosikoBayEnterMax = new Vector3(0.85f, 1.80f, 0.15f);
        public static readonly Vector3 HayosikoBayExitMin = new Vector3(-1.05f, 0.10f, -2.55f);
        public static readonly Vector3 HayosikoBayExitMax = new Vector3(1.05f, 2.05f, 0.35f);

        public static readonly string[] JonnezNames =
        {
            "JONNEZ ES(Clone)",
            "JONNEZ ES",
            "Jonnez",
            "JONNEZ"
        };

        public static readonly string[] HayosikoNames =
        {
            "HAYOSIKO(1500kg, 250)",
            "Hayosiko",
            "HAYOSIKO"
        };

        public static readonly string[] ForkPaths =
        {
            "LOD/Suspension/Steering",
            "Suspension/Steering",
            "Steering",
            "Fork",
            "Pivot/Handlebars"
        };
    }

    internal static class JonnezHelpers
    {
        public static float NormalizeAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            else if (angle < -180f) angle += 360f;
            return angle;
        }

        public static GameObject FindByNames(string[] names)
        {
            if (names == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                GameObject go = GameObject.Find(names[i]);
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

        public static bool ContainsIgnoreCase(string source, string token)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(token)) return false;
            return source.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsInsideBounds(Vector3 p, Vector3 min, Vector3 max)
        {
            return p.x >= min.x && p.x <= max.x
                && p.y >= min.y && p.y <= max.y
                && p.z >= min.z && p.z <= max.z;
        }

        public static bool SetIgnoreCollisionSafe(Collider a, Collider b, bool ignore)
        {
            if (a == null || b == null || a == b) return false;
            if (!a.enabled || !b.enabled) return false;
            if (!a.gameObject.activeInHierarchy || !b.gameObject.activeInHierarchy) return false;
            try
            {
                Physics.IgnoreCollision(a, b, ignore);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
