using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Individually placeable authored furniture for layout validation.</summary>
    public sealed class RestaurantFixture : MonoBehaviour
    {
        public string AssetId;
        public bool BlocksRoutes = true;
    }
}
