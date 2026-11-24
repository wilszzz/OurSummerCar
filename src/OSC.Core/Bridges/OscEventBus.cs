using System;
using System.Collections.Generic;

namespace OSC.Core.Bridges
{
    /// <summary>
    /// Лёгкая шина событий (мост) для общения изолированных модулей без прямых ссылок друг на друга.
    /// </summary>
    public sealed class OscEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> subscribers = new Dictionary<Type, List<Delegate>>();
        private readonly Action<string> log;

        public OscEventBus(Action<string> log)
        {
            this.log = log;
        }

        public void Subscribe<TEvent>(Action<TEvent> handler)
        {
            if (handler == null) return;
            Type type = typeof(TEvent);
            if (!subscribers.TryGetValue(type, out var list))
            {
                list = new List<Delegate>();
                subscribers[type] = list;
            }
            if (!list.Contains(handler))
            {
                list.Add(handler);
            }
        }

        public void Unsubscribe<TEvent>(Action<TEvent> handler)
        {
            if (handler == null) return;
            if (subscribers.TryGetValue(typeof(TEvent), out var list))
            {
                list.Remove(handler);
            }
        }

        public void Publish<TEvent>(TEvent evt)
        {
            if (!subscribers.TryGetValue(typeof(TEvent), out var list) || list.Count == 0) return;

            var snapshot = list.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    ((Action<TEvent>)snapshot[i])?.Invoke(evt);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[EventBus] Ошибка обработчика {typeof(TEvent).Name}: {ex.Message}");
                }
            }
        }

        public void ClearAll()
        {
            subscribers.Clear();
        }
    }
}
