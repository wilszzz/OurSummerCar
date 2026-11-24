namespace OurSummerCar.Network
{
    public enum PacketType : byte
    {
        None = 0,
        Ping = 1,
        Pong = 2,
        HandshakeRequest = 3,
        HandshakeResponse = 4,
        ChatMessage = 5,
        PlayerSync = 10,
        PlayerDisconnect = 11,
        DoorToggle = 20,
        DoorSnapshot = 21,
        GarageDoorSync = 22,
        GarageDoorAction = 23,
        RequestDoorSnapshot = 24,
        ItemPickUp = 30,
        ItemDrop = 31,
        ItemSync = 32,
        ContainerUnpack = 33,
        RequestItemSnapshot = 34,
        ItemSnapshot = 35,
        BeerCaseDrink = 36,
        SpawnItem = 37,
        ShoppingBagAction = 38,

        // Jerry Cans
        JerryCanLid = 40,
        JerryCanFluid = 41,

        // Tool Sets
        ToolSetToggle = 42,

        // House & Sauna
        LightSwitchToggle = 50,
        LightSwitchSnapshot = 51,
        SaunaKnobSync = 52,
        SaunaSimSync = 53,
        SaunaSteam = 54,

        // Vehicles
        VehicleDoorToggle = 60,
        VehicleDoorRotation = 61,
        VehicleDoorSnapshot = 62,
        VehicleDoorOwnership = 63,
        VehicleFuelSync = 64,
        VehiclePhysicsSync = 65,
        VehicleDrivingMode = 66,
        VehiclePassengerMode = 67,

        // Consumables (Food, Drinks, Spray, Extinguisher, Fluids, Cigarettes)
        ConsumableSync = 70,
        ConsumableAction = 71,
        ConsumableSnapshot = 72,

        // Vehicle Parts & Bolts Assembly (Satsuma)
        PartAssemble = 80,
        PartRemove = 81,
        PartBoltScrew = 82,
        PartAssembleSnapshot = 83,

        // Telephone & Quests
        TelephoneRing = 90,
        TelephoneCallAction = 91,

        // House & Yard Interactive (TV, Stove, Water, Wood Chopping)
        TVToggle = 100,
        StoveKnobSync = 101,
        WaterTapToggle = 102,
        WoodLogAction = 103,

        // Jobs, Quests & Economy (Firewood, Sewage, Kilju, Grandma, Store, Money)
        JobAction = 110,
        JobSync = 111,
        MoneySync = 112,
        StoreShelfSync = 113,
        StoreProductAction = 114,
        StoreRegisterAction = 115,
        MoneyDelta = 116,

        // Garage Heavy Equipment & Tools (Floor Jack, Motor Hoist, Lantern)
        FloorJackSync = 120,
        MotorHoistSync = 121,
        LanternSync = 122,
        HeavyEquipmentTransformSync = 123,

        // Co-op Sleep & House Fire
        SleepVote = 130,
        SleepExecute = 131,
        HouseFireSync = 132,
        TimeWeatherSync = 133,

        // Gambling Machines (Video Poker & Slot Machine)
        GamblingAction = 140,
        GamblingMachineState = 141,

        // Store Robbery & Windows
        StoreBreakIn = 150,
        StoreWindowSync = 151,

        // Quests: Moving Jokke & Fleetari Vandalism
        MovingQuestAction = 160,
        SewageSpillAction = 161,
        UncleStorySync = 162,
        JailSentenceSync = 163,
        FighterBrawlSync = 164,
        PlayerPhysiologySync = 165,
        LotteryDrawSync = 166,
        CaravanTrailerSync = 167,
        FishingMinigameSync = 168,
        WristwatchSync = 169,

        // Player Actions (Urine, Swear, Gestures)
        PlayerUrinate = 170,
        PlayerSwear = 171,
        PlayerGesture = 172,
        GifuHoseSync = 173,
        GearboxGrindSync = 174,
        CrashMemorialSync = 175,
        LicensePlatesSync = 176,
        TVProgramSync = 177,

        // Vehicle Interactions (Ignition, Lights, Hydraulics, Trailer, Boat)
        VehicleIgnition = 180,
        VehicleSwitchToggle = 181,
        VehicleHydraulics = 182,
        VehicleTrailerHitch = 183,
        PlayerKeysSync = 184,

        // World & Appliances (Fuel Pumps, Fuses, Coffee, Fireplace, Computer)
        FuelPumpAction = 190,
        FuseBoxAction = 191,
        CoffeeBrewAction = 192,
        FireplaceAction = 193,
        ComputerAction = 194,

        // Expanded Quests & NPCs (Haybales, Strawberry, Wrecks, Grandma, Ventti, Police)
        ExpandedQuestAction = 200,
        VenttiAction = 201,
        PoliceAction = 202,
        NpcTrafficSync = 205,
        NpcPoliceSync = 206,

        // World Savegame Synchronization
        SavegameRequest = 210,
        SavegameData = 211,
        SavegameComplete = 212,

        // Towing Rope & Crash Damage
        TowingRopeSync = 220,
        VehicleCrashSync = 221,
        VehicleRepairSync = 222,

        // Fleetari Repair Shop, Catalog & N2O
        FleetariOrderSync = 225,
        CatalogOrderSync = 226,
        NitrousBoostSync = 227,

        // Pub Nappo, Jukebox, Cottage & Boat
        PubNappoSync = 230,
        JukeboxSync = 231,
        BoatEngineSync = 232,
        CottageSync = 233,
        FishTrapSync = 234,

        // Rally, Drag Racing, Police & Wildlife
        RallyRaceSync = 240,
        DragRaceSync = 241,
        PoliceFineSync = 242,
        WildlifeSync = 243,

        // Car Radio & Audio Player
        CarRadioSync = 250,

        // Satsuma Deep Engine Simulation & Fluids
        VehicleFluidSync = 213,
        VehicleWearSync = 214,
        TirePressureSync = 215,
        VehicleInspectionSync = 216,

        // Storylines & NPC Quests
        JokkeStorySync = 217,
        PenaHitchhikeSync = 218,
        VenttiGameSync = 219,
        SuskiStorySync = 223,
        DancePavilionSync = 224,
        GrandmaStorySync = 228,

        // World Hazards & Environment
        WaspNestSync = 229,
        TrainBusSync = 235,
        ThunderstormSync = 236,
        HouseBurnSync = 237,
        PoliceRoadblockSync = 238,

        // Domestic & Minigames
        Computer386Sync = 239,
        StrawberryJobSync = 244,
        CombineHarvesterSync = 245,
        CoffeeBrewSync = 246,
        RadarDetectorSync = 247
    }

    public enum ShoppingBagActionType : byte
    {
        SpawnOne = 1,
        SpawnAll = 2,
        UpdateList = 3,
        Destroy = 4
    }

    public enum ConsumableActionType : byte
    {
        ConsumeBite = 1,
        ConsumeDrink = 2,
        Empty = 3,
        Destroy = 4,
        FluidLevel = 5,
        CigaretteTaken = 6
    }

    public enum JobActionType : byte
    {
        FirewoodDelivered = 1,
        FirewoodPaid = 2,
        SewageWellPumping = 3,
        SewageWellEmpty = 4,
        SewageWellPaid = 5,
        KiljuFermenting = 6,
        KiljuBottleSold = 7,
        GrandmaFoodDelivered = 8,
        InspectionPaid = 9,
        InspectionDone = 10,
        FlyerDelivered = 11
    }

    public enum GamblingActionType : byte
    {
        InsertCoin = 1,
        Bet = 2,
        Deal = 3,
        Hold = 4,
        DoubleChoice = 5,
        TakeWin = 6,
        SlotSpin = 7,
        MachineBreakTowed = 8
    }

    public enum MovingQuestActionType : byte
    {
        CallAccepted = 1,
        ItemDelivered = 2,
        QuestFinished = 3,
        SuitcaseMoved = 4,
        SuitcaseStolen = 5
    }

    public enum VehicleSwitchType : byte
    {
        Headlights = 1,
        HighBeams = 2,
        Hazard = 3,
        BlinkersLeft = 4,
        BlinkersRight = 5,
        Wipers = 6,
        Choke = 7,
        InteriorLight = 8,
        Horn = 9,
        Handbrake = 10
    }

    public enum ExpandedQuestType : byte
    {
        HaybaleDelivered = 1,
        HaybalePaid = 2,
        StrawberryHarvested = 3,
        StrawberryPaid = 4,
        JunkcarDelivered = 5,
        GrandmaGroceries = 6,
        VenttiCardDeal = 7,
        VenttiGameEnd = 8,
        PoliceBlowTest = 9,
        PoliceFineIssued = 10
    }
}
