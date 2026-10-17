namespace Astek.EventSystems
{
    /// <summary>
    /// Marker for anything that can be sent through the event system.
    /// Implement it on a class OR a struct:
    /// <code>
    /// public struct DamageEvent : IEvent { public int Amount; }   // zero-allocation
    /// public class  QuestDone   : GameEvent { public string Id; } // classic, supports Consume()
    /// </code>
    /// </summary>
    public interface IEvent { }

    /// <summary>
    /// Optional base class for class-based events (the original base class, fully backwards compatible).
    /// Adds <see cref="Consume"/> so a high-priority listener can stop lower-priority ones from receiving the event.
    /// </summary>
    public class GameEvent : IEvent
    {
        /// <summary>True once a listener called <see cref="Consume"/> during the current dispatch.</summary>
        public bool Consumed { get; private set; }
 
        /// <summary>Stops delivery to the remaining (lower-priority) listeners of this dispatch.</summary>
        public void Consume() => Consumed = true;
 
        // Reset automatically at the start of each dispatch, so pooled/reused events are safe.
        internal void ResetConsumed() => Consumed = false;
    }

}