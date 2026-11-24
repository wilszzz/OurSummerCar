using System;
using UnityEngine;

namespace OurSummerCar.Player
{
    /// <summary>
    /// Инверсная кинематика (FABRIK) для рук и ног модели персонажа по архитектуре WreckMP.
    /// Решает положение суставов цепи костей для удержания предметов в руках и позы в авто.
    /// </summary>
    public class FastIKFabric : MonoBehaviour
    {
        public static FastIKFabric CreateInstance(Transform lastBone, int length, Transform hint)
        {
            if (lastBone == null) return null;
            FastIKFabric fastIKFabric = lastBone.gameObject.AddComponent<FastIKFabric>();
            fastIKFabric.ChainLength = length;
            fastIKFabric.Pole = hint;
            GameObject targetObj = new GameObject(lastBone.name + "_target");
            targetObj.transform.position = lastBone.position;
            targetObj.transform.rotation = lastBone.rotation;
            fastIKFabric.Target = targetObj.transform;
            return fastIKFabric;
        }

        public int ChainLength = 2;
        public Transform Target;
        public Transform Pole;

        public int Iterations = 8;
        public float Delta = 0.001f;
        public float SnapBackStrength = 1f;
        public bool AllowIK = true;

        protected float[] BonesLength;
        protected float CompleteLength;
        protected Transform[] Bones;
        protected Vector3[] Positions;
        protected Vector3[] StartDirectionSucc;
        protected Quaternion[] StartRotationBone;
        protected Quaternion StartRotationTarget;
        protected Transform Root;

        private void Awake()
        {
            Init();
        }

        public void Init()
        {
            Bones = new Transform[ChainLength + 1];
            Positions = new Vector3[ChainLength + 1];
            BonesLength = new float[ChainLength];
            StartDirectionSucc = new Vector3[ChainLength + 1];
            StartRotationBone = new Quaternion[ChainLength + 1];
            Root = transform;

            for (int i = 0; i <= ChainLength; i++)
            {
                if (Root == null) return;
                Root = Root.parent;
            }

            if (Target == null)
            {
                GameObject tObj = new GameObject(gameObject.name + " Target");
                Target = tObj.transform;
                SetPositionRootSpace(Target, GetPositionRootSpace(transform));
            }
            StartRotationTarget = GetRotationRootSpace(Target);

            Transform current = transform;
            CompleteLength = 0f;

            for (int j = Bones.Length - 1; j >= 0; j--)
            {
                Bones[j] = current;
                StartRotationBone[j] = GetRotationRootSpace(current);

                if (j == Bones.Length - 1)
                {
                    StartDirectionSucc[j] = GetPositionRootSpace(Target) - GetPositionRootSpace(current);
                }
                else
                {
                    StartDirectionSucc[j] = GetPositionRootSpace(Bones[j + 1]) - GetPositionRootSpace(current);
                    BonesLength[j] = StartDirectionSucc[j].magnitude;
                    CompleteLength += BonesLength[j];
                }

                current = current.parent;
            }
        }

        private void LateUpdate()
        {
            ResolveIK();
        }

