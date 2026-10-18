# 🚫 CUSTOM NON-AI LICENSE

![AI Training Prohibited](https://shields.io)

This repository is free to use, modify, and integrate into your own products. However, **standalone reselling and the use of this data for training Artificial Intelligence (AI) or Large Language Models (LLMs) are strictly prohibited.** See `LICENSE` for details.
[Licence.md](https://github.com/user-attachments/files/33605994/Licence.md)

# Astek EventSystem

**A fast, garbage-free event bus for C#. Free, pure C#, and it drops into Unity, Godot or any .NET project.**

![netstandard2.1](https://img.shields.io/badge/.NET-netstandard2.1-512BD4)
![allocations](https://img.shields.io/badge/steady--state%20allocations-0%20bytes-brightgreen)
![dependencies](https://img.shields.io/badge/dependencies-none-lightgrey)

When an enemy dies, the score, the audio, the quests and the achievements all need to know, but the enemy should not have to know about any of them. Astek EventSystem is a small publish/subscribe event bus that lets your systems talk without referencing each other: subscribe where you care, send from where it happens.

It is built for games. Sending an event allocates no memory, and the tricky situations (listeners added or removed while an event is being delivered, exceptions in handlers, event loops) are handled for you instead of becoming bugs.

## Contents

- [Why use it](#why-use-it)
- [Install](#install)
- [Quick start](#quick-start)
- [Cheat sheet](#cheat-sheet)
- [How it compares](#how-it-compares)
- [Performance](#performance)
- [Guarantees](#guarantees)
- [Documentation](#documentation)
- [Compatibility](#compatibility)
- [Free, with optional donations](#free-with-optional-donations)
- [Feedback](#feedback)
- [License](#license)

## Why use it

- **Zero garbage.** Triggering, queueing and updating allocate 0 bytes in steady state, for struct and class events alike. No GC spikes from event traffic.
- **Struct events.** A `struct` event is never boxed. Class events work too, with an optional `Consume()` to stop lower-priority listeners.
- **Optimized dispatch.** Listeners live in a flat array and are called directly: no wrapper delegates, no dictionary lookup per trigger, no multicast list rebuilding.
- **Pure C#, no engine dependency.** The library uses only the .NET base class library. One DLL works in every engine that runs C#. An optional Unity helper adds auto-update, console logging and cleanup of destroyed objects.
- **Safe by design.** Add or remove listeners from inside a listener, use once-only listeners, call `Release()` mid-delivery: all defined and tested. A throwing listener is reported and the rest still run. Event cycles are caught before they overflow the stack.
- **Easy to use.** Define, listen, send. A `SubscriptionBag` unsubscribes everything an object registered with one call.
- **More when you need it.** Priorities, queued events with per-frame count or time budgets, a thread-safe `Post` for network and async code, and separate buses per scene, match, server room or unit test.

## Install

1. Download `Astek.EventSystem-1.0.0-dll.zip` from the [Releases](../../releases) page.
2. Reference `Astek.EventSystem.dll` from your project.
3. Call `EventManager.Update()` once per frame or tick (needed for queued events only).

Short version per environment (full steps in [docs/INSTALL.md](docs/INSTALL.md)):

| Environment | What to do |
|---|---|
| **Unity 2021.2+** | Set the API Compatibility Level to .NET Standard 2.1. Copy the DLL and `EventManagerUnity.cs` into `Assets/Plugins/Astek/`. The helper calls `Update()` for you |
| **Godot 4 (C#)** | Copy the DLL into `libs/`, add a `<Reference>` to your `.csproj`, add a small autoload node that calls `EventManager.Update()` in `_Process` |
| **MonoGame, Stride, Flax** | Add the DLL as a reference and call `EventManager.Update()` from your per-frame update |
| **Console, server, WinForms, WPF** | Add the reference. Call `EventManager.Update()` from your main loop or a timer, or use `TriggerEvent` only (it needs no `Update`) |

```xml
<ItemGroup>
  <Reference Include="Astek.EventSystem">
    <HintPath>libs/Astek.EventSystem.dll</HintPath>
  </Reference>
</ItemGroup>
```

Older runtimes (Unity before 2021.2, Godot 3, .NET Framework) need a `netstandard2.0` build. See [docs/INSTALL.md](docs/INSTALL.md).

## Quick start

```csharp
using Astek.DesignPattern.EventSystem;

// 1. Define an event. Struct = no garbage. Class (: GameEvent) = can Consume().
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

Any number of classes can listen to the same event. Each registers independently, ideally with its own `SubscriptionBag`.

## Cheat sheet

| I want to... | Code |
|---|---|
| Listen | `EventManager.AddListener<T>(handler)` (always write `<T>`) |
| Listen with an order | `AddListener<T>(handler, priority: 10)` (higher runs first) |
| Listen once | `AddListenerOnce<T>(handler)` |
| Unsubscribe | `subscription.Dispose()`, or dispose a `SubscriptionBag` |
| Send now | `EventManager.TriggerEvent(evt)` (returns `true` if anyone ran) |
| Send on the next update | `EventManager.QueueEvent(evt)` |
| Send from another thread | `EventManager.Post(evt)` |
| Stop lower-priority listeners | `evt.Consume()` (class events deriving from `GameEvent`) |
| Check for listeners | `EventManager.HasListeners<T>()` |
| Limit queue work per frame | `EventManager.Global.MaxEventsPerUpdate = 100;` |
| Use a separate bus | `var bus = new EventBus();` |
| Clear everything | `EventManager.Release()` |

## How it compares

| Common in simple event managers | Astek EventSystem |
|---|---|
| Garbage on every trigger (wrapper delegates, list copies, boxing) | 0 bytes in steady state |
| Struct events boxed or unsupported | Struct events are first-class |
| A listener removed mid-event may still be called | Never called once removed |
| A once-only listener can fire twice if re-triggered | Exactly once, even when re-entrant |
| One throwing listener stops the rest | Isolated and reported |
| Event loops crash with a stack overflow | Stopped by a depth limit |
| Tied to one engine | Pure C# |

## Performance

Measured on one machine, release build, class events, against the multicast-delegate event manager this library was rewritten from. Microbenchmarks vary, so run your own.

| Listeners on one event | Before | Astek EventSystem |
|---|---|---|
| 1 | about 85 ns, 32 B garbage | about 40 ns, 0 B |
| 10 | about 400 ns, 104 B | about 90 ns, 0 B |
| 100 | about 4 µs, 824 B | about 0.55 µs, 0 B |

Struct events are as fast or faster. Registering or removing thousands of listeners on a single event type is slower than the original, because each change scans that type's list. Hundreds are fine.

## Guarantees

- Listeners run by priority (high to low), then in the order they were added.
- Adding the same method on the same object twice is ignored.
- A listener removed during a delivery is not called afterwards, even in that delivery.
- A listener added during a delivery starts with the next one.
- Once-only listeners run exactly once.
- An exception in one listener never stops the others.
- Sending with no listeners is not an error.
- `Update()` delivers in queue order and never loops forever on events queued by listeners.
- Matching is by the event's exact type.
- Everything is single-threaded except `Post`.

The full list is in the [User Guide](docs/USER_GUIDE.md#13-the-rules-of-the-road).

## Documentation

| Document | What it covers |
|---|---|
| [User Guide](docs/USER_GUIDE.md) | Using the library in every situation: events, listening, sending, update loop, buses, configuration, recipes, API reference, FAQ |
| [Installation Guide](docs/INSTALL.md) | Step-by-step setup for Unity, Godot, MonoGame, Stride, Flax and plain .NET |

## Compatibility

- C# runtimes that can load a `netstandard2.1` library: Unity 2021.2+, Godot 4, MonoGame, Stride, Flax and modern .NET.
- Older runtimes: build or request the `netstandard2.0` version.
- Not supported: engines that do not run .NET code (for example Unreal, GameMaker).

**Tested with** (update this list as you verify):

- .NET 8 console, full self-test suite: yes
- Unity: 2021+
- Godot: Untested
- Other: In .net project 
## Free, with optional donations

Astek EventSystem is free to download and use. If it saves you time or keeps your project running smoother, a donation is a lovely way to say thanks and helps me keep building and polishing tools like this: [your donation link]. There is no pressure: use it either way.

## Feedback

I would love to hear how it works for you. Please [open an issue](../../issues) for:

- Bugs or confusing behavior (the smallest code snippet that shows it helps a lot)
- Features you would like, or things that felt harder than they should
- Performance numbers from your own project

[State your license here, for example MIT, or your own terms.]
