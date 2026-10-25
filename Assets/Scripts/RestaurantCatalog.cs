using System;
using UnityEngine;

namespace Guardian.UnitySim
{
    public sealed class RestaurantCatalog : ScriptableObject
    {
        [Serializable] public struct Entry { public string Id; public GameObject Prefab; }
        public Entry[] Entries;
        public GameObject WalkingGuest;
        /// <summary>Rigged guests that sit, eat and leave (GuardianDinerFlow); the walking guest is the fallback.</summary>
        public GameObject[] DinerGuests;
        public GameObject Get(string id)
        {
            foreach (Entry entry in Entries)
                if (entry.Id == id && entry.Prefab != null) return entry.Prefab;
            throw new InvalidOperationException("Restaurant prefab missing: " + id +
                ". Run Guardian > Restaurant > Build Assets.");
        }
    }
}
