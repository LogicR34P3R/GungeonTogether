using UnityEngine;

namespace GungeonTogether.Systems
{
    /// <summary>
    /// Shared lazy-GameObject singleton for MonoBehaviours that need to live across scene loads.
    /// </summary>
    public abstract class MonoSingleton<T> : MonoBehaviour where T : MonoSingleton<T>
    {
        private static T _instance;

        public static T Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject(typeof(T).Name);
                    _instance = go.AddComponent<T>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }
    }
}
