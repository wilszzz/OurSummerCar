using HutongGames.PlayMaker;
using UnityEngine;

namespace OSC.Module.Vehicles.Doors
{
    internal sealed class TrackedVehicleDoor
    {
        public int Hash;
        public string VehicleKey;
        public PlayMakerFSM Fsm;
        public FsmBool DoorOpenVar;
        public GameObject DoorObject;
        public Rigidbody DoorRb;
        public bool DefaultKinematic;
        public bool CanControlRb;

        public int Axis = 1; // 0=X, 1=Y, 2=Z
        public float ClosedAngle;
        public Vector3 ClosedLocalPos;
        public bool HasClosedAngle;
        public bool HasClosedLocalPos;
        public float LastClosedTransitionTime = -10f;

        public bool IsHayosikoSidedoor;
        public bool IsHoodOrBoot;
        public bool IsOpen;

        public int OwnerId; // По умолчанию хост (0)
        public bool SuppressEcho;
        public float SuppressEchoUntil;
        public float LastRemoteToggleTime = -10f;
        public float LastLocalActionTime = -10f;

        public float LastSentAngle = -999f;
        public Vector3 LastSentLocalPos;
        public bool HasLastSentLocalPos;

        public float TargetAngle = -999f;
        public Vector3 TargetLocalPos;
        public bool HasRemoteTarget;
        public float LastRemoteUpdateTime = -10f;

        public float SmoothVelocity;
        public bool IsApplyingRemote;

        public float SuppressOutboundUntil = -10f;
        public float SuppressRemoteInterpUntil = -10f;
        public bool HasOpenEvent;
        public bool HasCloseEvent;
    }
}
