namespace OSC.Module.Satsuma
{
    /// <summary>
    /// Коды подсообщений и константы протокола Stage 4 (OSC.Module.Satsuma).
    /// Использует PacketType 93..99.
    /// </summary>
    internal static class SatsumaProtocol
    {
        // Подтипы для SatsumaTuningSync (PacketType 97)
        public const byte TuningDelta = 1;
        public const byte TuningSnapshot = 2;

        // Подтипы для SatsumaFluidsAndWearSync (PacketType 98)
        public const byte FluidsDelta = 1;
        public const byte FluidsSnapshot = 2;
        public const byte WearDelta = 3;
        public const byte WearSnapshot = 4;
        public const byte BatteryState = 5;

        // Подтипы для SatsumaFleetariAndCatalogSync (PacketType 99)
        public const byte FleetariOrder = 1;
        public const byte FleetariJobStatus = 2;
        public const byte FerndaleKeys = 3;
        public const byte DynoState = 4;
        public const byte CatalogOrder = 5;
        public const byte EnvelopePost = 6;
        public const byte N2OValve = 7;
        public const byte InspectionResult = 8;
        public const byte FleetariSnapshot = 9;
    }
}
