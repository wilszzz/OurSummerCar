using System;
using OSC.Core.Ownership;
using UnityEngine;

namespace OSC.Module.Items
{
    public enum ItemSubPacket : byte
    {
        TransformMove = 0,
        BeerCaseDrink = 1,
        ConsumableAction = 2,
        JerryCanState = 3,
        ToolSetState = 4,
        EquipmentState = 5,
        SpawnedItem = 6,
        ShoppingBagGarbage = 7,
        WorldSnapshot = 8,
        SnapshotRequest = 9
    }

    public enum ConsumableActionKind : byte
    {
        ConsumeDrink = 1,
        ConsumeBite = 2,
        Empty = 3,
        Destroy = 4,
        FluidLevel = 5
    }

    public static class ItemValidator
    {
        private static readonly string[] VehiclePrefixes =
        {
            "SATSUMA", "HAYOSIKO", "GIFU", "FERNDALE", "RCO_RUSCKO",
            "RUSCKO", "RICOCHET", "KEKMET", "FLATBED", "BOAT", "COMBINE"
        };

        public static bool IsHandParent(Transform parent)
        {
            if (parent == null) return false;
            string n = parent.name;
            return n == "ItemPivot" || n == "ItemHolder" || n == "ToolHolder" || n == "HandHolder" || n == "ItemHold";
        }

        public static bool IsWorldItemContainer(Transform parent)
        {
            if (parent == null) return false;
            string n = parent.name;
            return n == "ITEMS" || n == "DatabaseOrders";
        }

        public static bool IsSpecialCarryObject(GameObject go)
        {
            return IsSpecialCarryObject(go, allowWhenParented: false);
        }

        public static bool IsSpecialCarryObject(GameObject go, bool allowWhenParented)
        {
            if (go == null) return false;
            if (!go.name.StartsWith("JONNEZ", StringComparison.OrdinalIgnoreCase)) return false;
            if (allowWhenParented) return true;
            return go.transform.parent == null || IsHandParent(go.transform.parent);
        }

        public static bool IsVehicleOrAttachedPart(GameObject go)
        {
            return IsVehicleOrAttachedPart(go, allowSpecialCarryWhenParented: false);
        }

        public static bool IsVehicleOrAttachedPart(GameObject go, bool allowSpecialCarryWhenParented)
        {
            if (go == null) return true;
            if (allowSpecialCarryWhenParented && IsSpecialCarryObject(go, allowWhenParented: true))
            {
                return false;
            }

            string name = go.name ?? string.Empty;
            string rootName = go.transform.root != null ? (go.transform.root.name ?? string.Empty) : string.Empty;

            for (int i = 0; i < VehiclePrefixes.Length; i++)
            {
                string prefix = VehiclePrefixes[i];
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    rootName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (name.StartsWith("JONNEZ", StringComparison.OrdinalIgnoreCase) ||
                rootName.StartsWith("JONNEZ", StringComparison.OrdinalIgnoreCase))
            {
                // Разрешён только сам корневой мопед JONNEZ ES, но не его руль/вилка/колёса
                return !IsSpecialCarryObject(go, allowSpecialCarryWhenParented);
            }

            Transform p = go.transform.parent;
            if (p != null && !IsHandParent(p) && !IsWorldItemContainer(p))
            {
                // Любая деталь, прикрученная к кузову/раме/двигателю, остаётся под управлением авто
                return true;
            }

            return false;
        }

        public static bool IsHeavyGarageEquipment(string nameLower)
        {
            return nameLower.Contains("floor jack") || nameLower.Contains("motor hoist");
        }

        public static bool IsValidPortableItem(GameObject go, OwnershipRegistry ownership, string moduleId)
        {
            return IsValidPortableItem(go, ownership, moduleId, allowSpecialCarryWhenParented: false);
        }

        public static bool IsValidPortableItem(GameObject go, OwnershipRegistry ownership, string moduleId, bool allowSpecialCarryWhenParented)
        {
            if (go == null) return false;

            string name = go.name ?? string.Empty;
            string nameLower = name.ToLowerInvariant();
            string rootName = go.transform.root != null ? (go.transform.root.name ?? string.Empty) : string.Empty;

            if (rootName.StartsWith("PLAYER", StringComparison.OrdinalIgnoreCase) ||
                rootName.StartsWith("RemotePuppet", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (nameLower.Contains("empty") && !nameLower.Contains("case"))
            {
                return false;
            }

            if (name.StartsWith("Door", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("pivot_door", StringComparison.OrdinalIgnoreCase) ||
                name == "coll" ||
                (name.StartsWith("Pivot", StringComparison.OrdinalIgnoreCase) && go.transform.parent != null && go.transform.parent.name.StartsWith("Door", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (IsSpecialCarryObject(go, allowSpecialCarryWhenParented))
            {
                return true;
            }

            if (IsVehicleOrAttachedPart(go, allowSpecialCarryWhenParented))
            {
                return false;
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                if (ownership != null)
                {
                    string owner = ownership.GetOwner(rb);
                    if (!string.IsNullOrEmpty(owner) && owner != moduleId)
                    {
                        return false;
                    }
                }

                if (rb.mass > 160f && !IsHeavyGarageEquipment(nameLower))
                {
                    return false;
                }
            }
            else if (!nameLower.StartsWith("shopping bag") && name != "block(Clone)")
            {
                return false;
            }

            return true;
        }
    }
}
