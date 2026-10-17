#if UNITY_5_3_OR_NEWER
using Astek.EventSystems.Core;
using UnityEngine;

namespace Astek.EventSystems
{
    [DefaultExecutionOrder(-10000)]
    internal sealed class EventManagerDriver : MonoBehaviour
    {
        private void Update() => EventManager.Update();
    }

    internal static class EventManagerUnityBootstrap
    {
        // Clears listeners/queue between play sessions when Domain Reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetBetweenSessions() => EventManager.ResetStatic();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            EventBus bus = EventManager.Global;
            bus.ExceptionHandler = Debug.LogException;
            bus.TargetAliveCheck = IsAlive; // set to null for zero per-listener overhead if you always unsubscribe manually

            GameObject go = new GameObject("[EventManager]") { hideFlags = HideFlags.HideInHierarchy };
            Object.DontDestroyOnLoad(go);
            go.AddComponent<EventManagerDriver>();
        }

        // Destroyed UnityEngine.Objects compare equal to null; plain C# targets are always considered alive.
        private static bool IsAlive(object target)
        {
            Object unityObject = target as Object;
            if (ReferenceEquals(unityObject, null)) return true; // not a Unity object (plain C# target)
            return unityObject != null;
        }
    }
}
#endif