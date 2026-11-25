using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSC.Module.Vehicles
{
    public sealed class CollisionAndExitGuard
    {
        private struct IgnoredColliderPair
        {
            public Collider First;
            public Collider Second;
        }

        private readonly List<IgnoredColliderPair> localVehicleIgnoredPairs = new List<IgnoredColliderPair>();
        private readonly List<IgnoredColliderPair> persistentIgnoredPairs = new List<IgnoredColliderPair>();
        private readonly HashSet<int> registeredPuppetColliderIds = new HashSet<int>();
        private bool jonnezPlayerIgnored;

        private float restoreLocalCollisionsAt = -1f;

        // Пост-высадочная защита от проваливания под дорогу
        private Transform guardPlayerTransform;
        private TrackedVehicle guardVehicle;
        private float groundGuardUntil = -1f;
        private float nextPuppetScanTime;

        public void Reset()
        {
            RestoreLocalVehicleCollisionsImmediate();
            persistentIgnoredPairs.Clear();
            registeredPuppetColliderIds.Clear();
            jonnezPlayerIgnored = false;
            restoreLocalCollisionsAt = -1f;
            guardPlayerTransform = null;
            guardVehicle = null;
            groundGuardUntil = -1f;
            nextPuppetScanTime = 0f;
        }

        public void SetLocalVehicleCollisionsIgnored(TrackedVehicle veh, Transform playerTr, bool ignore, float graceSecondsAfterExit = 0.65f)
        {
            if (ignore)
            {
                restoreLocalCollisionsAt = -1f;
                RestoreLocalVehicleCollisionsImmediate();

                if (veh == null || playerTr == null) return;
                veh.RefreshSolidColliders();

                var playerCols = playerTr.GetComponentsInChildren<Collider>(true);
                for (int p = 0; p < playerCols.Length; p++)
                {
                    Collider pc = playerCols[p];
                    if (pc == null) continue;

                    for (int v = 0; v < veh.CachedSolidColliders.Count; v++)
                    {
                        Collider vc = veh.CachedSolidColliders[v];
                        if (vc == null || vc.isTrigger) continue;
                        if (SetIgnoreSafe(pc, vc, true))
                        {
                            localVehicleIgnoredPairs.Add(new IgnoredColliderPair { First = pc, Second = vc });
                        }
                    }
                }
            }
            else
            {
                if (graceSecondsAfterExit <= 0f)
                {
                    RestoreLocalVehicleCollisionsImmediate();
                }
                else
                {
                    restoreLocalCollisionsAt = Time.time + graceSecondsAfterExit;
                }
            }
        }

        public void RestoreLocalVehicleCollisionsImmediate()
        {
            restoreLocalCollisionsAt = -1f;
            for (int i = 0; i < localVehicleIgnoredPairs.Count; i++)
            {
                var pair = localVehicleIgnoredPairs[i];
                SetIgnoreSafe(pair.First, pair.Second, false);
            }
            localVehicleIgnoredPairs.Clear();
        }

        public void TickUpdate(IList<TrackedVehicle> vehicles, Transform playerTr)
        {
            if (restoreLocalCollisionsAt > 0f && Time.time >= restoreLocalCollisionsAt)
            {
                RestoreLocalVehicleCollisionsImmediate();
            }

            if (Time.time >= nextPuppetScanTime)
            {
                nextPuppetScanTime = Time.time + 0.75f;
                MaintainJonnezAndPuppetImmunity(vehicles, playerTr);
            }
        }

        public void StartPostExitGroundGuard(Transform playerTr, TrackedVehicle veh, float durationSeconds = 0.45f)
        {
            if (playerTr == null) return;
            guardPlayerTransform = playerTr;
            guardVehicle = veh;
            groundGuardUntil = Time.time + durationSeconds;
            EnforcePlayerUprightAndCameraReset(playerTr);
            TickGroundGuard();
        }

        public void TickGroundGuard()
        {
            if (guardPlayerTransform == null || Time.time > groundGuardUntil)
            {
                guardPlayerTransform = null;
                guardVehicle = null;
                return;
            }

            EnforcePlayerUprightAndCameraReset(guardPlayerTransform);

            float bottomOffset = GetPlayerCapsuleBottomOffset(guardPlayerTransform);
            Vector3 pos = guardPlayerTransform.position;
            Transform excludeRoot = guardVehicle != null ? guardVehicle.Transform : null;

            if (TryFindGroundBelow(pos + Vector3.up * 2.0f, 5.5f, guardPlayerTransform, excludeRoot, out float groundY))
            {
                if (pos.y < groundY + bottomOffset - 0.04f)
                {
                    guardPlayerTransform.position = new Vector3(pos.x, groundY + bottomOffset + 0.05f, pos.z);
                }
            }
        }

        public Vector3 FindSafeExitPoint(TrackedVehicle veh, Transform playerTr, float preferredSideSign, Vector3 fallbackWorldPos)
        {
            if (veh == null || veh.Transform == null)
            {
                return fallbackWorldPos;
            }

            Transform vt = veh.Transform;
            float sideDist = Mathf.Max(1.2f, veh.Spec.ExitSideHalfWidth);
            float rearDist = Mathf.Max(2.2f, veh.Spec.ExitRearHalfLength);
            float bottomOffset = GetPlayerCapsuleBottomOffset(playerTr);

            var candidates = new List<Vector3>(5);
            if (preferredSideSign > 0.1f)
            {
                candidates.Add(vt.TransformPoint(new Vector3(sideDist, 0.4f, 0.25f)));
                candidates.Add(vt.TransformPoint(new Vector3(-sideDist, 0.4f, 0.25f)));
                candidates.Add(vt.TransformPoint(new Vector3(0f, 0.4f, -rearDist)));
            }
            else if (preferredSideSign < -0.1f)
            {
                candidates.Add(vt.TransformPoint(new Vector3(-sideDist, 0.4f, 0.25f)));
                candidates.Add(vt.TransformPoint(new Vector3(sideDist, 0.4f, 0.25f)));
                candidates.Add(vt.TransformPoint(new Vector3(0f, 0.4f, -rearDist)));
            }
            else
            {
                // Задняя дверь / платформа
                candidates.Add(vt.TransformPoint(new Vector3(0f, 0.4f, -rearDist)));
                candidates.Add(vt.TransformPoint(new Vector3(sideDist, 0.4f, -0.4f)));
                candidates.Add(vt.TransformPoint(new Vector3(-sideDist, 0.4f, -0.4f)));
            }
            candidates.Add(vt.TransformPoint(new Vector3(0f, 0.4f, rearDist)));
            candidates.Add(vt.position + Vector3.up * 2.2f);

            for (int i = 0; i < candidates.Count; i++)
            {
                Vector3 candidate = candidates[i];
                if (TryFindGroundBelow(candidate + Vector3.up * 2.2f, 9.0f, playerTr, vt, out float groundY))
                {
                    // Игнорируем крыши зданий слишком высоко над машиной
                    if (groundY <= vt.position.y + 2.4f)
                    {
                        candidate.y = groundY + bottomOffset + 0.06f;
                    }
                    else
                    {
                        candidate.y = vt.position.y + bottomOffset + 0.25f;
                    }
                }
                else
                {
                    // Над глубокой водой (лодка на озере): ставим чуть выше уровня воды/борта
                    candidate.y = vt.position.y + bottomOffset + 0.25f;
                }

                if (!IsBlockedByWorldSolid(candidate, vt, playerTr))
                {
                    return candidate;
                }
            }

            Vector3 safeFallback = vt.position + Vector3.up * (bottomOffset + 2.2f);
            if (TryFindGroundBelow(vt.position + Vector3.up * 2.5f, 8.0f, playerTr, vt, out float fbGroundY) &&
                fbGroundY <= vt.position.y + 2.0f)
            {
                safeFallback = new Vector3(fallbackWorldPos.x, fbGroundY + bottomOffset + 0.08f, fallbackWorldPos.z);
            }
            return safeFallback;
        }

        public static void EnforcePlayerUprightAndCameraReset(Transform playerTr)
        {
            if (playerTr == null) return;

            Vector3 pe = playerTr.eulerAngles;
            playerTr.rotation = Quaternion.Euler(0f, pe.y, 0f);

            Transform pivot = playerTr.Find("Pivot");
            if (pivot != null)
            {
                Vector3 le = pivot.localEulerAngles;
                if (Mathf.Abs(le.z) > 0.01f)
                {
                    pivot.localEulerAngles = new Vector3(le.x, le.y, 0f);
                }
            }

            Transform animPivot = playerTr.Find("Pivot/AnimPivot");
            if (animPivot != null)
            {
                animPivot.localPosition = Vector3.zero;
                animPivot.localRotation = Quaternion.identity;
            }

            Transform camNode = playerTr.Find("Pivot/AnimPivot/Camera");
            if (camNode != null)
            {
                camNode.localRotation = Quaternion.identity;
            }

            Transform fpsCam = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera");
            if (fpsCam != null)
            {
                Vector3 fe = fpsCam.localEulerAngles;
                fpsCam.localEulerAngles = new Vector3(fe.x, 0f, 0f);
                fpsCam.localPosition = new Vector3(0f, 1.4f, 0f);
            }

            Transform mainCamChild = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera");
            if (mainCamChild != null)
            {
                mainCamChild.localRotation = Quaternion.identity;
            }

            if (Camera.main != null && Camera.main.transform != null && Camera.main.transform.IsChildOf(playerTr))
            {
                Vector3 ce = Camera.main.transform.localEulerAngles;
                Camera.main.transform.localEulerAngles = new Vector3(ce.x, ce.y, 0f);
            }
        }

        private void MaintainJonnezAndPuppetImmunity(IList<TrackedVehicle> vehicles, Transform playerTr)
        {
            if (vehicles == null) return;

            // 1. Постоянное отключение коллизий игрока с рамой мопеда Jonnez (предотвращает подлёт)
            if (!jonnezPlayerIgnored && playerTr != null)
            {
                TrackedVehicle jonnez = null;
                for (int i = 0; i < vehicles.Count; i++)
                {
                    if (vehicles[i].Kind == VehicleKind.Jonnez && vehicles[i].IsBound)
                    {
                        jonnez = vehicles[i];
                        break;
                    }
                }

                if (jonnez != null)
                {
                    var pCols = playerTr.GetComponentsInChildren<Collider>(true);
                    if (pCols.Length > 0 && jonnez.CachedSolidColliders.Count > 0)
                    {
                        for (int p = 0; p < pCols.Length; p++)
                        {
                            for (int j = 0; j < jonnez.CachedSolidColliders.Count; j++)
                            {
                                if (SetIgnoreSafe(pCols[p], jonnez.CachedSolidColliders[j], true))
                                {
                                    persistentIgnoredPairs.Add(new IgnoredColliderPair
                                    {
                                        First = pCols[p],
                                        Second = jonnez.CachedSolidColliders[j]
                                    });
                                }
                            }
                        }
                        jonnezPlayerIgnored = true;
                    }
                }
            }

            // 2. Любые коллайдеры под RemotePuppet_* (например, удерживаемые предметы) не должны сталкиваться с авто и игроком
            var puppetRoots = new[] { GameObject.Find("RemotePuppet_Host"), GameObject.Find("RemotePuppet_Client_1") };
            for (int r = 0; r < puppetRoots.Length; r++)
            {
                GameObject root = puppetRoots[r];
                if (root == null) continue;

                Collider[] cols = root.GetComponentsInChildren<Collider>(true);
                for (int c = 0; c < cols.Length; c++)
                {
                    Collider pupCol = cols[c];
                    if (pupCol == null || pupCol.isTrigger) continue;
                    int cid = pupCol.GetInstanceID();
                    if (registeredPuppetColliderIds.Contains(cid)) continue;

                    registeredPuppetColliderIds.Add(cid);

                    if (playerTr != null)
                    {
                        var pCols = playerTr.GetComponentsInChildren<Collider>(true);
                        for (int p = 0; p < pCols.Length; p++)
                        {
                            SetIgnoreSafe(pupCol, pCols[p], true);
                        }
                    }

                    for (int v = 0; v < vehicles.Count; v++)
                    {
                        var veh = vehicles[v];
                        if (!veh.IsBound) continue;
                        for (int vc = 0; vc < veh.CachedSolidColliders.Count; vc++)
                        {
                            SetIgnoreSafe(pupCol, veh.CachedSolidColliders[vc], true);
                        }
                    }
                }
            }
        }

        private static float GetPlayerCapsuleBottomOffset(Transform playerTr)
        {
            if (playerTr == null) return 0.95f;
            var cc = playerTr.GetComponent<CharacterController>();
            if (cc == null) return 0.95f;
            return Mathf.Max(0.25f, cc.height * 0.5f - cc.center.y);
        }

        private static bool TryFindGroundBelow(Vector3 rayOrigin, float maxDistance, Transform playerTr, Transform excludeVehRoot, out float highestGroundY)
        {
            highestGroundY = 0f;
            bool found = false;

            RaycastHit[] hits = Physics.RaycastAll(rayOrigin, Vector3.down, maxDistance);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null || col.isTrigger) continue;
                if (playerTr != null && (col.transform == playerTr || col.transform.IsChildOf(playerTr))) continue;
                if (excludeVehRoot != null && (col.transform == excludeVehRoot || col.transform.IsChildOf(excludeVehRoot))) continue;

                float y = hits[i].point.y;
                if (!found || y > highestGroundY)
                {
                    highestGroundY = y;
                    found = true;
                }
            }

            return found;
        }

        private static bool IsBlockedByWorldSolid(Vector3 worldFeetAnchor, Transform excludeVehRoot, Transform playerTr)
        {
            Vector3 lowSphere = worldFeetAnchor + Vector3.up * 0.1f;
            Vector3 highSphere = worldFeetAnchor + Vector3.up * 0.85f;
            return HasSolidOverlap(lowSphere, 0.36f, excludeVehRoot, playerTr)
                || HasSolidOverlap(highSphere, 0.36f, excludeVehRoot, playerTr);
        }

        private static bool HasSolidOverlap(Vector3 center, float radius, Transform excludeVehRoot, Transform playerTr)
        {
            Collider[] overlaps = Physics.OverlapSphere(center, radius);
            for (int i = 0; i < overlaps.Length; i++)
            {
                Collider col = overlaps[i];
                if (col == null || col.isTrigger) continue;
                if (playerTr != null && (col.transform == playerTr || col.transform.IsChildOf(playerTr))) continue;
                if (excludeVehRoot != null && (col.transform == excludeVehRoot || col.transform.IsChildOf(excludeVehRoot))) continue;
                return true;
            }
            return false;
        }

        private static bool SetIgnoreSafe(Collider a, Collider b, bool ignore)
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
