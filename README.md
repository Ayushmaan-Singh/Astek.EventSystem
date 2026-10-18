# 🚫 CUSTOM NON-AI LICENSE

![AI Training Prohibited](https://shields.io)

This repository is free to use, modify, and integrate into your own products. However, **standalone reselling and the use of this data for training Artificial Intelligence (AI) or Large Language Models (LLMs) are strictly prohibited.** See `LICENSE` for details.
[Licence.md](https://github.com/user-attachments/files/33605994/Licence.md)

# Astek EventSystem: User Guide

[Back to the README](../README.md) | [Installation Guide](INSTALL.md)

## Contents

1. [What it is, in one minute](#1-what-it-is-in-one-minute)
2. [The seven words you need](#3-the-seven-words-you-need)
3. [Quick start](#4-quick-start)
4. [Defining events](#5-defining-events)
5. [Listening](#6-listening)
6. [Sending: trigger, queue or post](#7-sending-trigger-queue-or-post)
7. [The update loop](#8-the-update-loop)
8. [Priority, consuming and cancelling](#9-priority-consuming-and-cancelling)
9. [Multiple buses](#10-multiple-buses)
10. [Configuration](#11-configuration)
11. [Errors and safety](#12-errors-and-safety)
12. [The rules of the road](#13-the-rules-of-the-road)
13. [Recipes for every situation](#14-recipes-for-every-situation)
14. [Performance guidelines](#15-performance-guidelines)
15. [What it does not do](#16-what-it-does-not-do)
16. [Troubleshooting](#17-troubleshooting)
17. [API reference](#18-api-reference)
18. [FAQ](#19-faq)
19. [Support and feedback](#20-support-and-feedback)

## 1. What it is, in one minute

Astek EventSystem lets the parts of your game talk to each other without knowing about each other. One part announces that something happened (an enemy died, a level loaded). Any number of other parts subscribe and react. The sender never references the listeners, so you can add, remove and test systems independently.

It is built to be fast and safe. Sending an event creates no garbage in steady state, and the awkward situations (changing listeners while an event is being delivered, exceptions inside handlers, event loops) are handled for you.

The public types you will use:

| Type | What it is for |
|---|---|
| `EventManager` | The shared, static entry point. Most code only needs this |
| `EventBus` | Create your own separate buses (per scene, match, server room, unit test) |
| `IEvent` | Marker that says 'this type can be sent as an event' |
| `GameEvent` | Optional base class for class events; adds `Consume()` |
| `Subscription` | The receipt returned by `AddListener`; disposing it unsubscribes |
| `SubscriptionBag` | Collects subscriptions so one call unsubscribes them all |
| `EventManager.EventDelegate<T>` | The handler signature: `void Handler(T e)` |

Everything is in the namespace `Astek.DesignPattern.EventSystem`.

## 3. The seven words you need

| Word | Meaning |
|---|---|
| Event | A small object (a struct or a class) that describes something that happened, such as `EnemyDied` |
| Listener or handler | A method that wants to hear about one event type |
| Bus | The middleman that remembers who listens to what and delivers events. `EventManager` gives you one shared bus; `EventBus` lets you make more |
| Trigger | Deliver an event to its listeners right now |
| Queue | Store an event and deliver it later, when `Update()` runs |
| Post | Like queue, but safe to call from any thread |
| Subscription | The receipt you get when you add a listener; disposing it cancels the listener |

## 4. Quick start

```csharp
using Astek.DesignPattern.EventSystem;

// 1. Define an event.
public struct EnemyDied : IEvent { public int EnemyId; public int Score; }

// 2. Listen, and keep the receipts in a bag.
public class ScoreBoard
{
    private readonly SubscriptionBag _subs = new SubscriptionBag();
    private int _score;

    public void Enable()  => EventManager.AddListener<EnemyDied>(OnEnemyDied).AddTo(_subs);
    public void Disable() => _subs.Dispose();   // unsubscribes everything in the bag

    private void OnEnemyDied(EnemyDied e) { _score += e.Score; }
}

// 3. Send.
EventManager.TriggerEvent(new EnemyDied { EnemyId = 7, Score = 100 });  // listeners run now
EventManager.QueueEvent(new EnemyDied { EnemyId = 8, Score = 50 });     // listeners run on the next Update()
```

## 5. Defining events

An event is any struct or class that implements `IEvent`. Class events can also derive from `GameEvent`, which gives them `Consume()` ([section 9](#9-priority-consuming-and-cancelling)).

```csharp
public struct PlayerDied : IEvent { }                                  // no data needed
public struct ScoreChanged : IEvent { public int Delta; public int Total; }
public class  QuestCompleted : GameEvent { public string QuestName; public int Reward; }
```

| | Struct event | Class event |
|---|---|---|
| Garbage | None | One allocation per `new` (reuse instances to avoid it) |
| What listeners receive | A copy | The same object everyone sees |
| Can a listener change it? | No, it only changes its own copy | Yes |
| `Consume()` | Not available | Available when deriving from `GameEvent` |
| Best for | Frequent, small notifications: damage, input, score | Rare or large events, request and response, cancellation |

Rules of thumb:

- **Name events for what happened**, in the past tense: `EnemyDied`, `LevelLoaded`, `ItemPicked`.
- **Matching is by exact type.** A listener for `EnemyDied` receives only `EnemyDied`. A listener for a base class does not receive derived events, so give every event its own type.
- **Keep struct events small** (a few fields). They are copied to each listener.
- **Do not change a class event after you queue it.** A queued class event is held by reference until `Update()` delivers it.
- **Carry plain data** (ids, numbers, enums). Avoid keeping references to objects that may be destroyed before the event is delivered.

## 6. Listening

### 6.1 The basics

```csharp
Subscription sub = EventManager.AddListener<EnemyDied>(OnEnemyDied);

void OnEnemyDied(EnemyDied e) { /* react */ }
```

A handler is any method with the shape `void Name(TheEvent e)`.

> [!IMPORTANT]
> Always write the event type in angle brackets: `AddListener<EnemyDied>(OnEnemyDied)`. C# cannot work out the type from a method name alone, and the compiler will complain if you leave it out.

### 6.2 Method, lambda or static method

```csharp
EventManager.AddListener<EnemyDied>(OnEnemyDied);                  // instance method
EventManager.AddListener<EnemyDied>(e => _score += e.Score);       // lambda
EventManager.AddListener<EnemyDied>(StaticHelpers.OnEnemyDied);    // static method
```

A lambda cannot be removed with `RemoveListener`, because writing the same lambda twice makes two different objects. Keep the returned `Subscription` (or use a bag) and dispose it instead.

### 6.3 Priority

```csharp
EventManager.AddListener<EnemyDied>(PlaySound, priority: 10);   // runs first
EventManager.AddListener<EnemyDied>(UpdateScore);                // priority 0
EventManager.AddListener<EnemyDied>(Cleanup, priority: -5);      // runs last
```

Higher numbers run first. Listeners with the same priority run in the order they were added. The default priority is 0 and negative values are allowed.

### 6.4 Listen once

```csharp
EventManager.AddListenerOnce<LevelLoaded>(e => ShowIntro());
```

The listener removes itself just before its first call. It fires exactly once, even if the same event is triggered again from inside the handler.

### 6.5 Checking and removing

```csharp
bool listening = EventManager.HasListeners<EnemyDied>();            // does anyone listen?
bool mine      = EventManager.HasListeners<EnemyDied>(OnEnemyDied); // is this handler registered?
bool removed   = EventManager.RemoveListener<EnemyDied>(OnEnemyDied); // false if it was not registered
```

Registering the same method on the same object twice is ignored: the first registration wins, including its priority and its once-or-not setting. Two different objects registering the same method are two different listeners.

### 6.6 Subscriptions and bags: the recommended way to unsubscribe

Every `AddListener` call returns a `Subscription`. Disposing it removes that one listener. A `SubscriptionBag` collects many subscriptions so one call removes them all.

```csharp
private readonly SubscriptionBag _subs = new SubscriptionBag();

void Enable()
{
    EventManager.AddListener<EnemyDied>(OnEnemyDied).AddTo(_subs);
    EventManager.AddListener<LevelLoaded>(OnLevelLoaded).AddTo(_subs);
}

void Disable() => _subs.Dispose();   // removes both, newest first, and empties the bag
```

- `Dispose()` is safe to call more than once, and safe after the bus has been released.
- A bag can be reused after `Dispose()`, so repeated `Enable()` and `Disable()` work.
- **Each object owns its own bag.** Do not share one bag between objects with different lifetimes, because disposing it unsubscribes all of them.
- One bag can hold subscriptions to different event types. It tracks lifetime, not type.

Subscribe and unsubscribe in matching pairs:

| Environment | Subscribe in | Unsubscribe in |
|---|---|---|
| Unity MonoBehaviour | `OnEnable` | `OnDisable` |
| Godot node | `_EnterTree` or `_Ready` | `_ExitTree` |
| Plain C# class | Constructor or an `Init` method | `Dispose` (implement `IDisposable`) |

## 7. Sending: trigger, queue or post

| Situation | Use | Why |
|---|---|---|
| Listeners must finish before your next line of code | `TriggerEvent` | Runs listeners immediately and returns |
| You are inside a listener and want to avoid deep nesting | `QueueEvent` | Waits for the next `Update()`; also breaks event cycles |
| You want to spread work across frames | `QueueEvent` plus `MaxEventsPerUpdate` | Throttles delivery ([section 11](#11-configuration)) |
| You are on a background thread | `Post` | The only thread-safe call |
| You need an answer back | `TriggerEvent` with a class event that listeners fill in | It is synchronous ([section 14.7](#147-request-and-response-ask-a-question-get-an-answer)) |

### 7.1 `TriggerEvent`

```csharp
bool anyoneHeard = EventManager.TriggerEvent(new EnemyDied { EnemyId = 7, Score = 100 });
```

Runs every listener now, highest priority first, then returns. The result is `true` if at least one listener ran. Having no listeners is not an error: it simply returns `false`.

### 7.2 `QueueEvent`

```csharp
EventManager.QueueEvent(new EnemyDied { EnemyId = 8, Score = 50 });
```

Stores the event. It is delivered by the next `EventManager.Update()`, in the order events were queued, across all event types. Listeners are looked up at delivery time, so a listener added after you queued the event but before `Update()` still receives it. If nobody listens when it is delivered, the event is dropped silently.

### 7.3 `Post`

```csharp
Task.Run(() => EventManager.Post(new DownloadFinished { Id = 3 }));
```

Safe from any thread. The event is moved into the queue and delivered on the thread that calls `Update()`. It is a little slower than `QueueEvent` (the event is boxed), so use it only for cross-thread hand-offs.

### 7.4 Sending from inside a listener

This is allowed. `TriggerEvent` inside a handler delivers immediately (nested). `QueueEvent` inside a handler waits for the next `Update()`. If events keep triggering each other in a loop, the library stops at a depth limit and reports an error instead of crashing ([section 12](#12-errors-and-safety)).

### 7.5 The non-generic overloads

`TriggerEvent(IEvent)` and `QueueEvent(IEvent)` exist for the rare case where all you hold is an `IEvent`. They find the right listeners by the object's real type, but they box struct events, which creates garbage. If you pass the concrete event type (`new EnemyDied { ... }`), C# automatically picks the faster generic version, so you normally never think about this.

## 8. The update loop

Queued and posted events only run when `Update()` is called. Call `EventManager.Update()` once per frame or tick, from wherever your program has a main loop:

| Environment | Where `Update()` is called |
|---|---|
| Unity | Automatic if `EventManagerUnity.cs` is in your project |
| Godot | An autoload node's `_Process` (see the [Installation Guide](INSTALL.md)) |
| MonoGame | Your `Game.Update` |
| Console or custom loop | Once per iteration of the main loop |
| Server | Once per tick, on the thread that owns the bus |
| WinForms or WPF | From a timer or the render loop |

How `Update()` behaves:

- It delivers events in the order they were queued, across all event types.
- It only delivers events queued **before** the call. Events queued by listeners during `Update()` wait for the next call. This prevents endless loops.
- A nested call to `Update()` from inside a listener is ignored.
- By default it delivers everything queued. You can limit it by count or by time ([section 11](#11-configuration)).

## 9. Priority, consuming and cancelling

**Priority** decides the order listeners run ([section 6.3](#63-priority)).

**Consume** lets a high-priority listener stop the lower-priority ones. It is available on events that derive from `GameEvent`:

```csharp
public class PointerClick : GameEvent { public float X; public float Y; }

// UI first: if it handles the click, nobody else sees it.
EventManager.AddListener<PointerClick>(e => { if (HitsButton(e.X, e.Y)) e.Consume(); }, priority: 100);

// Gameplay: skipped automatically when the click was consumed.
EventManager.AddListener<PointerClick>(e => Shoot(e.X, e.Y));
```

- After `Consume()`, every remaining listener of that dispatch is skipped, whatever its priority.
- `Consumed` is reset at the start of every dispatch, so you can reuse the same event object.
- `Consume()` does nothing useful on struct events, because listeners only see copies. Use a `GameEvent` class when you need it.
- To **cancel an action** (for example a damage request), put a flag on a class event and let high-priority listeners set it. [Section 14.8](#148-a-cancellable-action) has an example.

## 10. Multiple buses

`EventManager` is one shared bus, available as `EventManager.Global`. When you want events kept separate, create your own with `new EventBus()`. It has the same methods as `EventManager`.

```csharp
var room = new EventBus();
room.AddListener<PlayerJoined>(OnJoined);
room.TriggerEvent(new PlayerJoined { PlayerId = 3 });
room.Update();   // you pump the buses you create
```

Use a separate bus for:

- **A scene or level** whose listeners must disappear with it.
- **A match or server room**, so one room never hears another room's events.
- **Unit tests**, so tests never leak listeners into each other.
- **A plugin or module** that should not see the game's global events.

Rules for separate buses:

- Buses are completely independent. A listener on one never hears events sent on another.
- You call `Update()` on each bus you create. Only `EventManager.Global` is pumped by the Unity or Godot helper.
- The same event types work on any bus.
- A bus is single-threaded, like the shared one. Only `Post` may be called from other threads.
- Call `Release()` when you are finished with a bus.

## 11. Configuration

Settings are public fields on the bus. For the shared bus, set them through `EventManager.Global`.

| Setting | Default | What it does |
|---|---|---|
| `MaxEventsPerUpdate` | no limit | The most queued events one `Update()` delivers. Set it to `1` for the classic one-event-per-frame behavior |
| `MaxMillisecondsPerUpdate` | 0 (off) | A soft time budget for one `Update()`. It is checked after each event, so one slow event can overrun it. The rest stay queued |
| `MaxDispatchDepth` | 64 | How deeply triggers may nest before the library reports an error and drops the event |
| `ExceptionHandler` | writes to `Console.Error` | Receives exceptions thrown by listeners (`Action<Exception>`) |
| `TargetAliveCheck` | none | Optional `Func<object, bool>` asked about each listener's target object. Return `false` to remove that listener |
| `QueuedCount` | read-only | How many events are waiting for the next `Update()` |

```csharp
EventManager.Global.MaxEventsPerUpdate = 200;
EventManager.Global.MaxMillisecondsPerUpdate = 1.5f;
EventManager.Global.ExceptionHandler = ex => MyLogger.Error(ex);
EventManager.Global.TargetAliveCheck = target => !IsDestroyed(target);
```

`TargetAliveCheck` is not called for static methods, because they have no target object. It costs one call per listener per delivery, so leave it unset if you always unsubscribe yourself. The Unity helper sets it to detect destroyed Unity objects.

Cleaning up:

| Call | Effect |
|---|---|
| `ClearQueue()` | Drops events waiting in the queue (queued and posted). Listeners stay |
| `Release()` | Removes every listener and every waiting event. Subscriptions you hold become harmless no-ops |

## 12. Errors and safety

- **A listener throws.** The exception goes to `ExceptionHandler` and the remaining listeners still run. If your own `ExceptionHandler` throws, that is swallowed so delivery never breaks.
- **Event cycles.** If A triggers B and B triggers A (nested, without queueing), the library stops at `MaxDispatchDepth`, reports an `InvalidOperationException` that names the event type, and drops that event. The program keeps running. Fix the cycle by using `QueueEvent` in one of the handlers.
- **Null arguments.** `AddListener(null)` and sending a null event throw `ArgumentNullException`. `RemoveListener(null)` simply returns `false`.
- **Destroyed objects.** Set `TargetAliveCheck` (or use the Unity helper) so listeners on destroyed objects are removed instead of called.
- **No listeners.** Never an error. `TriggerEvent` returns `false`, and queued events are dropped silently.

## 13. The rules of the road

These behaviors are guaranteed:

1. Listeners run by priority (high to low), then in the order they were added.
2. Registering the same method on the same object twice is ignored; the first registration wins.
3. A listener removed during a delivery is **not** called afterwards, even within that delivery.
4. A listener added during a delivery starts receiving events after the outermost delivery of that event type finishes.
5. A once-listener runs exactly once, even if its event is triggered again from inside it.
6. An exception in one listener never stops the others.
7. Sending with no listeners is not an error.
8. `Update()` delivers in queue order across all event types, only delivers events queued before the call, and ignores nested calls.
9. Queued events find their listeners at delivery time, not at queue time.
10. `Release()`, `ClearQueue()`, `RemoveListener()` and `Dispose()` are safe to call from inside a listener.
11. Nested triggers deeper than `MaxDispatchDepth` are reported and dropped.
12. Matching is by the event's exact type.
13. Sending (`TriggerEvent`, `QueueEvent`, `Update`) allocates nothing in steady state. Registering a listener may allocate.
14. Everything is single-threaded except `Post`.

## 14. Recipes for every situation

### 14.1 A Unity MonoBehaviour

```csharp
public class HealthBar : MonoBehaviour
{
    private readonly SubscriptionBag _subs = new SubscriptionBag();

    void OnEnable()  => EventManager.AddListener<HealthChanged>(OnHealthChanged).AddTo(_subs);
    void OnDisable() => _subs.Dispose();

    void OnHealthChanged(HealthChanged e) { /* update the bar */ }
}
```

### 14.2 A Godot node

```csharp
public partial class ScoreLabel : Label
{
    private readonly SubscriptionBag _subs = new SubscriptionBag();
    private int _score;

    public override void _EnterTree() => EventManager.AddListener<EnemyDied>(OnEnemyDied).AddTo(_subs);
    public override void _ExitTree()  => _subs.Dispose();

    private void OnEnemyDied(EnemyDied e) { _score += e.Score; Text = _score.ToString(); }
}
```

### 14.3 A plain C# service class

```csharp
public sealed class AchievementService : IDisposable
{
    private readonly SubscriptionBag _subs = new SubscriptionBag();

    public AchievementService()
    {
        EventManager.AddListener<EnemyDied>(OnEnemyDied).AddTo(_subs);
        EventManager.AddListenerOnce<LevelCompleted>(e => Unlock(Achievement.FirstLevel)).AddTo(_subs);
    }

    public void Dispose() => _subs.Dispose();
}
```

### 14.4 Listeners that live for the whole run

If an object exists for the entire program (a manager, a logger), subscribe once at startup. You do not have to unsubscribe, but keeping a bag and disposing it on shutdown is tidy.

### 14.5 A lambda that captures variables

```csharp
Subscription sub = EventManager.AddListener<EnemyDied>(e => total += e.Score);
// later
sub.Dispose();
```

### 14.6 A one-shot reaction

```csharp
EventManager.AddListenerOnce<GameStarted>(e => ShowTutorial());
```

### 14.7 Request and response (ask a question, get an answer)

Events can carry answers back because `TriggerEvent` is synchronous. Use a class event so listeners can write into it.

```csharp
public class QueryHealth : GameEvent { public int EntityId; public int Result; public bool Answered; }

// Whoever owns the entity answers:
EventManager.AddListener<QueryHealth>(e =>
{
    if (e.EntityId == _id) { e.Result = _health; e.Answered = true; e.Consume(); }
});

// Asking:
var q = new QueryHealth { EntityId = 7 };
EventManager.TriggerEvent(q);
if (q.Answered) UseHealth(q.Result);
```

### 14.8 A cancellable action

```csharp
public class DamageRequest : GameEvent { public int TargetId; public int Amount; public bool Cancelled; }

// A shield, high priority, can cancel or reduce the damage:
EventManager.AddListener<DamageRequest>(e => { if (HasShield(e.TargetId)) e.Cancelled = true; }, priority: 100);

// The code that applies damage:
var req = new DamageRequest { TargetId = id, Amount = 25 };
EventManager.TriggerEvent(req);
if (!req.Cancelled) ApplyDamage(req.TargetId, req.Amount);
```

### 14.9 Events for one specific object

The bus delivers to every listener of a type, so put an id in the event and let listeners filter.

```csharp
public struct EnemyHit : IEvent { public int EnemyId; public int Damage; }

void OnEnemyHit(EnemyHit e)
{
    if (e.EnemyId != _myId) return;   // not for me
    TakeDamage(e.Damage);
}
```

### 14.10 Deferred delivery and breaking cycles

Use `QueueEvent` when the reaction can wait until the next `Update()`, or when a handler would otherwise trigger the event that is currently running.

### 14.11 Spreading a burst of events over several frames

```csharp
EventManager.Global.MaxEventsPerUpdate = 100;       // at most 100 per frame
EventManager.Global.MaxMillisecondsPerUpdate = 1f;  // or stop after about 1 ms
```

### 14.12 Sending from a background thread

```csharp
Task.Run(async () =>
{
    var data = await DownloadAsync();
    EventManager.Post(new DownloadFinished { Id = data.Id });   // delivered on the next Update()
});
```

### 14.13 Reusing class events to avoid garbage

Keep one event instance and refill it before each send:

```csharp
private readonly PointerClick _click = new PointerClick();

void OnInput(float x, float y)
{
    _click.X = x; _click.Y = y;
    EventManager.TriggerEvent(_click);
}
```

This is safe with `TriggerEvent`. Do not reuse an instance you have queued, because it is delivered later and would show whatever you wrote into it afterwards. Struct events never need this.

### 14.14 Starting from a clean slate between scenes or levels

- **Preferred:** each object disposes its own bag when it goes away. Nothing else is needed.
- **A separate bus per scene:** create it when the scene starts and call `Release()` when it ends.
- **`EventManager.Release()`:** removes every listener of the shared bus, including permanent services. Use it only when everything will register again, for example when returning to a main menu that rebuilds all systems.

### 14.15 Unit testing

Use a fresh bus per test so nothing leaks between tests:

```csharp
var bus = new EventBus();
int total = 0;
bus.AddListener<EnemyDied>(e => total += e.Score);

bus.TriggerEvent(new EnemyDied { Score = 5 });

Assert.AreEqual(5, total);
```

For queued events, call `bus.Update()` before asserting.

### 14.16 A server with many rooms

```csharp
class Room
{
    public readonly EventBus Events = new EventBus();
    public void Tick() { Simulate(); Events.Update(); }
    public void Close() => Events.Release();
}
```

Each room owns a bus and pumps it on its own thread. Other threads hand events in with `room.Events.Post(...)`.

### 14.17 Debugging

| Question | Tool |
|---|---|
| Is anyone listening to this event? | `HasListeners<T>()` |
| Are events piling up in the queue? | `EventManager.Global.QueuedCount` |
| Are my handlers throwing? | Set `ExceptionHandler` to log to your own output |
| Did my event reach anyone? | The result of `TriggerEvent` (`true` or `false`) |

### 14.18 Replacing what you use today

| You use | Replace or combine with |
|---|---|
| A C# `event Action<int>` on each class | A struct event, `AddListener` and `TriggerEvent` |
| `UnityEvent` wired in the inspector | Keep it for designer-made links. Use this library for code-to-code messages, or have a component listen and invoke its `UnityEvent` |
| Singletons calling each other | Events, so the systems no longer reference each other |
| `SendMessage` or `BroadcastMessage` | Events: type-checked, no reflection, no garbage |

## 15. Performance guidelines

- Prefer **struct events** for frequent notifications. Keep them small.
- Pass the concrete event type to `TriggerEvent` and `QueueEvent`. Avoid variables typed as `IEvent`, which use the slower boxed path.
- Reuse class events instead of creating new ones in hot loops.
- Use `HasListeners<T>()` to skip building an expensive event nobody wants.
- Subscribe once, not every frame. Registering is for setup, sending is for the hot path.
- Hundreds of listeners on one event type are fine. Registering or removing thousands on a single type is slower, because each change scans that type's list.
- Leave `TargetAliveCheck` unset if you always unsubscribe yourself.

## 16. What it does not do

- **No return values from handlers.** Use a class event with result fields ([section 14.7](#147-request-and-response-ask-a-question-get-an-answer)).
- **No base-class listening.** A listener for a base type does not receive derived events.
- **No async handlers.** A handler runs synchronously. Starting async work inside one is fine, but the library does not wait for it.
- **No thread safety** apart from `Post`.
- **No persistence.** Queued events are lost when the program closes or `Release()` is called.
- **No automatic subscription.** Listeners must be registered by your code.

## 17. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `AddListener(OnX)` does not compile | Write the type: `AddListener<MyEvent>(OnX)` |
| `TriggerEvent` works, `QueueEvent` never fires | Nothing calls `EventManager.Update()`. Add it to your loop or use the engine helper |
| A listener never fires | The event type differs (matching is exact), the listener was removed, or its object was destroyed |
| A listener fires twice | Two different objects registered the same method, or it was added on two buses |
| A lambda cannot be removed | Keep its `Subscription` and dispose that |
| A listener added inside a handler misses the current event | By design: it starts with the next delivery (rule 4) |
| `Consume()` has no effect | The event is a struct, or the listener runs at a lower priority than the consumer, or it is a different instance |
| Error mentioning maximum dispatch depth | An event cycle. Use `QueueEvent` in one handler to break it |
| Handler exceptions seem to vanish | Set `ExceptionHandler` to log them. The default writes to the console error stream only |
| Events pile up | `Update()` is not called every frame, or `MaxEventsPerUpdate` is too low for your event rate |

## 18. API reference

### `EventManager` (static)

| Member | Description |
|---|---|
| `EventBus Global { get; }` | The shared bus behind all static methods |
| `Subscription AddListener<T>(EventDelegate<T> handler, int priority = 0)` | Register a handler |
| `Subscription AddListenerOnce<T>(EventDelegate<T> handler, int priority = 0)` | Register a handler that fires once |
| `bool RemoveListener<T>(EventDelegate<T> handler)` | Unregister; `false` if it was not registered |
| `bool HasListeners<T>(EventDelegate<T> handler)` | Is this handler registered? |
| `bool HasListeners<T>()` | Does anyone listen to `T`? |
| `bool TriggerEvent<T>(T e)` | Deliver now; `true` if any listener ran |
| `bool TriggerEvent(IEvent e)` | Same, for a value you hold as `IEvent` (boxes structs) |
| `bool QueueEvent<T>(T e)` | Deliver on the next `Update()` |
| `bool QueueEvent(IEvent e)` | Same, for a value you hold as `IEvent` (boxes structs) |
| `void Post(IEvent e)` | Thread-safe queue |
| `void Update()` | Deliver queued events |
| `void ClearQueue()` | Drop waiting events, keep listeners |
| `void Release()` | Drop waiting events and all listeners |
| `void ResetStatic()` | Same as `Release()`; kept for compatibility |

The handler type is `EventManager.EventDelegate<T>`, a method shaped `void Handler(T e)` where `T : IEvent`.

### `EventBus`

Has the same methods as `EventManager` (except `Global` and `ResetStatic`), plus these members:

| Member | Description |
|---|---|
| `int MaxEventsPerUpdate` | Delivery cap per `Update()` |
| `float MaxMillisecondsPerUpdate` | Soft time budget per `Update()` |
| `int MaxDispatchDepth` | Nested trigger limit |
| `Action<Exception> ExceptionHandler` | Where listener exceptions go |
| `Func<object, bool> TargetAliveCheck` | Optional liveness check on listener targets |
| `int QueuedCount { get; }` | Events waiting for `Update()` |

### `IEvent`, `GameEvent`

| Member | Description |
|---|---|
| `IEvent` | Empty marker interface for struct or class events |
| `GameEvent` | Optional base class for class events |
| `bool GameEvent.Consumed { get; }` | True after a listener called `Consume()` during the current delivery |
| `void GameEvent.Consume()` | Stop the remaining listeners of this delivery |

### `Subscription` (struct), `SubscriptionBag`

| Member | Description |
|---|---|
| `bool Subscription.IsValid` | `false` for `default(Subscription)` |
| `void Subscription.Dispose()` | Unsubscribe; safe to call more than once |
| `Subscription Subscription.AddTo(SubscriptionBag bag)` | Add to a bag and return itself, for chaining |
| `int SubscriptionBag.Count` | Subscriptions currently held |
| `void SubscriptionBag.Add(Subscription s)` | Add a subscription (invalid ones are ignored) |
| `void SubscriptionBag.Dispose()` | Unsubscribe everything, newest first, and empty the bag |

## 19. FAQ

**Do I have to call `Update()`?** Only for `QueueEvent` and `Post`. `TriggerEvent` runs immediately without it.

**Do I have to unsubscribe?** For anything that can be destroyed or disabled before the program ends, yes. A bag makes it one line.

**Can I send an event from inside a listener?** Yes. `TriggerEvent` nests; `QueueEvent` waits for the next `Update()`.

**Struct or class event?** Struct for frequent, small notifications. Class when listeners must write back, you need `Consume()`, or the payload is large.

**Can two classes listen to the same event?** Yes, any number. Each registers on its own, ideally with its own bag ([section 6.6](#66-subscriptions-and-bags-the-recommended-way-to-unsubscribe)).

**Can I listen to every event?** Not with one call. Matching is by exact type, so register once per type you care about.

**Is it safe to add or remove listeners while an event is being delivered?** Yes. The behavior is defined in [section 13](#13-the-rules-of-the-road).

**Does it work with async code?** Send from async code with `Post`. Handlers themselves run synchronously.

**Which engines does it work with?** Any C# runtime that can load a netstandard2.1 library. See the [Installation Guide](INSTALL.md) for Unity, Godot, MonoGame and plain .NET.

## 20. Support and feedback

Found a bug, an unexpected behavior, or a missing feature? Please open an issue.
To help me troubleshoot efficiently, please include:
- The smallest possible code snippet that reproduces the issue
- The engine and version you are using
You are also welcome to join the conversation on [![Discord](https://shields.io)](https://discord.gg/gXXvuevZQ). Please note that I am a solo developer balancing this project with a full-time job, so I truly appreciate your patience regarding response times.

[Back to the top](#astek-eventsystem-user-guide)
