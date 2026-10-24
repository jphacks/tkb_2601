using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Static original person. Seat contact is explicit; there is no crowd AI.</summary>
    public sealed class RestaurantPerson : MonoBehaviour
    {
        public bool Standing;
        public RestaurantFixture Seat;
    }
}
