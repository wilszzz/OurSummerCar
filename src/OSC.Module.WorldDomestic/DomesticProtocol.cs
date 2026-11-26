namespace OSC.Module.WorldDomestic
{
    /// <summary>
    /// Коды подсообщений и константы протокола Stage 5 (OSC.Module.WorldDomestic).
    /// Использует PacketType 25 (PlayerSleepSync) и 110..113.
    /// </summary>
    internal static class DomesticProtocol
    {
        // Подтипы для PlayerSleepSync (PacketType 25)
        public const byte SleepEnterState = 1;
        public const byte SleepExitState = 2;
        public const byte SleepExecuteSkip = 3;
        public const byte SleepSnapshot = 4;

        // Подтипы для DomesticPowerAndWaterSync (PacketType 110)
        public const byte PowerSwitchState = 1;
        public const byte FuseBoxState = 2;
        public const byte WaterTapState = 3;
        public const byte WellPumpAction = 4;
        public const byte PowerWaterSnapshot = 5;

        // Подтипы для DomesticSaunaAndFireSync (PacketType 111)
        public const byte SaunaStoveState = 1;
        public const byte SaunaWaterSteam = 2;
        public const byte SaunaPeeSteamBurn = 3;
        public const byte HouseFireState = 4;
        public const byte SaunaFireSnapshot = 5;

        // Подтипы для DomesticAppliancesAndStatsSync (PacketType 112)
        public const byte ApplianceFsmState = 1;
        public const byte GrillFoodState = 2;
        public const byte PlayerStatusEffect = 3;
        public const byte PlayerGearToggle = 4;
        public const byte AppliancesSnapshot = 5;

        // Подтипы для DomesticHazardsAndContainersSync (PacketType 113)
        public const byte WaspNestState = 1;
        public const byte ContainerLidState = 2;
        public const byte HazardsContainersSnapshot = 3;
    }
}
