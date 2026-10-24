using Astek.EventSystems.Core;

namespace Astek.EventSystems
{
    public static class EventManager
    {
        public delegate void EventDelegate<in T>(T e) where T : IEvent;
        public static EventBus Global { get; } = new EventBus();
        public static Subscription AddListener<T>(EventDelegate<T> handler, int priority = 0) where T : IEvent
            => Global.AddListener(handler, priority);

        public static Subscription AddListenerOnce<T>(EventDelegate<T> handler, int priority = 0) where T : IEvent
            => Global.AddListenerOnce(handler, priority);

        public static bool RemoveListener<T>(EventDelegate<T> handler) where T : IEvent
            => Global.RemoveListener(handler);

        public static bool HasListeners<T>(EventDelegate<T> handler) where T : IEvent
            => Global.HasListeners(handler);

        public static bool HasListeners<T>() where T : IEvent
            => Global.HasListeners<T>();

        public static bool TriggerEvent<T>(T e) where T : IEvent
            => Global.TriggerEvent(e);

        public static bool TriggerEvent(IEvent e)
            => Global.TriggerEvent(e);

        public static bool QueueEvent<T>(T e) where T : IEvent
            => Global.QueueEvent(e);

        public static bool QueueEvent(IEvent e)
            => Global.QueueEvent(e);

        public static void Post(IEvent e) => Global.Post(e);

        public static void Update() => Global.Update();
        public static void ClearQueue() => Global.ClearQueue();

        public static void Release() => Global.Release();

        public static void ResetStatic() => Global.Release();
    }
}
