using Astek.EventSystems.Core;

namespace Astek.EventSystems
{
    /// <summary>
    /// Global, static entry point to the event system (a thin facade over <see cref="Global"/>).
    ///
    /// QUICK START
    /// -----------
    /// 1) Define an event (struct = no garbage, class = can Consume()):
    /// <code>
    /// public struct EnemyDied : IEvent { public int EnemyId; public int Score; }
    /// </code>
    ///
    /// 2) Listen (always write the event type in &lt;&gt;), and keep the subscription to unsubscribe later:
    /// <code>
    /// private readonly SubscriptionBag _subs = new SubscriptionBag();
    ///
    /// void Enable()  { EventManager.AddListener&lt;EnemyDied&gt;(OnEnemyDied).AddTo(_subs); }
    /// void Disable() { _subs.Dispose(); }   // or EventManager.RemoveListener&lt;EnemyDied&gt;(OnEnemyDied)
    ///
    /// void OnEnemyDied(EnemyDied e) { score += e.Score; }
    /// </code>
    ///
    /// 3) Send:
    /// <code>
    /// EventManager.TriggerEvent(new EnemyDied { EnemyId = 7, Score = 100 }); // runs listeners right now
    /// EventManager.QueueEvent(new EnemyDied { EnemyId = 8, Score = 50 });    // runs on the next Update()
    /// </code>
    ///
    /// 4) Outside Unity, call <c>EventManager.Update()</c> once per frame/tick in your main loop.
    ///    (The Unity adapter file does this automatically.)
    ///
    /// OTHER TOOLS
    ///  - AddListenerOnce&lt;T&gt;(handler)          : fires once, then removes itself.
    ///  - AddListener&lt;T&gt;(handler, priority)    : higher priority first; GameEvent.Consume() stops the chain.
    ///  - EventManager.Post(evt)                : thread-safe, delivered on the next Update().
    ///  - new EventBus()                        : an isolated bus (per scene / match / unit test).
    ///  - EventManager.Global.MaxEventsPerUpdate / MaxMillisecondsPerUpdate : throttle queue processing.
    ///  - EventManager.Global.ExceptionHandler  : where listener exceptions go.
    /// </summary>
    public static class EventManager
    {
        /// <summary>
        /// The handler signature: <c>void Handler(MyEvent e)</c>.
        /// Contravariant, so a handler taking a base event type can be registered for a derived one.
        /// </summary>
        public delegate void EventDelegate<in T>(T e) where T : IEvent;

        /// <summary>
        /// The shared bus behind this class. Configure it through its public fields
        /// (<see cref="EventBus.MaxEventsPerUpdate"/>, <see cref="EventBus.ExceptionHandler"/>, ...).
        /// </summary>
        public static EventBus Global { get; } = new EventBus();

        /// <summary>Subscribes a handler. Higher <paramref name="priority"/> runs first. Duplicates are ignored. See <see cref="EventBus.AddListener{T}"/>.</summary>
        /// <returns>A handle; Dispose() it (or add it to a <see cref="SubscriptionBag"/>) to unsubscribe. Ignoring it is fine.</returns>
        public static Subscription AddListener<T>(EventDelegate<T> handler, int priority = 0) where T : IEvent
            => Global.AddListener(handler, priority);

        /// <summary>Subscribes a handler that removes itself right before its first call.</summary>
        public static Subscription AddListenerOnce<T>(EventDelegate<T> handler, int priority = 0) where T : IEvent
            => Global.AddListenerOnce(handler, priority);

        /// <summary>Unsubscribes a handler (matched by target + method). Returns false if it was not registered.</summary>
        public static bool RemoveListener<T>(EventDelegate<T> handler) where T : IEvent
            => Global.RemoveListener(handler);

        /// <summary>True if this exact handler is registered for T.</summary>
        public static bool HasListeners<T>(EventDelegate<T> handler) where T : IEvent
            => Global.HasListeners(handler);

        /// <summary>True if anyone listens to T. Use it to skip building expensive events.</summary>
        public static bool HasListeners<T>() where T : IEvent
            => Global.HasListeners<T>();

        /// <summary>Runs all listeners immediately. Returns true if at least one ran; never throws for "no listeners".</summary>
        public static bool TriggerEvent<T>(T e) where T : IEvent
            => Global.TriggerEvent(e);

        /// <summary>Non-generic overload; routes by runtime type and boxes structs. Prefer the generic overload.</summary>
        public static bool TriggerEvent(IEvent e)
            => Global.TriggerEvent(e);

        /// <summary>Queues the event; listeners run during the next <see cref="Update"/>, in FIFO order.</summary>
        public static bool QueueEvent<T>(T e) where T : IEvent
            => Global.QueueEvent(e);

        /// <summary>Non-generic overload; routes by runtime type at dispatch time and boxes structs.</summary>
        public static bool QueueEvent(IEvent e)
            => Global.QueueEvent(e);

        /// <summary>THREAD-SAFE. Hands an event over from any thread; it is dispatched on the next <see cref="Update"/>.</summary>
        public static void Post(IEvent e) => Global.Post(e);

        /// <summary>Dispatches queued events. Call once per frame/tick (the Unity adapter does it automatically).</summary>
        public static void Update() => Global.Update();

        /// <summary>Drops pending events (queued and posted) but keeps listeners.</summary>
        public static void ClearQueue() => Global.ClearQueue();

        /// <summary>Removes all listeners and pending events.</summary>
        public static void Release() => Global.Release();

        /// <summary>Same as <see cref="Release"/>; kept for compatibility. Unity calls it on play start for "Domain Reload disabled".</summary>
        public static void ResetStatic() => Global.Release();
    }
}