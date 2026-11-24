using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSC.Module.Players
{
    public sealed class AnimatorSyncer : MonoBehaviour
    {
        public Transform sourceSkeleton;
        public Transform sourceSkeleton2;

        private readonly List<Transform> sourceBones = new List<Transform>();
        private readonly List<Transform> sourceBones2 = new List<Transform>();
        private readonly List<Transform> targetBones = new List<Transform>();

        private Transform[] fastSourceBones;
        private Transform[] fastSourceBones2;
        private Transform[] fastTargetBones;
        private byte[] boneRouteTypes;
        private int boneCount;

        private const byte ROUTE_SPINE = 0;
        private const byte ROUTE_HEAD = 1;
        private const byte ROUTE_LEFT_LEG = 2;
        private const byte ROUTE_RIGHT_LEG = 3;
        private const byte ROUTE_LEFT_ARM = 4;
        private const byte ROUTE_RIGHT_ARM = 5;
        private const byte ROUTE_PROCEDURAL = 6;

        private int pelvisEndIndex;

        public int head = 0;
        public int leftLeg = 1;
        public int rightLeg = 1;
        public int leftArm = 1;
        public int rightArm = 1;

        public bool lockStraightLegs = false;
        private Quaternion[] defaultTargetLocalRotations;
        private Vector3[] defaultTargetLocalPositions;
        private bool isInitialized;

        public void InitBones()
        {
            if (isInitialized || sourceSkeleton == null) return;

            sourceBones.Clear();
            sourceBones2.Clear();
            targetBones.Clear();

            Dictionary<string, Transform> s2BonesByName = null;
            if (sourceSkeleton2 != null)
            {
                s2BonesByName = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
                Transform[] allS2 = sourceSkeleton2.GetComponentsInChildren<Transform>(true);
                for (int s = 0; s < allS2.Length; s++)
                {
                    if (allS2[s] != null && !s2BonesByName.ContainsKey(allS2[s].name))
                    {
                        s2BonesByName.Add(allS2[s].name, allS2[s]);
                    }
                }
            }

            var pelvis = sourceSkeleton.Find("pelvis");
            if (pelvis != null) LoopBones(pelvis, "", s2BonesByName);
            pelvisEndIndex = sourceBones.Count;

            var thigL = sourceSkeleton.Find("thig_left");
            if (thigL != null) LoopBones(thigL, "", s2BonesByName);

            var thigR = sourceSkeleton.Find("thig_right");
            if (thigR != null) LoopBones(thigR, "", s2BonesByName);

            boneCount = sourceBones.Count;
            fastSourceBones = sourceBones.ToArray();
            fastSourceBones2 = sourceBones2.ToArray();
            fastTargetBones = targetBones.ToArray();
            boneRouteTypes = new byte[boneCount];
            defaultTargetLocalRotations = new Quaternion[boneCount];
            defaultTargetLocalPositions = new Vector3[boneCount];

            for (int i = 0; i < boneCount; i++)
            {
                if (fastTargetBones[i] != null)
                {
                    defaultTargetLocalRotations[i] = fastTargetBones[i].localRotation;
                    defaultTargetLocalPositions[i] = fastTargetBones[i].localPosition;
                }
            }

            for (int i = 0; i < boneCount; i++)
            {
                if (fastSourceBones[i] == null) continue;
                string boneName = fastSourceBones[i].name.ToLower();
                bool isLeg = i >= pelvisEndIndex;
                bool isHead = boneName.Contains("head");
                bool isLeft = boneName.Contains("left");
                bool isRight = boneName.Contains("right");
                bool isBend = boneName.Contains("bend");

                if (isBend) boneRouteTypes[i] = ROUTE_PROCEDURAL;
                else if (isLeft && isLeg) boneRouteTypes[i] = ROUTE_LEFT_LEG;
                else if (isRight && isLeg) boneRouteTypes[i] = ROUTE_RIGHT_LEG;
                else if (isLeft && !isLeg) boneRouteTypes[i] = ROUTE_LEFT_ARM;
                else if (isRight && !isLeg) boneRouteTypes[i] = ROUTE_RIGHT_ARM;
                else if (isHead) boneRouteTypes[i] = ROUTE_HEAD;
                else boneRouteTypes[i] = ROUTE_SPINE;
            }

            isInitialized = true;
        }

        private void LoopBones(Transform bone, string subPath, Dictionary<string, Transform> s2BonesByName)
        {
            if (!string.IsNullOrEmpty(subPath)) subPath += "/";
            subPath += bone.name;

            for (int i = 0; i < bone.childCount; i++)
            {
                LoopBones(bone.GetChild(i), subPath, s2BonesByName);
            }

            sourceBones.Add(bone);
            Transform s2 = null;
            if (sourceSkeleton2 != null)
            {
                s2 = sourceSkeleton2.Find(subPath);
                if (s2 == null && s2BonesByName != null) s2BonesByName.TryGetValue(bone.name, out s2);
            }
            sourceBones2.Add(s2);
            targetBones.Add(transform.Find(subPath));
        }

        public void SetLegChannels(int left, int right)
        {
            leftLeg = left;
            rightLeg = right;
        }

        public void ResetChannels()
        {
            head = 0;
            leftLeg = 1;
            rightLeg = 1;
            leftArm = 1;
            rightArm = 1;
        }

        private void LateUpdate()
        {
            if (!isInitialized) return;

            for (int i = 0; i < boneCount; i++)
            {
                Transform target = fastTargetBones[i];
                Transform source = fastSourceBones[i];
                if (target == null || source == null) continue;

                int route;
                switch (boneRouteTypes[i])
                {
                    case ROUTE_PROCEDURAL: route = 0; break;
                    case ROUTE_LEFT_LEG: route = leftLeg; break;
                    case ROUTE_RIGHT_LEG: route = rightLeg; break;
                    case ROUTE_LEFT_ARM: route = leftArm; break;
                    case ROUTE_RIGHT_ARM: route = rightArm; break;
                    case ROUTE_HEAD: route = head; break;
                    default: route = 1; break;
                }

                if (route == 1)
                {
                    target.localPosition = source.localPosition;
                    target.localRotation = source.localRotation;
                }
                else if (route == 2)
                {
                    Transform source2 = (fastSourceBones2 != null && i < fastSourceBones2.Length) ? fastSourceBones2[i] : null;
                    if (source2 != null)
                    {
                        target.localPosition = source2.localPosition;
                        target.localRotation = source2.localRotation;
                    }
                    else
                    {
                        target.localPosition = source.localPosition;
                        target.localRotation = source.localRotation;
                    }
                }
                else if (route == 0 && lockStraightLegs && (boneRouteTypes[i] == ROUTE_LEFT_LEG || boneRouteTypes[i] == ROUTE_RIGHT_LEG))
                {
                    if (defaultTargetLocalRotations != null && i < defaultTargetLocalRotations.Length)
                    {
                        target.localPosition = defaultTargetLocalPositions[i];
                        target.localRotation = defaultTargetLocalRotations[i];
                    }
                }
            }
        }
    }
}
