using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OSC.Core.Utilities
{
    public static class SceneHasher
    {
        private static readonly Dictionary<int, GameObject> hashToGameObject = new Dictionary<int, GameObject>();
        private static readonly Dictionary<GameObject, int> gameObjectToHash = new Dictionary<GameObject, int>();
        private static readonly StringBuilder stringBuilder = new StringBuilder(256);

        public static int RegisteredCount => hashToGameObject.Count;

        public static string GetScenePath(Transform t)
        {
            if (t == null) return string.Empty;

            if (t.parent == null ||
                t.parent.name == "ItemPivot" ||
                t.parent.name == "ItemHolder" ||
                t.parent.name == "ToolHolder" ||
                t.parent.name == "HandHolder" ||
                t.parent.name == "ItemHold")
            {
                return t.name ?? string.Empty;
            }

            lock (stringBuilder)
            {
                stringBuilder.Length = 0;
                BuildPathRecursive(t, stringBuilder);
                return stringBuilder.ToString();
            }
        }

        private static void BuildPathRecursive(Transform t, StringBuilder sb)
        {
            if (t.parent != null &&
                t.parent.name != "ItemPivot" &&
                t.parent.name != "ItemHolder" &&
                t.parent.name != "ToolHolder" &&
                t.parent.name != "HandHolder" &&
                t.parent.name != "ItemHold")
            {
                BuildPathRecursive(t.parent, sb);
                sb.Append('/');
                sb.Append(t.name);
                sb.Append('_');
                sb.Append(t.GetSiblingIndex());
            }
            else
            {
                sb.Append(t.name);
            }
        }

        public static int GetStableHash(string str)
        {
            if (string.IsNullOrEmpty(str)) return 0;
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < str.Length; i++)
                {
                    hash ^= str[i];
                    hash *= 16777619;
                }
                int result = (int)hash;
                return (result == 0) ? 1 : result;
            }
        }

        public static string GetObjectIdentifier(GameObject go)
        {
            if (go == null) return string.Empty;

            var fsms = go.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < fsms.Length; i++)
            {
                if (fsms[i] != null && fsms[i].FsmName == "Use")
                {
                    fsms[i].InitializeSafe();
                    var idVar = fsms[i].FsmVariables.FindFsmString("ID");
                    if (idVar != null && !string.IsNullOrEmpty(idVar.Value))
                    {
                        return idVar.Value;
                    }
                }
            }

            string nameLower = go.name.ToLower();
            if (nameLower.Contains("beercase0") || nameLower == "beercase")
            {
                return "beercase_default";
            }

            if (go.name == "block(Clone)" || nameLower == "satsuma_block")
            {
                return "satsuma_engine_block";
            }

            if ((go.name == "JONNEZ ES(Clone)" || go.name == "JONNEZ ES" ||
                 (go.name.StartsWith("JONNEZ") && go.GetComponent<Rigidbody>() != null)) &&
                (go.transform.parent == null ||
                 go.transform.parent.name == "ItemPivot" ||
                 go.transform.parent.name == "ItemHolder" ||
                 go.transform.parent.name == "ToolHolder" ||
                 go.transform.parent.name == "HandHolder" ||
                 go.transform.parent.name == "ItemHold" ||
                 go.transform.parent.name.StartsWith("HAYOSIKO", StringComparison.OrdinalIgnoreCase)))
            {
                return "jonnez_moped_unique";
            }

            return GetScenePath(go.transform);
        }

        public static int GetHash(GameObject go)
        {
            if (go == null) return 0;

            if (gameObjectToHash.TryGetValue(go, out int cached) && cached != 0)
            {
                return cached;
            }

            string idStr = GetObjectIdentifier(go);
            int hash = GetStableHash(idStr);

            if (!hashToGameObject.TryGetValue(hash, out var existing) || existing == null)
            {
                hashToGameObject[hash] = go;
            }
            gameObjectToHash[go] = hash;
            return hash;
        }

        public static bool TryGetHash(GameObject go, out int hash)
        {
            if (go == null)
            {
                hash = 0;
                return false;
            }
            return gameObjectToHash.TryGetValue(go, out hash);
        }

        public static void RegisterDynamicObject(int hash, GameObject go)
        {
            if (go == null || hash == 0) return;
            hashToGameObject[hash] = go;
            gameObjectToHash[go] = hash;
        }

        public static GameObject FindObjectByHash(int hash)
        {
            if (hash == 0) return null;
            if (hashToGameObject.TryGetValue(hash, out var go))
            {
                if (go != null) return go;
                hashToGameObject.Remove(hash);
            }
            return null;
        }

        public static void Clear()
        {
            hashToGameObject.Clear();
            gameObjectToHash.Clear();
        }
    }
}
