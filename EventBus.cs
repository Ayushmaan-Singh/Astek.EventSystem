using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Astek.EventSystems.Core
{
    #region Support types

    /// <summary>Global counter that hands out one dense id per event type (shared by every bus in the process).</summary>
    internal static class TypeIdCounter
    {
        internal static int Next;
    }

    /// <summary>Dense per-type index (0,1,2...) so a bus can find a channel with one array read.</summary>
    internal static class TypeId<T>
    {
        internal static readonly int Id = Interlocked.Increment(ref TypeIdCounter.Next) - 1;
    }

    /// <summary>Per-type facts computed once</summary>
    internal static class TypeTraits<T>
    {
        /// <summary>Struct events cannot be null or derived, so the null / runtime-type checks are skipped for them.</summary>
        internal static readonly bool IsValueType = typeof(T).IsValueType;

        /// <summary>True for GameEvent subclasses: enables Consume() handling. Compiles down to a constant branch per T.</summary>
        internal static readonly bool IsGameEvent = typeof(GameEvent).IsAssignableFrom(typeof(T));
    }

    /// <summary>
    /// Type-erased view of a Channel&lt;T&gt;. Lets the bus and Subscription talk to a channel without knowing T
    /// (needed for the global queue order, Release(), and Subscription.Dispose()).
    /// </summary>
    internal interface IChannel
    {
        /// <summary>Pops the oldest event from this channel's own typed queue and dispatches it.</summary>
        void DispatchNext();

        /// <summary>Dispatches a boxed event whose runtime type is exactly T (used by the non-generic / cross-thread paths).</summary>
        bool TriggerBoxed(IEvent e);

        /// <summary>Removes a listener given as a plain Delegate (it is cast back to EventDelegate).</summary>
        bool RemoveBoxed(Delegate handler);

        /// <summary>Drops queued events, keeps listeners.</summary>
        void ClearQueue();

        /// <summary>Drops queued events and all listeners.</summary>
        void Clear();
    }

    /// <summary>
    /// Handle returned by AddListener. Optional to keep. Dispose() unsubscribes (idempotent, safe after Release()).
    /// </summary>
    public readonly struct Subscription : IDisposable
    {
        private readonly IChannel _channel;
        private readonly Delegate _handler;

        internal Subscription(IChannel channel, Delegate handler)
        {
            _channel = channel;
            _handler = handler;
        }

        /// <summary>False for a default(Subscription).</summary>
        public bool IsValid => _channel != null;

        /// <summary>Unsubscribes the listener. Idempotent. a no-op for default(Subscription) or after Release().</summary>
        public void Dispose()
        {
            if (_channel != null) _channel.RemoveBoxed(_handler);
        }

        /// <summary>Fluent helper: <c>bus.AddListener&lt;Hit&gt;(OnHit).AddTo(_bag);</c></summary>
        public Subscription AddTo(SubscriptionBag bag)
        {
            bag.Add(this);
            return this;
        }
    }

    /// <summary>
    /// Collects subscriptions so one Dispose() unsubscribes them all. Reusable after Dispose().
    /// Typical use: create in OnEnable / constructor, Dispose in OnDisable / destructor.
    /// </summary>
    public sealed class SubscriptionBag : IDisposable
    {
        private readonly List<Subscription> _subscriptions = new List<Subscription>();

        /// <summary>Number of subscriptions currently held.</summary>
        public int Count => _subscriptions.Count;

        /// <summary>Adds a subscription (invalid/default handles are ignored).</summary>
        public void Add(Subscription subscription)
        {
            if (subscription.IsValid) _subscriptions.Add(subscription);
        }

        /// <summary>Unsubscribes everything in the bag (newest first) and empties it. The bag can be reused afterwards.</summary>
        public void Dispose()
        {
            for (int i = _subscriptions.Count - 1; i >= 0; i--) _subscriptions[i].Dispose();
            _subscriptions.Clear();
        }
    }

    #endregion

    /// <summary>
    /// A self-contained event bus. Use <see cref="EventManager"/> for the shared global one,
    /// or create your own (per scene, per match, per unit test...). No dependencies besides the BCL.
    ///
    /// Threading: every member is main-thread/single-thread only EXCEPT <see cref="Post"/>.
    ///
    /// Semantics (the contract):
    ///  - Listeners are matched by the event's exact runtime type (no base-class listening).
    ///  - Higher priority runs first; equal priority runs in registration order.
    ///  - Registering the same delegate twice for the same event type is ignored (first registration wins).
    ///  - A listener removed during a dispatch will NOT be invoked afterwards, even in that same dispatch.
    ///  - A listener added during a dispatch starts receiving events after the outermost dispatch
    ///    of that event type finishes (never mid-dispatch).
    ///  - AddListenerOnce listeners are removed BEFORE invocation, so they fire exactly once even with re-entrancy.
    ///  - An exception in one listener is reported to <see cref="ExceptionHandler"/> and does not stop the others.
    ///  - Triggering with no listeners is not an error; it returns false.
    ///  - Queued events are dispatched by <see cref="Update"/> (call it once per frame/tick) in FIFO order across all types.
    ///    Events queued while Update() is running are processed on the next Update().
    ///
    /// Internal layout (for maintainers):
    ///  - One Channel&lt;T&gt; per event type, found via a dense per-type id (<c>TypeId&lt;T&gt;.Id</c>) indexing an array.
    ///  - A Channel owns its listener array and its typed queue of pending events.
    ///  - The bus owns one FIFO of "which channel has the next event", which preserves ordering across event types.
    ///  - Rarely-typed paths (non-generic calls, Post) carry the event boxed and resolve the channel by Type at dispatch time.
    /// </summary>
    public sealed class EventBus
    {
        #region Configuration

        /// <summary>Max queued events dispatched per Update(). Default: all queued at the start of the call.</summary>
        public int MaxEventsPerUpdate = int.MaxValue;

        /// <summary>Soft time budget per Update() in milliseconds (checked after each event). 0 = unlimited.</summary>
        public float MaxMillisecondsPerUpdate = 0f;

        /// <summary>Max nested synchronous dispatches (guards against A-triggers-B-triggers-A stack overflows).</summary>
        public int MaxDispatchDepth = 64;

        /// <summary>Receives exceptions thrown by listeners. Default writes to Console.Error.</summary>
        public Action<Exception> ExceptionHandler = DefaultExceptionHandler;

        /// <summary>
        /// Optional liveness check on a listener's target object (delegate.Target). Return false to auto-unsubscribe it.
        /// Used by the Unity adapter to drop listeners on destroyed UnityEngine.Objects. Leave null for zero overhead.
        /// </summary>
        public Func<object, bool> TargetAliveCheck;

        #endregion

        /// <summary>One unit of pending work in the bus-wide FIFO.</summary>
        private struct QueueItem
        {
            public readonly IChannel Channel; // typed fast path: the event sits in the channel's own queue
            public readonly IEvent Boxed; // slow path (non-generic / cross-thread): resolved by type at dispatch time

            public QueueItem(IChannel channel, IEvent boxed)
            {
                Channel = channel;
                Boxed   = boxed;
            }
        }

        /// <summary>Channels indexed by TypeId&lt;T&gt;.Id (null = no listener or event of that type ever touched this bus).</summary>
        private IChannel[] _channels = new IChannel[16];

        /// <summary>Runtime Type -> channel, for the non-generic and cross-thread (boxed) paths only.</summary>
        private readonly Dictionary<Type, IChannel> _byType = new Dictionary<Type, IChannel>();

        /// <summary>Global FIFO of pending work; keeps ordering across different event types.</summary>
        private readonly Queue<QueueItem> _order = new Queue<QueueItem>(64);

        /// <summary>Thread-safe mailbox filled by Post(), drained into _order at the start of Update().</summary>
        private readonly ConcurrentQueue<IEvent> _inbox = new ConcurrentQueue<IEvent>();

        /// <summary>Current nesting of synchronous dispatches across ALL channels (cycle guard).</summary>
        private int _depth;

        /// <summary>True while Update() runs; makes a nested Update() a no-op.</summary>
        private bool _updating;

        /// <summary>Number of events waiting for the next Update().</summary>
        public int QueuedCount => _order.Count;

        #region Listeners

        /// <summary>Subscribes <paramref name="handler"/> to events of type T. Always write the type: AddListener&lt;MyEvent&gt;(OnMyEvent).</summary>
        /// <param name="handler">The method to call. Duplicates (same target + method) are ignored.</param>
        /// <param name="priority">Higher runs first. Default 0.</param>
        /// <returns>A handle you may keep to unsubscribe later.</returns>
        public Subscription AddListener<T>(EventManager.EventDelegate<T> handler, int priority = 0) where T : IEvent
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Channel<T> channel = GetOrCreate<T>();
            channel.Add(handler, priority, false);
            return new Subscription(channel, handler);
        }

        /// <summary>Like AddListener, but automatically unsubscribes right before its first invocation.</summary>
        public Subscription AddListenerOnce<T>(EventManager.EventDelegate<T> handler, int priority = 0) where T : IEvent
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Channel<T> channel = GetOrCreate<T>();
            channel.Add(handler, priority, true);
            return new Subscription(channel, handler);
        }

        /// <summary>Unsubscribes. Returns false if it was not registered. Safe to call at any time, including from inside a listener.</summary>
        public bool RemoveListener<T>(EventManager.EventDelegate<T> handler) where T : IEvent
        {
            if (handler == null) return false;
            Channel<T> channel = Find<T>();
            return channel != null && channel.Remove(handler);
        }

        /// <summary>True if this exact handler (same target + method) is registered for T.</summary>
        public bool HasListeners<T>(EventManager.EventDelegate<T> handler) where T : IEvent
        {
            if (handler == null) return false;
            Channel<T> channel = Find<T>();
            return channel != null && channel.Has(handler);
        }

        /// <summary>True if at least one listener is registered for T. Use it to skip building expensive events.</summary>
        public bool HasListeners<T>() where T : IEvent
        {
            var channel = Find<T>();
            return channel != null && channel.Live > 0;
        }

        #endregion

        #region Triggering and queueing

        /// <summary>Dispatches immediately. Returns true if at least one listener was invoked.</summary>
        public bool TriggerEvent<T>(T e) where T : IEvent
        {
            if (!TypeTraits<T>.IsValueType)
            {
                if (e == null) throw new ArgumentNullException(nameof(e));
                // Variable typed as a base class but holding a derived event: route by runtime type.
                if (e.GetType() != typeof(T)) return TriggerBoxed(e);
            }
            Channel<T> channel = Find<T>();
            return channel != null && channel.Trigger(e);
        }

        /// <summary>Non-generic overload (routes by runtime type). Boxes struct events; prefer the generic overload.</summary>
        public bool TriggerEvent(IEvent e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            return TriggerBoxed(e);
        }

        /// <summary>Queues the event for the next Update(). Listeners are resolved when it is dispatched, not now.</summary>
        public bool QueueEvent<T>(T e) where T : IEvent
        {
            if (!TypeTraits<T>.IsValueType)
            {
                if (e == null) throw new ArgumentNullException(nameof(e));
                if (e.GetType() != typeof(T))
                {
                    _order.Enqueue(new QueueItem(null, e));
                    return true;
                }
            }
            GetOrCreate<T>().Enqueue(e);
            return true;
        }

        /// <summary>Non-generic overload (routes by runtime type at dispatch time). Boxes struct events.</summary>
        public bool QueueEvent(IEvent e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            _order.Enqueue(new QueueItem(null, e));
            return true;
        }

        /// <summary>
        /// THREAD-SAFE. Hands an event to the bus from any thread (network, async, jobs);
        /// it is moved into the queue and dispatched on the thread that calls Update().
        /// </summary>
        public void Post(IEvent e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            _inbox.Enqueue(e);
        }

        /// <summary>
        /// Pumps the queue. Call once per frame/tick from your main loop (the Unity adapter does this for you).
        /// Only events queued before the call are processed; events queued by listeners wait for the next call.
        /// </summary>
        public void Update()
        {
            if (_updating) return; // ignore accidental re-entrant Update()
            _updating = true;
            try
            {
                IEvent posted;
                while (_inbox.TryDequeue(out posted)) _order.Enqueue(new QueueItem(null, posted));

                int budget = Math.Min(_order.Count, MaxEventsPerUpdate);
                if (budget <= 0) return;

                bool timed = MaxMillisecondsPerUpdate > 0f;
                long deadline = timed
                    ? Stopwatch.GetTimestamp() + (long)(MaxMillisecondsPerUpdate * Stopwatch.Frequency / 1000.0)
                    : 0L;

                while (budget-- > 0 && _order.Count > 0)
                {
                    QueueItem item = _order.Dequeue();
                    if (item.Channel != null) item.Channel.DispatchNext();
                    else
                    {
                        if (_byType.TryGetValue(item.Boxed.GetType(), out IChannel channel)) channel.TriggerBoxed(item.Boxed);
                    }
                    if (timed && Stopwatch.GetTimestamp() >= deadline) break;
                }
            }
            finally
            {
                _updating = false;
            }
        }

        #endregion

        #region Cleanup

        /// <summary>Drops all pending events (queued and posted) but keeps listeners.</summary>
        public void ClearQueue()
        {
            _order.Clear();
            while (_inbox.TryDequeue(out IEvent discard)) { }
            for (int i = 0; i < _channels.Length; i++)
            {
                if (_channels[i] != null) _channels[i].ClearQueue();
            }
        }

        /// <summary>Removes ALL listeners and pending events. Safe to call from inside a listener.</summary>
        public void Release()
        {
            _order.Clear();
            while (_inbox.TryDequeue(out IEvent discard)) { }
            for (int i = 0; i < _channels.Length; i++)
            {
                if (_channels[i] != null) _channels[i].Clear();
            }
        }

        #endregion

        #region Internals

        /// <summary>Fallback used when no handler is assigned.</summary>
        private static void DefaultExceptionHandler(Exception ex) => Console.Error.WriteLine(ex);

        /// <summary>Forwards an exception to ExceptionHandler; never throws.</summary>
        private void Report(Exception ex)
        {
            try
            {
                (ExceptionHandler ?? DefaultExceptionHandler)(ex);
            }
            catch
            {
                /* a broken handler must never break dispatch */
            }
        }

        /// <summary>Hot-path lookup: one static read, one bounds check, one array read. Returns null if the channel does not exist yet (never allocates).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Channel<T> Find<T>() where T : IEvent
        {
            int id = TypeId<T>.Id;
            IChannel[] channels = _channels;
            return (uint)id < (uint)channels.Length ? (Channel<T>)channels[id] : null;
        }

        /// <summary>Cold-path lookup used when registering/queueing: creates and registers the channel on first use.</summary>
        private Channel<T> GetOrCreate<T>() where T : IEvent
        {
            Channel<T> channel = Find<T>();
            if (channel != null) return channel;

            int id = TypeId<T>.Id;
            if (id >= _channels.Length) Array.Resize(ref _channels, Math.Max(id + 1, _channels.Length * 2));
            channel            = new Channel<T>(this);
            _channels[id]      = channel;
            _byType[typeof(T)] = channel;
            return channel;
        }

        /// <summary>Dispatch by the event's exact runtime type. Returns false if nothing ever registered for that type.</summary>
        private bool TriggerBoxed(IEvent e) => _byType.TryGetValue(e.GetType(), out IChannel channel) && channel.TriggerBoxed(e);

        /// <summary>
        /// All listeners + the typed queue for one event type.
        /// Listeners live in a flat array (no multicast delegates, no wrappers, no allocation on dispatch).
        /// While dispatching, the array is never resized or shifted: removals only null a slot (compacted afterwards)
        /// and additions go to a pending list (merged afterwards). That makes every re-entrancy case safe.
        /// </summary>
        private sealed class Channel<T> : IChannel where T : IEvent
        {
            /// <summary>One registered listener. A default(Entry) (Callback == null) is a dead slot.</summary>
            private struct Entry
            {
                /// <summary>The user's handler, invoked directly (no wrapper).</summary>
                public EventManager.EventDelegate<T> Callback;

                /// <summary>Cached handler.Target, handed to TargetAliveCheck (null for static methods).</summary>
                public object Target;

                /// <summary>Higher runs first.</summary>
                public int Priority;

                /// <summary>Unsubscribe right before the first invocation.</summary>
                public bool Once;
            }

            private readonly EventBus _bus;

            /// <summary>Events of this type waiting for Update(). Stored by value, so struct events never box.</summary>
            private readonly Queue<T> _queued = new Queue<T>();

            /// <summary>Active listeners, sorted by priority (desc) then registration order. Gaps only exist while dispatching.</summary>
            private Entry[] _entries = new Entry[4];

            /// <summary>Listeners added while this channel is dispatching; merged (sorted) when the outermost dispatch ends.</summary>
            private List<Entry> _pending;
            private int _count; // used slots (may include dead ones while dispatching)
            private int _live; // alive entries in _entries
            private int _depth; // dispatch nesting for this channel
            private bool _dirty; // dead slots to compact once _depth returns to 0

            public Channel(EventBus bus)
            {
                _bus = bus;
            }

            /// <summary>Number of registered listeners, including ones waiting in the pending list.</summary>
            public int Live => _live + (_pending != null ? _pending.Count : 0);

            /// <summary>Linear scan (listener counts are small); checks active and pending entries.</summary>
            public bool Has(EventManager.EventDelegate<T> handler)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_entries[i].Callback == handler) return true;
                }
                if (_pending != null)
                {
                    for (int i = 0; i < _pending.Count; i++)
                    {
                        if (_pending[i].Callback == handler) return true;
                    }
                }
                return false;
            }

            /// <summary>Registers a listener. Duplicate delegates are ignored. During a dispatch it is parked in _pending.</summary>
            public void Add(EventManager.EventDelegate<T> handler, int priority, bool once)
            {
                if (Has(handler)) return;

                Entry entry = new Entry { Callback = handler, Target = handler.Target, Priority = priority, Once = once };
                if (_depth > 0)
                {
                    if (_pending == null) _pending = new List<Entry>(4);
                    _pending.Add(entry);
                }
                else
                {
                    Insert(entry);
                }
            }

            // Only called while _depth == 0 (array has no gaps and may be resized/shifted freely).
            private void Insert(Entry entry)
            {
                if (_count == _entries.Length) Array.Resize(ref _entries, _count * 2);

                int pos = _count;
                while (pos > 0 && _entries[pos - 1].Priority < entry.Priority) pos--;
                if (pos < _count) Array.Copy(_entries, pos, _entries, pos + 1, _count - pos);

                _entries[pos] = entry;
                _count++;
                _live++;
            }

            /// <summary>
            /// Unregisters a listener. Outside a dispatch the array is shifted immediately; during one the slot is only
            /// nulled (Kill) so running loops keep valid indices, and compaction happens afterwards.
            /// </summary>
            public bool Remove(EventManager.EventDelegate<T> handler)
            {
                if (_pending != null)
                {
                    for (int i = _pending.Count - 1; i >= 0; i--)
                    {
                        if (_pending[i].Callback != handler) continue;
                        _pending.RemoveAt(i);
                        return true;
                    }
                }

                for (int i = _count - 1; i >= 0; i--)
                {
                    if (_entries[i].Callback != handler) continue;

                    if (_depth == 0)
                    {
                        _count--;
                        if (i < _count) Array.Copy(_entries, i + 1, _entries, i, _count - i);
                        _entries[_count] = default(Entry);
                        _live--;
                    }
                    else
                    {
                        Kill(i);
                    }
                    return true;
                }
                return false;
            }

            bool IChannel.RemoveBoxed(Delegate handler) => Remove((EventManager.EventDelegate<T>)handler);

            /// <summary>Marks a slot dead without moving anything (safe while a dispatch loop is running).</summary>
            private void Kill(int index)
            {
                _entries[index] = default(Entry);
                _live--;
                _dirty = true;
            }

            /// <summary>Runs when the outermost dispatch ends: closes gaps left by Kill, then merges pending listeners in priority order.</summary>
            private void Flush()
            {
                if (_dirty)
                {
                    int w = 0;
                    for (int r = 0; r < _count; r++)
                    {
                        if (_entries[r].Callback == null) continue;
                        if (w != r) _entries[w] = _entries[r];
                        w++;
                    }
                    Array.Clear(_entries, w, _count - w);
                    _count = w;
                    _dirty = false;
                }

                if (_pending != null && _pending.Count > 0)
                {
                    for (int i = 0; i < _pending.Count; i++) Insert(_pending[i]);
                    _pending.Clear();
                }
            }

            /// <summary>Removes every listener and queued event. Safe mid-dispatch: running loops just see empty slots.</summary>
            public void Clear()
            {
                _queued.Clear();
                if (_pending != null) _pending.Clear();
                Array.Clear(_entries, 0, _count);
                _live = 0;
                if (_depth == 0)
                {
                    _count = 0;
                    _dirty = false;
                }
                else
                {
                    _dirty = true; // an active dispatch loop still walks the (now empty) slots
                }
            }

            public void ClearQueue() => _queued.Clear();

            /// <summary>Stores the event in this channel's typed queue and records its position in the bus-wide FIFO.</summary>
            public void Enqueue(T e)
            {
                _queued.Enqueue(e);
                _bus._order.Enqueue(new QueueItem(this, null));
            }

            /// <summary>Called by EventBus.Update() when this channel owns the next event in global order.</summary>
            public void DispatchNext()
            {
                if (_queued.Count == 0) return;
                Trigger(_queued.Dequeue());
            }

            bool IChannel.TriggerBoxed(IEvent e) => Trigger((T)e);

            /// <summary>
            /// The hot path. Walks a snapshot [0, n) of the listener array, invoking each live entry directly.
            /// Order of operations per listener: liveness check -> unsubscribe-if-once -> invoke (exceptions isolated) -> consume check.
            /// Returns true if at least one listener was invoked.
            /// </summary>
            public bool Trigger(T e)
            {
                if (_live == 0) return false;

                EventBus bus = _bus;
                if (bus._depth >= bus.MaxDispatchDepth)
                {
                    bus.Report(new InvalidOperationException(
                        "EventBus: max dispatch depth (" + bus.MaxDispatchDepth + ") exceeded while dispatching " +
                        typeof(T).Name + ". Is there an event cycle?"));
                    return false;
                }

                bus._depth++;
                _depth++;
                bool called = false;
                try
                {
                    // Cast once, outside the loop: null for struct events / plain IEvent classes, so they pay nothing per listener.
                    GameEvent consumable = TypeTraits<T>.IsGameEvent ? (GameEvent)(object)e : null;
                    if (consumable != null) consumable.ResetConsumed();

                    Entry[] entries = _entries; // stable for the whole dispatch (never resized while _depth > 0)
                    int n = _count; // listeners added mid-dispatch go to _pending, never into [0, n)
                    Func<object, bool> alive = bus.TargetAliveCheck;

                    for (int i = 0; i < n; i++)
                    {
                        EventManager.EventDelegate<T> callback = entries[i].Callback;
                        if (callback == null) continue; // removed earlier in this dispatch

                        try
                        {
                            if (alive != null)
                            {
                                object target = entries[i].Target;
                                if (target != null && !alive(target))
                                {
                                    Kill(i);
                                    continue;
                                }
                            }

                            if (entries[i].Once) Kill(i); // unsubscribe BEFORE invoking: exactly-once even if re-triggered
                            called = true;
                            callback(e);
                        }
                        catch (Exception ex)
                        {
                            bus.Report(ex);
                        }

                        if (consumable != null && consumable.Consumed) break;
                    }
                }
                finally
                {
                    bus._depth--;
                    if (--_depth == 0 && (_dirty || (_pending != null && _pending.Count > 0))) Flush();
                }
                return called;
            }
        }

        #endregion
    }
}