using UnityEngine;
using UnityEngine.Rendering;

namespace Guardian.UnitySim
{
    /// <summary>Presentation settings only; never changes the Guardian timeline or actors.</summary>
    public sealed class RestaurantLighting : MonoBehaviour
    {
        private void Start() { Apply(transform.parent); }
        public static void Apply(Transform world)
        {
            if (world==null) return;
            Transform sun=world.Find("Sun Light");
            if (sun==null) return;
            Light light=sun.GetComponent<Light>();
            light.color=new Color(1f,.96f,.88f);
            light.intensity=.95f;
            light.shadows=LightShadows.Soft;
            light.shadowStrength=.58f;
            light.shadowBias=.035f;
            light.shadowNormalBias=.18f;
            light.shadowResolution=LightShadowResolution.High;
            RenderSettings.ambientMode=AmbientMode.Trilight;
            RenderSettings.ambientSkyColor=new Color(.48f,.51f,.55f);
            RenderSettings.ambientEquatorColor=new Color(.35f,.33f,.29f);
            RenderSettings.ambientGroundColor=new Color(.19f,.17f,.14f);
            RenderSettings.reflectionIntensity=.6f;
            QualitySettings.shadows=ShadowQuality.All;
            QualitySettings.shadowResolution=ShadowResolution.High;
            QualitySettings.shadowDistance=45;
            QualitySettings.shadowCascades=2;
            QualitySettings.pixelLightCount=4;
            QualitySettings.antiAliasing=4;
        }
    }
}
