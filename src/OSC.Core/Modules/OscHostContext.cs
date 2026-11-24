using System;
using OSC.Core.Bridges;
using OSC.Core.Network;
using OSC.Core.Ownership;

namespace OSC.Core.Modules
{
    public sealed class OscHostContext
    {
        public NetworkTransport Transport { get; }
        public PacketRouter Router { get; }
        public OscEventBus Events { get; }
        public OwnershipRegistry Ownership { get; }
        public Action<string> Log { get; }

        public bool IsInGameWorld { get; set; }
        public string LocalNickname { get; set; } = "Player";

        public OscHostContext(
            NetworkTransport transport,
            PacketRouter router,
            OscEventBus events,
            OwnershipRegistry ownership,
            Action<string> log)
        {
            Transport = transport;
            Router = router;
            Events = events;
            Ownership = ownership;
            Log = log;
        }
    }
}
