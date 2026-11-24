using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OurSummerCar.Core
{
    /// <summary>
    /// Детерминированный генератор сетевых идентификаторов объектов сцены
    /// по точной иерархической модели WreckMP (FNV-1a 32-bit от пути с sibling index).
    /// </summary>
    public static class SceneHasher
    {
        private static readonly Dictionary<int, GameObject> hashToGameObject = new Dictionary<int, GameObject>();
        private static readonly Dictionary<GameObject, int> gameObjectToHash = new Dictionary<GameObject, int>();
        private static readonly StringBuilder stringBuilder = new StringBuilder(256);

        public static int RegisteredCount => hashToGameObject.Count;

        /// <summary>
        /// Вычисляет точный детерминированный путь объекта с номерами дочерних элементов (SiblingIndex).
        /// Пример: SATSUMA(557kg, 248)_0/FR_22/AckerFR_0/wheelFR_0/SpindleFR_1
        /// Для корневых объектов (без родителя, либо находящихся в руках ItemPivot) возвращает чистое имя transform.name БЕЗ SiblingIndex.
        /// </summary>
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

        /// <summary>
        /// FNV-1a 32-bit хэш функция (идентичная WreckMP).
        /// </summary>
        public static int GetStableHash(string str)
        {
            if (string.IsNullOrEmpty(str)) return 0;
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < str.Length; i++)
                {
                    hash ^= (uint)str[i];
                    hash *= 16777619;
                }
                int result = (int)hash;
                return (result == 0) ? 1 : result;
            }
        }

        /// <summary>
        /// Возвращает идентификатор объекта: значение переменной "ID" из FSM "Use" (модель WreckMP)
        /// либо стабильный путь в иерархии сцены (без SiblingIndex для корневых объектов).
        /// </summary>
        public static string GetObjectIdentifier(GameObject go)
        {
            if (go == null) return string.Empty;

            // 1. Проверяем наличие FSM "Use" с переменной ID (модель WreckMP - уникальный ключ предмета в MSC)
            var fsms = go.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < fsms.Length; i++)
            {
                if (fsms[i] != null && fsms[i].FsmName == "Use")
                {
                    if (fsms[i].Fsm != null && !fsms[i].Fsm.Initialized)
                    {
                        fsms[i].Initialize();
                    }
                    var idVar = fsms[i].FsmVariables.FindFsmString("ID");
                    if (idVar != null && !string.IsNullOrEmpty(idVar.Value))
                    {
                        return idVar.Value;
                    }
                }
            }

            // 2. Дефолтный ящик пива в доме — жестко фиксированный стабильный идентификатор
            string nameLower = go.name.ToLower();
            if (nameLower.Contains("beercase0") || nameLower == "beercase")
            {
                return "beercase_default";
            }

            // 3. Блок двигателя Сацумы — абсолютно уникальный ключевой агрегат в игре
            if (go.name == "block(Clone)" || nameLower == "satsuma_block")
            {
                return "satsuma_engine_block";
            }

            // 4. Мопед Jonnez ES — уникальный ключевой транспорт в игре (только корневой объект мопеда)
            if ((go.name == "JONNEZ ES(Clone)" || go.name == "JONNEZ ES" || go.name.StartsWith("JONNEZ")) &&
                (go.transform.parent == null ||
                 go.transform.parent.name == "ItemPivot" ||
                 go.transform.parent.name == "ItemHolder" ||
                 go.transform.parent.name == "ToolHolder" ||
                 go.transform.parent.name == "HandHolder" ||
                 go.transform.parent.name == "ItemHold"))
            {
                return "jonnez_moped_unique";
            }

            // 5. Для объектов без родителя GetScenePath возвращает чистое имя transform.name БЕЗ SiblingIndex,
            // а для вложенных в иерархию — детерминированный путь parentPath/name_siblingIndex.
            return GetScenePath(go.transform);
        }

        /// <summary>
        /// Возвращает детерминированный хэш для GameObject (иммутабелен после первой регистрации).
        /// </summary>
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

        /// <summary>
        /// Пытается получить уже зарегистрированный хэш для GameObject.
        /// </summary>
        public static bool TryGetHash(GameObject go, out int hash)
        {
            if (go == null)
            {
                hash = 0;
                return false;
            }
            return gameObjectToHash.TryGetValue(go, out hash);
        }

        /// <summary>
        /// Регистрирует динамический объект (например, выпавший из пакета или созданный в магазине).
        /// </summary>
        public static void RegisterDynamicObject(int hash, GameObject go)
        {
            if (go == null || hash == 0) return;
            hashToGameObject[hash] = go;
            gameObjectToHash[go] = hash;
        }

        /// <summary>
        /// Удаляет объект из реестра (например, при уничтожении пакета).
        /// </summary>
        public static void UnregisterObject(GameObject go)
        {
            if (go == null) return;
            if (gameObjectToHash.TryGetValue(go, out int hash))
            {
                gameObjectToHash.Remove(go);
                hashToGameObject.Remove(hash);
            }
        }

        /// <summary>
        /// Регистрирует объект в реестре хэшей.
        /// </summary>
        public static int RegisterObject(GameObject go)
        {
            return GetHash(go);
        }

        /// <summary>
        /// Мгновенный O(1) поиск GameObject по стабильному хэшу без блокировок и сканирования ресурсов.
        /// </summary>
        public static GameObject FindObjectByHash(int hash)
        {
            if (hash == 0) return null;

            if (hashToGameObject.TryGetValue(hash, out var go))
            {
                if (go != null) return go;
                // Объект был уничтожен в Unity — чистим мертвую ссылку
                hashToGameObject.Remove(hash);
            }

            return null;
        }

        private struct IndexCandidate
        {
            public GameObject Go;
            public string Id;
            public string Path;
            public int SiblingIndex;
        }

        private static bool IsSceneObject(GameObject go)
        {
            if (go == null || go.transform == null) return false;
            return go.activeInHierarchy || !go.activeSelf || go.transform.parent != null;
        }

        /// <summary>
        /// Сканирует и детерминированно регистрирует все подходящие физические предметы сцены,
        /// детали с тегом PART и объекты Removal/Screw (включая неактивные в кладовке, магазине или коробках).
        /// </summary>
        public static int IndexSceneItems(Func<GameObject, bool> filter)
        {
            int count = 0;
            try
            {
                var uniqueObjects = new HashSet<GameObject>();

                var rbs = Resources.FindObjectsOfTypeAll<Rigidbody>();
                for (int i = 0; i < rbs.Length; i++)
                {
                    if (rbs[i] == null) continue;
                    GameObject go = rbs[i].gameObject;
                    if (!IsSceneObject(go)) continue;

                    if (filter == null || filter(go))
                    {
                        uniqueObjects.Add(go);
                    }
                }

                var allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i];
                    if (!IsSceneObject(go)) continue;
                    if (go.CompareTag("PART"))
                    {
                        uniqueObjects.Add(go);
                    }
                }

                var allFsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
                for (int i = 0; i < allFsms.Length; i++)
                {
                    var fsm = allFsms[i];
                    if (fsm == null || !IsSceneObject(fsm.gameObject)) continue;
                    if (fsm.FsmName == "Removal" || fsm.FsmName == "Screw")
                    {
                        uniqueObjects.Add(fsm.gameObject);
                    }
                }

                var candidates = new List<IndexCandidate>(uniqueObjects.Count);
                foreach (var go in uniqueObjects)
                {
                    candidates.Add(new IndexCandidate
                    {
                        Go = go,
                        Id = GetObjectIdentifier(go),
                        Path = GetScenePath(go.transform),
                        SiblingIndex = go.transform.GetSiblingIndex()
                    });
                }

                candidates.Sort((a, b) =>
                {
                    int cmp = string.CompareOrdinal(a.Id, b.Id);
                    if (cmp != 0) return cmp;
                    cmp = string.CompareOrdinal(a.Path, b.Path);
                    if (cmp != 0) return cmp;
                    return a.SiblingIndex.CompareTo(b.SiblingIndex);
                });

                for (int i = 0; i < candidates.Count; i++)
                {
                    GetHash(candidates[i].Go);
                    count++;
                }
            }
            catch (Exception ex)
            {
                MSCLoader.ModConsole.Print($"[SceneHasher] Ошибка индексации: {ex.Message}");
            }
            return count;
        }

        /// <summary>
        /// Возвращает все зарегистрированные в данный момент объекты мира.
        /// </summary>
        public static IEnumerable<GameObject> GetAllRegisteredObjects()
        {
            return hashToGameObject.Values;
        }

        /// <summary>
        /// Очистка реестра при смене сцены или перезапуске.
        /// </summary>
        public static void Clear()
        {
            hashToGameObject.Clear();
            gameObjectToHash.Clear();
        }
    }
}
