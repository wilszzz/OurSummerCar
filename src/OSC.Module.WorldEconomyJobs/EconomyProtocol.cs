namespace OSC.Module.WorldEconomyJobs
{
    /// <summary>
    /// Коды подсообщений и константы протокола Stage 6 (OSC.Module.WorldEconomyJobs).
    /// Использует PacketType 130..135.
    /// </summary>
    internal static class EconomyProtocol
    {
        // Подтипы для EconomyWalletAndStoreSync (PacketType 130)
        public const byte WalletSync = 1;
        public const byte TeimoRegisterAction = 2;
        public const byte StoreFuelPump = 3;
        public const byte StoreWindowBreak = 4;
        public const byte StoreSnapshot = 5;

        // Подтипы для PubAndGamblingSync (PacketType 131)
        public const byte PubBeerTap = 1;
        public const byte GamblingSlotOrPoker = 2;
        public const byte DartsAndLottery = 3;
        public const byte VenttiMatchState = 4;
        public const byte PubSnapshot = 5;

        // Подтипы для JobsSync (PacketType 132)
        public const byte SewageWellState = 1;
        public const byte SewageTruckLevel = 2;
        public const byte FirewoodAndSplitter = 3;
        public const byte KiljuBrewState = 4;
        public const byte StrawberryOrCombine = 5;
        public const byte JobsSnapshot = 6;

        // Подтипы для StoryAndQuestsSync (PacketType 133)
        public const byte TelephoneCallState = 1;
        public const byte PlayerVehicleKeys = 2;
        public const byte JokkeRideAndSuitcase = 3;
        public const byte GrandmaAndSuski = 4;
        public const byte HooligansAndFight = 5;
        public const byte StorySnapshot = 6;

        // Подтипы для NpcTrafficAndPoliceSync (PacketType 134)
        public const byte NpcVehiclePose = 1;
        public const byte PoliceCheckpointState = 2;
        public const byte JailAndFinesState = 3;
        public const byte RallyStageState = 4;
        public const byte NpcPoliceSnapshot = 5;

        // Подтипы для SavegameAndRespawnSync (PacketType 135)
        public const byte SaveTriggerNotice = 1;
        public const byte SaveFileChunk = 2;
        public const byte SaveFileComplete = 3;
        public const byte PlayerDeathNotice = 4;
        public const byte PlayerRespawnState = 5;
    }
}