        private void ResolveIK()
        {
            if (Target == null || !AllowIK || Root == null) return;

            if (BonesLength == null || BonesLength.Length != ChainLength)
            {
                Init();
                if (BonesLength == null || BonesLength.Length != ChainLength) return;
            }

            for (int i = 0; i < Bones.Length; i++)
            {
                if (Bones[i] == null) return;
                Positions[i] = GetPositionRootSpace(Bones[i]);
            }

            Vector3 targetPosRoot = GetPositionRootSpace(Target);
            Quaternion targetRotRoot = GetRotationRootSpace(Target);

            // Если цель вне досягаемости — вытягиваемся в прямую линию
            if ((targetPosRoot - GetPositionRootSpace(Bones[0])).sqrMagnitude >= CompleteLength * CompleteLength)
            {
                Vector3 dir = (targetPosRoot - Positions[0]).normalized;
                for (int j = 1; j < Positions.Length; j++)
                {
                    Positions[j] = Positions[j - 1] + dir * BonesLength[j - 1];
                }
            }
            else
            {
                for (int k = 0; k < Positions.Length - 1; k++)
                {
                    Positions[k + 1] = Vector3.Lerp(Positions[k + 1], Positions[k] + StartDirectionSucc[k], SnapBackStrength);
                }

                for (int iter = 0; iter < Iterations; iter++)
                {
                    // Обратный проход (от конца к началу)
                    for (int m = Positions.Length - 1; m > 0; m--)
                    {
                        if (m == Positions.Length - 1)
                        {
                            Positions[m] = targetPosRoot;
                        }
                        else
                        {
                            Positions[m] = Positions[m + 1] + (Positions[m] - Positions[m + 1]).normalized * BonesLength[m];
                        }
                    }

                    // Прямой проход (от начала к концу)
                    for (int n = 1; n < Positions.Length; n++)
                    {
                        Positions[n] = Positions[n - 1] + (Positions[n] - Positions[n - 1]).normalized * BonesLength[n - 1];
                    }

                    if ((Positions[Positions.Length - 1] - targetPosRoot).sqrMagnitude < Delta * Delta)
                    {
                        break;
                    }
                }
            }

            // Коррекция плоскости сгиба суставов через Pole/Hint
            if (Pole != null)
            {
                Vector3 polePosRoot = GetPositionRootSpace(Pole);
                for (int num = 1; num < Positions.Length - 1; num++)
                {
                    Plane plane = new Plane(Positions[num + 1] - Positions[num - 1], Positions[num - 1]);
                    Vector3 closest = ClosestPointOnPlane(plane, polePosRoot);
                    float angle = SignedAngle(ClosestPointOnPlane(plane, Positions[num]) - Positions[num - 1], closest - Positions[num - 1], plane.normal);
                    Positions[num] = Quaternion.AngleAxis(angle, plane.normal) * (Positions[num] - Positions[num - 1]) + Positions[num - 1];
                }
            }

            // Применение вычисленных позиций и вращений к костям
            for (int b = 0; b < Positions.Length; b++)
            {
                if (b == Positions.Length - 1)
                {
                    // Для кисти / пальцев фиксируем естественное целевое вращение
                    if (Target != null)
                    {
                        Bones[b].rotation = Target.rotation;
                    }
                }
                else
                {
                    Vector3 dir = Positions[b + 1] - Positions[b];
                    if (dir.sqrMagnitude > 0.00001f && StartDirectionSucc[b].sqrMagnitude > 0.00001f)
                    {
                        Quaternion rot = Quaternion.FromToRotation(StartDirectionSucc[b], dir);
                        SetRotationRootSpace(Bones[b], rot * StartRotationBone[b]);
                    }
                }
                SetPositionRootSpace(Bones[b], Positions[b]);
            }
        }

        private Vector3 ClosestPointOnPlane(Plane plane, Vector3 point)
        {
            float dist = Vector3.Dot(plane.normal, point) + plane.distance;
            return point - plane.normal * dist;
        }

        public static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis)
        {
            float angle = Vector3.Angle(from, to);
            float crossX = from.y * to.z - from.z * to.y;
            float crossY = from.z * to.x - from.x * to.z;
            float crossZ = from.x * to.y - from.y * to.x;
            float sign = Mathf.Sign(axis.x * crossX + axis.y * crossY + axis.z * crossZ);
            return angle * sign;
        }

        private Vector3 GetPositionRootSpace(Transform cur)
        {
            if (Root == null || cur == null) return cur != null ? cur.position : Vector3.zero;
            return Quaternion.Inverse(Root.rotation) * (cur.position - Root.position);
        }

        private void SetPositionRootSpace(Transform cur, Vector3 pos)
        {
            if (Root == null || cur == null)
            {
                if (cur != null) cur.position = pos;
                return;
            }
            cur.position = Root.rotation * pos + Root.position;
        }

        private Quaternion GetRotationRootSpace(Transform cur)
        {
            if (Root == null || cur == null) return cur != null ? cur.rotation : Quaternion.identity;
            return Quaternion.Inverse(Root.rotation) * cur.rotation;
        }

        private void SetRotationRootSpace(Transform cur, Quaternion rot)
        {
            if (Root == null || cur == null)
            {
                if (cur != null) cur.rotation = rot;
                return;
            }
            cur.rotation = Root.rotation * rot;
        }

        private void OnDestroy()
        {
            if (Target != null && Target.gameObject.name.Contains("_target"))
            {
                Destroy(Target.gameObject);
            }
        }
    }
}
