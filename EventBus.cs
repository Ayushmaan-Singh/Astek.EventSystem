using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Astek.EventSystems.Core
{
    #region Support types

    internal static class TypeIdCounter
    {
        internal static int Next;
    }
    internal static class TypeId<T>
    {
        internal static readonly int Id = Interlocked.Increment(ref TypeIdCounter.Next) - 1;
    }
    internal static class TypeTraits<T>
    {
        internal static readonly bool IsValueType = typeof(T).IsValueType;
        internal static readonly bool IsGameEvent = typeof(GameEvent).IsAssignableFrom(typeof(T));
    }

    internal interface IChannel
    {
        void DispatchNext();
        bool TriggerBoxed(IEvent e);
        bool RemoveBoxed(Delegate handler);
        void ClearQueue();
        void Clear();
    }

    public readonly struct Subscription : IDisposable
    {
        private readonly IChannel _channel;
        private readonly Delegate _handler;

        internal Subscription(IChannel channel, Delegate handler)
        {
            _channel = channel;
            _handler = handler;
        }

        public bool IsValid => _channel != null;

        public void Dispose()
        {
            if (_channel != null) _channel.RemoveBoxed(_handler);
        }

        public Subscription AddTo(SubscriptionBag bag)
        {
            bag.Add(this);
            return this;
        }
    }

    public sealed class SubscriptionBag : IDisposable
    {
        private readonly List<Subscription> _subscriptions = new List<Subscription>();

        public int Count => _subscriptions.Count;

        public void Add(Subscription subscription)
        {
            if (subscription.IsValid) _subscriptions.Add(subscription);
        }

        public void Dispose()
        {
            for (int i = _subscriptions.Count - 1; i >= 0; i--) _subscriptions[i].Dispose();
            _subscriptions.Clear();
        }
    }

    #endregion

    public sealed class EventBus
    {
        #region Configuration

        public int MaxEventsPerUpdate = int.MaxValue;
        public float MaxMillisecondsPerUpdate = 0f;
        public int MaxDispatchDepth = 64;

        public Action<Exception> ExceptionHandler = DefaultExceptionHandler;
        public Func<object, bool> TargetAliveCheck;

        #endregion

        private struct QueueItem
        {
            public readonly IChannel Channel;
            public readonly IEvent Boxed;

            public QueueItem(IChannel channel, IEvent boxed)
            {
                Channel = channel;
                Boxed   = boxed;
            }
        }

        private IChannel[] _channels = new IChannel[16];
        private readonly Dictionary<Type, IChannel> _byType = new Dictionary<Type, IChannel>();
        private readonly Queue<QueueItem> _order = new Queue<QueueItem>(64);
        private readonly ConcurrentQueue<IEvent> _inbox = new ConcurrentQueue<IEvent>();

        private int _depth;
        private bool _updating;

        public int QueuedCount => _order.Count;

        #region Listeners

        public Subscription AddListener<T>(EventManager.EventDelegate<T> handler, int priority = 0) where T : IEvent
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Channel<T> channel = GetOrCreate<T>();
            return channel.Add(handler, priority, false) ? new Subscription(channel, handler) : default(Subscription);
        }

        public Subscription AddListenerOnce<T>(EventManager.EventDelegate<T> handler, int priority = 0) where T : IEvent
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Channel<T> channel = GetOrCreate<T>();
            channel.Add(handler, priority, true);
            return new Subscription(channel, handler);
        }

        public bool RemoveListener<T>(EventManager.EventDelegate<T> handler) where T : IEvent
        {
            if (handler == null) return false;
            Channel<T> channel = Find<T>();
            return channel != null && channel.Remove(handler);
        }

        public bool HasListeners<T>(EventManager.EventDelegate<T> handler) where T : IEvent
        {
            if (handler == null) return false;
            Channel<T> channel = Find<T>();
            return channel != null && channel.Has(handler);
        }

        public bool HasListeners<T>() where T : IEvent
        {
            var channel = Find<T>();
            return channel != null && channel.Live > 0;
        }

        #endregion

        #region Triggering and queueing

        public bool TriggerEvent<T>(T e) where T : IEvent
        {
            if (!TypeTraits<T>.IsValueType)
            {
                if (e == null) throw new ArgumentNullException(nameof(e));
                if (e.GetType() != typeof(T)) return TriggerBoxed(e);
            }
            Channel<T> channel = Find<T>();
            return channel != null && channel.Trigger(e);
        }

        public bool TriggerEvent(IEvent e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            return TriggerBoxed(e);
        }

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

        public bool QueueEvent(IEvent e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            _order.Enqueue(new QueueItem(null, e));
            return true;
        }

        public void Post(IEvent e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            _inbox.Enqueue(e);
        }

        public void Update()
        {
            if (_updating) return;
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

        public void ClearQueue()
        {
            _order.Clear();
            while (_inbox.TryDequeue(out IEvent discard)) { }
            for (int i = 0; i < _channels.Length; i++)
            {
                if (_channels[i] != null) _channels[i].ClearQueue();
            }
        }

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

        private static void DefaultExceptionHandler(Exception ex) => Console.Error.WriteLine(ex);

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Channel<T> Find<T>() where T : IEvent
        {
            int id = TypeId<T>.Id;
            IChannel[] channels = _channels;
            return (uint)id < (uint)channels.Length ? (Channel<T>)channels[id] : null;
        }

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

        private bool TriggerBoxed(IEvent e) => _byType.TryGetValue(e.GetType(), out IChannel channel) && channel.TriggerBoxed(e);

        private sealed class Channel<T> : IChannel where T : IEvent
        {
            private struct Entry
            {
                public EventManager.EventDelegate<T> Callback;
                public object Target;
                public int Priority;
                public bool Once;
            }

            private readonly EventBus _bus;
            private readonly Queue<T> _queued = new Queue<T>();
            private Entry[] _entries = new Entry[4];

            private List<Entry> _pending;
            private int _count;
            private int _live;
            private int _depth;
            private bool _dirty;

            public Channel(EventBus bus)
            {
                _bus = bus;
            }

            public int Live => _live + (_pending != null ? _pending.Count : 0);

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

            public bool Add(EventManager.EventDelegate<T> handler, int priority, bool once)
            {
                if (Has(handler)) return false;

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
                return true;
            }

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

            private void Kill(int index)
            {
                _entries[index] = default(Entry);
                _live--;
                _dirty = true;
            }

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
                    _dirty = true;
                }
            }

            public void ClearQueue() => _queued.Clear();

            public void Enqueue(T e)
            {
                _queued.Enqueue(e);
                _bus._order.Enqueue(new QueueItem(this, null));
            }

            public void DispatchNext()
            {
                if (_queued.Count == 0) return;
                Trigger(_queued.Dequeue());
            }

            bool IChannel.TriggerBoxed(IEvent e) => Trigger((T)e);

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
                    GameEvent consumable = TypeTraits<T>.IsGameEvent ? (GameEvent)(object)e : null;
                    if (consumable != null) consumable.ResetConsumed();

                    Entry[] entries = _entries;
                    int n = _count;
                    Func<object, bool> alive = bus.TargetAliveCheck;

                    for (int i = 0; i < n; i++)
                    {
                        EventManager.EventDelegate<T> callback = entries[i].Callback;
                        if (callback == null) continue;

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

                            if (entries[i].Once) Kill(i);
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
