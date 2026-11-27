namespace OSC.Core.Network
{
    /// <summary>
    /// Типы сетевых пакетов, жёстко разделённые по диапазонам модулей.
    /// </summary>
    public enum PacketType : byte
    {
        // 0..19: Ядро и соединение
        Ping = 1,
        Pong = 2,
        HandshakeRequest = 3,
        HandshakeResponse = 4,
        WorldReadyResync = 5,

        // 20..39: Модуль игроков и анимаций
        PlayerSync = 20,
        PlayerGesture = 21,
        PlayerUrinate = 22,
        PlayerSwear = 23,
        PlayerAppearance = 24,
        PlayerSleepSync = 25,

        // 40..49: Модуль времени и погоды
        TimeWeatherSync = 40,

        // 50..59: Модуль дверей мира и гаража
        DoorToggle = 50,
        DoorSnapshot = 51,
        GarageDoorSync = 52,

        // 60..89: Модули транспорта (подготовка к Этапу 2)
        VehiclePhysicsSync = 60,
        VehicleDrivingMode = 61,
        VehicleDoorToggle = 62,
        VehicleDoorRotation = 63,
        VehicleDoorOwnership = 64,
        VehicleDoorSnapshot = 65,
        VehiclePassengerSeat = 66,
        VehiclePassengerSnapshot = 67,
        VehicleControlSync = 68,
        VehicleSystemToggle = 69,
        BoatEngineSync = 70,
        VehicleFuelSync = 71,
        VehicleRadioSync = 72,
        VehicleTowingSync = 73,
        VehicleDamageSync = 74,
        VehicleFluidWearSync = 75,
        JonnezStateSync = 76,
        BoatMooringSync = 77,

        // 90..109: Модули предметов и сборки Satsuma (Этап 2 + Этап 4)
        ItemPickUp = 90,
        ItemDrop = 91,
        ItemSync = 92,
        PartAssemble = 93,
        PartRemove = 94,
        PartBoltScrew = 95,
        PartAssembleSnapshot = 96,
        SatsumaTuningSync = 97,
        SatsumaFluidsAndWearSync = 98,
        SatsumaFleetariAndCatalogSync = 99,

        // 110..129: Модуль дома, быта, сна и сауны (Этап 5)
        DomesticPowerAndWaterSync = 110,
        DomesticSaunaAndFireSync = 111,
        DomesticAppliancesAndStatsSync = 112,
        DomesticHazardsAndContainersSync = 113,

        // 130..149: Модуль экономики, работ, NPC, полиции и сохранений (Этап 6)
        EconomyWalletAndStoreSync = 130,
        PubAndGamblingSync = 131,
        JobsSync = 132,
        StoryAndQuestsSync = 133,
        NpcTrafficAndPoliceSync = 134,
        SavegameAndRespawnSync = 135
    }
}
