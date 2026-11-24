using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Vehicles
{
    /// <summary>
    /// Менеджер сетевой синхронизации автомобильной магнитолы (Radio / CD / Cassette).
    /// </summary>
    public class CarRadioManager : MonoBehaviour
    {
        public static CarRadioManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        private readonly List<PlayMakerFSM> trackedRadioFsms = new List<PlayMakerFSM>();
        private bool isSuppressingRadio = false;
        private float nextScanTime = 0f;

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            ScanRadios();
            OnLog?.Invoke("[Магнитола] Менеджер радио и музыки в автомобиле инициализирован");
        }

        public void Reset()
        {
            trackedRadioFsms.Clear();
            isSuppressingRadio = false;
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
            }
        }

        public void ScanRadios()
        {
            try
            {
                trackedRadioFsms.Clear();
                var radios = GameObject.FindObjectsOfType<PlayMakerFSM>();
                if (radios != null)
                {
                    for (int i = 0; i < radios.Length; i++)
                    {
                        var fsm = radios[i];
                        if (fsm != null && (fsm.FsmName == "Radio" || fsm.gameObject.name.IndexOf("radio", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            trackedRadioFsms.Add(fsm);

                            if (fsm.HasState("Radio OK"))
                            {
                                fsm.InsertAction("Radio OK", () =>
                                {
                                    if (isSuppressingRadio) return;
                                    SendRadioState(GetHierarchyPath(fsm.transform), true, 1f, 1f, 0);
                                }, 0, false);
                            }
                            if (fsm.HasState("Radio Out"))
                            {
                                fsm.InsertAction("Radio Out", () =>
                                {
                                    if (isSuppressingRadio) return;
                                    SendRadioState(GetHierarchyPath(fsm.transform), false, 0f, 0f, 0);
                                }, 0, false);
                            }
                            if (fsm.HasState("CD OK"))
                            {
                                fsm.InsertAction("CD OK", () =>
                                {
                                    if (isSuppressingRadio) return;
                                    SendRadioState(GetHierarchyPath(fsm.transform), true, 1f, 1f, 1);
                                }, 0, false);
                            }
                            if (fsm.HasState("CD Out"))
                            {
                                fsm.InsertAction("CD Out", () =>
                                {
                                    if (isSuppressingRadio) return;
                                    SendRadioState(GetHierarchyPath(fsm.transform), false, 0f, 0f, 0);
                                }, 0, false);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Магнитола] Ошибка сканирования радио: {ex.Message}");
            }
        }

        private void Update()
        {
            if (transport == null || !transport.IsRunning) return;

            if (Time.time > nextScanTime)
            {
                nextScanTime = Time.time + 10f;
                if (trackedRadioFsms.Count == 0)
                {
                    ScanRadios();
                }
            }
        }

        public void SendRadioState(string radioPath, bool power, float volume, float channel, int track)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.CarRadioSync))
            {
                packet.WriteString(radioPath);
                packet.WriteBoolean(power);
                packet.WriteSingle(volume);
                packet.WriteSingle(channel);
                packet.WriteInt32(track);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type != PacketType.CarRadioSync) return;

            string path = reader.ReadString();
            bool power = reader.ReadBoolean();
            float vol = reader.ReadSingle();
            float chan = reader.ReadSingle();
            int track = reader.ReadInt32();

            isSuppressingRadio = true;
            try
            {
                for (int i = 0; i < trackedRadioFsms.Count; i++)
                {
                    var fsm = trackedRadioFsms[i];
                    if (fsm != null)
                    {
                        var pVar = fsm.FsmVariables.FindFsmBool("Power");
                        if (pVar != null) pVar.Value = power;

                        var vVar = fsm.FsmVariables.FindFsmFloat("Volume");
                        if (vVar != null) vVar.Value = vol;

                        var cVar = fsm.FsmVariables.FindFsmFloat("Channel");
                        if (cVar != null) cVar.Value = chan;

                        fsm.SendEvent(power ? "RADIO ON" : "RADIO OFF");
                    }
                }
            }
            finally
            {
                isSuppressingRadio = false;
            }
        }

        private string GetHierarchyPath(Transform current)
        {
            if (current == null) return string.Empty;
            string path = current.name;
            while (current.parent != null)
            {
                current = current.parent;
                path = current.name + "/" + path;
            }
            return path;
        }
    }
}
