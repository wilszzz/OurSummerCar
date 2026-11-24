using UnityEngine;

namespace OSC.Core.Bridges
{
    /// <summary>
    /// Событие-мост о посадке/высадке удалённого игрока в транспорт.
    /// Публикуется модулями транспорта/пассажиров, читается модулем OSC.Module.Players.
    /// </summary>
    public struct RemotePlayerSeatEvent
    {
        public int PlayerId;
        public bool IsSeated;
        public bool IsDriver;
        public int VehicleId;
        public int SeatIndex;
        public Transform VehicleTransform;
        public Transform SteeringForkTransform;
        public Vector3 LocalSeatPosition;
        public Quaternion LocalSeatRotation;
        public bool UsePassengerAnimation;
    }

    /// <summary>
    /// Событие-мост о статусе локального игрока за рулём / пассажиром.
    /// </summary>
    public struct LocalPlayerOccupancyEvent
    {
        public bool IsSeated;
        public bool IsDriver;
        public int VehicleId;
        public Transform VehicleTransform;
    }

    /// <summary>
    /// Событие-мост о позиции локального игрока для модулей мира/предметов.
    /// </summary>
    public struct LocalPlayerTransformUpdatedEvent
    {
        public Vector3 Position;
        public string Nickname;
    }

    /// <summary>
    /// Событие-мост о захвате/сбросе предмета игроком (чтобы модуль игроков рисовал его в руках, не зная деталей ItemManager).
    /// </summary>
    public struct PlayerHeldItemEvent
    {
        public int PlayerId;
        public bool IsHolding;
        public GameObject ItemObject;
        public int ItemHash;
    }

    /// <summary>
    /// Событие-мост для переноски спец-объектов (например, мопеда Jonnez) между модулем предметов и модулем мопеда.
    /// </summary>
    public struct SpecialObjectCarryEvent
    {
        public int PlayerId;
        public bool IsLocal;
        public bool IsCarried;
        public GameObject TargetObject;
    }
}
