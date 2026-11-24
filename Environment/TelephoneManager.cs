using System;
using OurSummerCar.Jobs;
using OurSummerCar.Network;

namespace OurSummerCar.Environment
{
    /// <summary>
    /// Фасад сетевого менеджера домашнего телефона в My Summer Car.
    /// Перенаправляет вызовы в StorylinesAndQuestsManager для синхронизации
    /// звонков, снятия/повешения трубки и квестовых маркеров на карте (дрова, септики, Йокке).
    /// </summary>
    public class TelephoneManager
    {
        public static TelephoneManager Instance { get; private set; }

        private readonly NetworkTransport transport;
        public event Action<string> OnLog;

        public TelephoneManager(NetworkTransport transport)
        {
            Instance = this;
            this.transport = transport;
        }

        public void InitializeTelephone()
        {
            StorylinesAndQuestsManager.Instance?.InitializeTelephone();
            OnLog?.Invoke("[Телефон] Инициализирован через StorylinesAndQuestsManager");
        }

        public void Reset()
        {
        }
    }
}
