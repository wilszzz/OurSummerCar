using System;
using System.Collections.Generic;

namespace OSC.Core.Ownership
{
    /// <summary>
    /// Реестр эксклюзивного владения Unity-объектами (Rigidbody, PlayMakerFSM и др.).
    /// Гарантирует, что два разных модуля никогда не начнут одновременно управлять одной физикой или FSM.
    /// </summary>
    public sealed class OwnershipRegistry
    {
        private readonly Dictionary<int, string> objectOwners = new Dictionary<int, string>();
        private readonly Action<string> log;

        public OwnershipRegistry(Action<string> log)
        {
            this.log = log;
        }

        public bool TryClaim(UnityEngine.Object target, string moduleId, string context = null)
        {
            if (target == null || string.IsNullOrEmpty(moduleId)) return false;

            int id = target.GetInstanceID();
            if (objectOwners.TryGetValue(id, out string currentOwner))
            {
                if (currentOwner == moduleId) return true;

                log?.Invoke($"[Ownership] Конфликт владения '{target.name}' ({target.GetType().Name}, {context}): уже занят модулем '{currentOwner}', отказ '{moduleId}'.");
                return false;
            }

            objectOwners[id] = moduleId;
            return true;
        }

        public void Release(UnityEngine.Object target, string moduleId)
        {
            if (target == null) return;
            int id = target.GetInstanceID();
            if (objectOwners.TryGetValue(id, out string currentOwner) && currentOwner == moduleId)
            {
                objectOwners.Remove(id);
            }
        }

        public string GetOwner(UnityEngine.Object target)
        {
            if (target == null) return null;
            return objectOwners.TryGetValue(target.GetInstanceID(), out string owner) ? owner : null;
        }

        public void ReleaseModule(string moduleId)
        {
            var toRemove = new List<int>();
            foreach (var kv in objectOwners)
            {
                if (kv.Value == moduleId)
                {
                    toRemove.Add(kv.Key);
                }
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                objectOwners.Remove(toRemove[i]);
            }
        }

        public void Clear()
        {
            objectOwners.Clear();
        }
    }
}
