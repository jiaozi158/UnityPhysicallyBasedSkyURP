using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;
using Unity.Mathematics;
using static Unity.Mathematics.math;

#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

/// <summary>
/// A renderer feature that adds physically based sky and precomputed atmospheric scattering support to the URP volume.
/// </summary>
[DisallowMultipleRendererFeature("Physically Based Sky URP")]
[Tooltip("Add this Renderer Feature to support visual environment override in URP Volume.")]
[HelpURL("https://github.com/jiaozi158/UnityPhysicallyBasedSkyURP/tree/main")]
public class PhysicallyBasedSkyURP : ScriptableRendererFeature
{
    public enum PrecomputationQualityMode
    {
        [InspectorName("High")]
        [Tooltip("Generates full resolution look-up tables.")]
        High = 0,

        [InspectorName("Low")]
        [Tooltip("Generates half resolution look-up tables.")]
        Low = 1,
    }

    private Material m_PbrSkyMaterial;
    private Material m_PbrSkyLUTMaterial;

    [Header("Setup")]
    [Tooltip("The shader of physically based sky.")]
    [SerializeField] private Shader m_Shader;
    [Tooltip("The precomputation shader of physically based sky.")]
    [SerializeField] private Shader m_LutShader;

    [Header("Performance")]
    [Tooltip("The precomputation quality of physically based sky.")]
    [SerializeField] private PrecomputationQualityMode m_Precomputation = PrecomputationQualityMode.High;

    private bool isShaderMismatchLogPrinted;
    private int lastSkyType;
    private VisualEnvironment.SkyAmbientMode lastSkyAmbientMode;
    
    private CelestialBodyData m_CelestialBodyData = new CelestialBodyData();

    private PBSkyPrePass m_PBSkyPrePass;
    private SkyViewLUTPass m_SkyViewLUTPass;
    private AtmosphericScatteringPass m_AtmosphericScatteringPass;
    private AmbientProbePass m_AmbientProbePass;
    private PBSkyPostPass m_PBSkyPostPass;

    [Header("Sky")]
    [Tooltip("The fallback sky material when physically based sky is disabled.")]
    [SerializeField] private Material m_FallbackSkyMaterial;

    [Header("Volumetric Clouds")]
    [Tooltip("[Optional] The material of volumetric clouds used when updating sky reflection.")]
    [SerializeField] private Material m_VolumetricCloudsMaterial;

    private const string k_PbrSkyShaderName = "Hidden/Skybox/PhysicallyBasedSky";
    private const string k_PbrSkyLutShaderName = "Hidden/Sky/PhysicallyBasedSkyPrecomputation";

    private const string k_CloudsShaderName = "Hidden/Sky/VolumetricClouds";
    private const string k_PbrSkyMaterialName = "Physically Based Sky";
    private const string k_DynamicAmbientProbeKeywordName = "VISUAL_ENVIRONMENT_DYNAMIC_SKY";
    private const string k_AtmosphericScatteringLowResolutionKeywordName = "ATMOSPHERIC_SCATTERING_LOW_RES";

    /// <summary>
    /// Get the skybox material of physically based sky.
    /// </summary>
    /// <value>
    /// The material of physically based sky.
    /// </value>
    public Material PBRSkyMaterial
    {
        get { return m_PbrSkyMaterial; }
    }

    /// <summary>
    /// Get or set the fallback sky material when physically based sky is disabled.
    /// </summary>
    /// <value>
    /// The material of fallback sky shader.
    /// </value>
    public Material FallbackSkyMaterial
    {
        get { return m_FallbackSkyMaterial; }
        set { m_FallbackSkyMaterial = value; }
    }

    /// <summary>
    /// Get or set the material of volumetric clouds shader.
    /// </summary>
    /// <value>
    /// [Optional] The material of "Hidden/Sky/VolumetricClouds" shader used when updating sky reflection.
    /// </value>
    public Material CloudsMaterial
    {
        get { return m_VolumetricCloudsMaterial; }
        set { m_VolumetricCloudsMaterial = value; ValidateCloudsMaterial(); }
    }

    /// <summary>
    /// Get or set the shader of physically based sky.
    /// </summary>
    /// <value>
    /// The shader of physically based sky.
    /// </value>
    public Shader PBSkyShader
    {
        get { return m_Shader; }
        set { m_Shader = value; }
    }

    /// <summary>
    /// Get or set the precomputation shader of physically based sky.
    /// </summary>
    /// <value>
    /// The precomputation shader of physically based sky.
    /// </value>
    public Shader PBSkyLutShader
    {
        get { return m_LutShader; }
        set { m_LutShader = value; }
    }

    /// <summary>
    /// Get or set the precomputation quality of physically based sky.
    /// </summary>
    /// <value>
    /// The precomputation quality of physically based sky.
    /// </value>
    public PrecomputationQualityMode PrecomputationQuality
    {
        get { return m_Precomputation; }
        set { m_Precomputation = value; }
    }

    public struct CelestialBodyData
    {
        public Vector3 color;
        public float radius;

        public Vector3 forward;
        public float distanceFromCamera;
        public Vector3 right;
        public float angularRadius;       // Units: radians
        public Vector3 up;
        public int type;                  // 0: star, 1: moon

        public Vector3 surfaceColor;
        public float earthshine;

        public Vector4 surfaceTextureScaleOffset; // -1 if unused (TODO: 16 bit)

        public Vector3 sunDirection;
        public float flareCosInner;

        //public Vector2 phaseAngleSinCos;
        public float flareCosOuter;
        public float flareSize;           // Units: radians

        public Vector3 flareColor;
        public float flareFalloff;

        //public Vector3 padding;
        //public int shadowIndex;
    };

    public override void Create()
    {
        var stack = VolumeManager.instance.stack;
        PhysicallyBasedSky pbrSkyVolume = stack.GetComponent<PhysicallyBasedSky>();
        VisualEnvironment visualEnvVolume = stack.GetComponent<VisualEnvironment>();

        // Validate sky shaders
        bool shadersValid = true;
        if (m_Shader != Shader.Find(k_PbrSkyShaderName))
        {
    #if UNITY_EDITOR || DEBUG
            if (!isShaderMismatchLogPrinted)
            {
                Debug.LogErrorFormat("Physically Based Sky URP: Skybox shader is not {0}.", k_PbrSkyShaderName);
                isShaderMismatchLogPrinted = true;
            }
    #endif
            shadersValid = false;
        }

        if (m_LutShader != Shader.Find(k_PbrSkyLutShaderName))
        {
    #if UNITY_EDITOR || DEBUG
            if (!isShaderMismatchLogPrinted)
            {
                Debug.LogErrorFormat("Physically Based Sky URP: LUT shader is not {0}.", k_PbrSkyLutShaderName);
                isShaderMismatchLogPrinted = true;
            }
    #endif
            shadersValid = false;
        }

        if (!shadersValid) return;
        isShaderMismatchLogPrinted = false;

        // Cleanup settings when disabled
        if (!isActive)
        {
            bool isCustomSkyType = visualEnvVolume != null && visualEnvVolume.IsActive() && visualEnvVolume.skyType.value == (int)VisualEnvironment.SkyType.Custom && visualEnvVolume.customSkyMaterial.value != null;

            RenderSettings.skybox = isCustomSkyType ? visualEnvVolume.customSkyMaterial.value : m_FallbackSkyMaterial;
            RenderSettings.customReflectionTexture = null;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;

            Shader.DisableKeyword(k_DynamicAmbientProbeKeywordName);

        #if UNITY_EDITOR
            // Update ambient probe
            if (RenderSettings.skybox != null)
            {
                DynamicGI.UpdateEnvironment();
            }
        #endif
            return;
        }

        // Initialize sky materials
        m_PbrSkyMaterial = CoreUtils.CreateEngineMaterial(m_Shader);
        m_PbrSkyLUTMaterial = CoreUtils.CreateEngineMaterial(m_LutShader);
        m_PbrSkyMaterial.name = k_PbrSkyMaterialName;

        // Initialize render passes
        m_PBSkyPrePass ??= new PBSkyPrePass(m_PbrSkyMaterial, m_CelestialBodyData)
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses
        };

        m_PBSkyPrePass.material = m_PbrSkyMaterial;
        m_PBSkyPrePass.lutMaterial = m_PbrSkyLUTMaterial;

        m_SkyViewLUTPass ??= new SkyViewLUTPass(m_PbrSkyLUTMaterial, ref m_CelestialBodyData)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses
        };

        m_SkyViewLUTPass.lutMaterial = m_PbrSkyLUTMaterial;

        m_AtmosphericScatteringPass ??= new AtmosphericScatteringPass(m_PbrSkyLUTMaterial)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingSkybox + 1
        };

        m_AtmosphericScatteringPass.lutMaterial = m_PbrSkyLUTMaterial;

        m_AmbientProbePass ??= new AmbientProbePass(m_VolumetricCloudsMaterial)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses
        };

        m_PBSkyPostPass ??= new PBSkyPostPass()
        {
            renderPassEvent = RenderPassEvent.AfterRendering
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // Do not add render passes if any error occurs.
        bool shouldDisable = isShaderMismatchLogPrinted || m_PbrSkyMaterial == null || m_PbrSkyLUTMaterial == null;

        shouldDisable |= renderingData.cameraData.camera == null;

        shouldDisable |= renderingData.cameraData.camera.cameraType == CameraType.Preview;

        if (shouldDisable)
            return;

        var stack = VolumeManager.instance.stack;
        PhysicallyBasedSky pbrSkyVolume = stack.GetComponent<PhysicallyBasedSky>();
        VisualEnvironment visualEnvVolume = stack.GetComponent<VisualEnvironment>();
        Fog fogVolume = stack.GetComponent<Fog>();

        bool isPbrSky = pbrSkyVolume != null && visualEnvVolume != null && visualEnvVolume.IsActive() && visualEnvVolume.skyType.value == (int)VisualEnvironment.SkyType.PhysicallyBased;

        {
            bool halfResolutionLuts = m_Precomputation == PrecomputationQualityMode.Low;

            m_PBSkyPrePass.pbrSky = pbrSkyVolume;
            m_SkyViewLUTPass.pbrSky = pbrSkyVolume;
            m_AtmosphericScatteringPass.pbrSky = pbrSkyVolume;

            m_PBSkyPrePass.visualEnvironment = visualEnvVolume;
            m_SkyViewLUTPass.visualEnvironment = visualEnvVolume;
            m_AtmosphericScatteringPass.visualEnvironment = visualEnvVolume;

            m_PBSkyPrePass.fog = fogVolume;
            m_AtmosphericScatteringPass.fog = fogVolume;

            m_SkyViewLUTPass.halfResolutionLuts = halfResolutionLuts;

            if (isPbrSky)
                CoreUtils.SetKeyword(m_PbrSkyMaterial, k_AtmosphericScatteringLowResolutionKeywordName, halfResolutionLuts);
            CoreUtils.SetKeyword(m_PbrSkyLUTMaterial, k_AtmosphericScatteringLowResolutionKeywordName, halfResolutionLuts);

            bool hasFog = isPbrSky && pbrSkyVolume.atmosphericScattering.value || (fogVolume != null && fogVolume.IsActive());

        #if UNITY_EDITOR
            bool isEditingPrefab = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() != null;
            bool isSceneViewFocused = UnityEditor.SceneView.lastActiveSceneView != null && UnityEditor.SceneView.lastActiveSceneView.hasFocus;
            // Disable atmospheric scattering and fog when entering prefab mode.
            hasFog &= !(isEditingPrefab && isSceneViewFocused);
        #endif

            if (isPbrSky)
            {
                renderer.EnqueuePass(m_PBSkyPrePass);

                m_SkyViewLUTPass.celestialBodyData = m_PBSkyPrePass.celestialBodyData;

                renderer.EnqueuePass(m_SkyViewLUTPass);
            }

            if (hasFog && renderingData.cameraData.camera.cameraType != CameraType.Reflection)
                renderer.EnqueuePass(m_AtmosphericScatteringPass);
            
            renderer.EnqueuePass(m_PBSkyPostPass);
        }

        if (visualEnvVolume.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic && renderingData.cameraData.camera.cameraType != CameraType.Reflection && RenderSettings.skybox != null)
        {
            m_AmbientProbePass.visualEnvironment = visualEnvVolume;
            m_AmbientProbePass.cloudsMaterial = ValidateCloudsMaterial();
            m_AmbientProbePass.isPbrSky = isPbrSky;
            Shader.EnableKeyword(k_DynamicAmbientProbeKeywordName);
            renderer.EnqueuePass(m_AmbientProbePass);
        }
        else
        {
            Shader.DisableKeyword(k_DynamicAmbientProbeKeywordName);
        }

        UpdateSkySettings(isPbrSky, visualEnvVolume);
    }

    protected override void Dispose(bool disposing)
    {
        if (m_PBSkyPrePass != null)
            m_PBSkyPrePass.Dispose();

        if (m_SkyViewLUTPass != null)
            m_SkyViewLUTPass.Dispose();

        if (m_AtmosphericScatteringPass != null)
            m_AtmosphericScatteringPass.Dispose();

        if (m_AmbientProbePass != null)
            m_AmbientProbePass.Dispose();

        if (m_PBSkyPostPass != null)
            m_PBSkyPostPass.Dispose();

        if (m_PbrSkyMaterial != null)
            CoreUtils.Destroy(m_PbrSkyMaterial);

        if (m_PbrSkyLUTMaterial != null)
            CoreUtils.Destroy(m_PbrSkyLUTMaterial);
    }

    private Material ValidateCloudsMaterial()
    {
        return m_VolumetricCloudsMaterial != null && m_VolumetricCloudsMaterial.shader == Shader.Find(k_CloudsShaderName)
            ? m_VolumetricCloudsMaterial
            : null;
    }

    private void UpdateSkySettings(bool isPbrSky, VisualEnvironment visualEnvVolume)
    {
        bool isCustomSky = visualEnvVolume.skyType.value == (int)VisualEnvironment.SkyType.Custom;
        bool isCustomSkyValid = visualEnvVolume.customSkyMaterial.value != null;

        bool isDynamicSky = visualEnvVolume.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic;

        bool isSkyTypeChanged = lastSkyType != visualEnvVolume.skyType.value;
        bool isAmbientModeChanged = lastSkyAmbientMode != visualEnvVolume.skyAmbientMode.value;
        
        // Reset the sky reflection texture
        if (!isDynamicSky && isAmbientModeChanged)
        {
            RenderSettings.customReflectionTexture = null;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        }

        // Update the sky material
        RenderSettings.skybox = isPbrSky
            ? m_PbrSkyMaterial
            : isCustomSky && isCustomSkyValid
            ? visualEnvVolume.customSkyMaterial.value
            : isSkyTypeChanged // executes once only
            ? m_FallbackSkyMaterial
            : RenderSettings.skybox;

    #if UNITY_EDITOR
        // Re-bake the sky ambient probe
        if (isSkyTypeChanged && (!isPbrSky || !isDynamicSky) && RenderSettings.skybox != null)
        {
            DynamicGI.UpdateEnvironment();
        }
    #endif  

        lastSkyType = visualEnvVolume.skyType.value;
        lastSkyAmbientMode = visualEnvVolume.skyAmbientMode.value;
    }

    /// <summary>
    /// This pass updates the global shader properties of physically based sky.
    /// </summary>
    private class PBSkyPrePass : ScriptableRenderPass
    {
        private const string profilerTag = "Setup Physically Based Sky";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(profilerTag);

        public PhysicallyBasedSky pbrSky;
        public VisualEnvironment visualEnvironment;
        public Fog fog;

        public CelestialBodyData celestialBodyData;

        public Material material;
        public Material lutMaterial;

        private static readonly int _AtmosphericRadius = Shader.PropertyToID("_AtmosphericRadius");
        private static readonly int _AerosolAnisotropy = Shader.PropertyToID("_AerosolAnisotropy");
        private static readonly int _AerosolPhasePartConstant = Shader.PropertyToID("_AerosolPhasePartConstant");
        private static readonly int _AerosolSeaLevelExtinction = Shader.PropertyToID("_AerosolSeaLevelExtinction");
        private static readonly int _AirDensityFalloff = Shader.PropertyToID("_AirDensityFalloff");
        private static readonly int _AirScaleHeight = Shader.PropertyToID("_AirScaleHeight");
        private static readonly int _AerosolDensityFalloff = Shader.PropertyToID("_AerosolDensityFalloff");
        private static readonly int _AerosolScaleHeight = Shader.PropertyToID("_AerosolScaleHeight");
        private static readonly int _OzoneScaleOffset = Shader.PropertyToID("_OzoneScaleOffset");
        private static readonly int _OzoneLayerStart = Shader.PropertyToID("_OzoneLayerStart");
        private static readonly int _OzoneLayerEnd = Shader.PropertyToID("_OzoneLayerEnd");
        private static readonly int _AirSeaLevelExtinction = Shader.PropertyToID("_AirSeaLevelExtinction");
        private static readonly int _AirSeaLevelScattering = Shader.PropertyToID("_AirSeaLevelScattering");
        private static readonly int _AerosolSeaLevelScattering = Shader.PropertyToID("_AerosolSeaLevelScattering");
        private static readonly int _OzoneSeaLevelExtinction = Shader.PropertyToID("_OzoneSeaLevelExtinction");
        private static readonly int _GroundAlbedo_PlanetRadius = Shader.PropertyToID("_GroundAlbedo_PlanetRadius");
        private static readonly int _HorizonTint = Shader.PropertyToID("_HorizonTint");
        private static readonly int _ZenithTint = Shader.PropertyToID("_ZenithTint");
        private static readonly int _IntensityMultiplier = Shader.PropertyToID("_IntensityMultiplier");
        private static readonly int _ColorSaturation = Shader.PropertyToID("_ColorSaturation");
        private static readonly int _AlphaSaturation = Shader.PropertyToID("_AlphaSaturation");
        private static readonly int _AlphaMultiplier = Shader.PropertyToID("_AlphaMultiplier");
        private static readonly int _HorizonZenithShiftPower = Shader.PropertyToID("_HorizonZenithShiftPower");
        private static readonly int _HorizonZenithShiftScale = Shader.PropertyToID("_HorizonZenithShiftScale");
        private static readonly int _CelestialLightCount = Shader.PropertyToID("_CelestialLightCount");
        private static readonly int _CelestialBodyCount = Shader.PropertyToID("_CelestialBodyCount");
        private static readonly int _AtmosphericDepth = Shader.PropertyToID("_AtmosphericDepth");
        private static readonly int _RcpAtmosphericDepth = Shader.PropertyToID("_RcpAtmosphericDepth");
        private static readonly int _CelestialLightExposure = Shader.PropertyToID("_CelestialLightExposure");

        private static readonly int _DisableSunDisk = Shader.PropertyToID("_DisableSunDisk");

        private static readonly int _HasGroundAlbedoTexture = Shader.PropertyToID("_HasGroundAlbedoTexture");
        private static readonly int _GroundAlbedoTexture = Shader.PropertyToID("_GroundAlbedoTexture");

        private static readonly int _HasGroundEmissionTexture = Shader.PropertyToID("_HasGroundEmissionTexture");
        private static readonly int _GroundEmissionTexture = Shader.PropertyToID("_GroundEmissionTexture");
        private static readonly int _GroundEmissionMultiplier = Shader.PropertyToID("_GroundEmissionMultiplier");

        private static readonly int _HasSpaceEmissionTexture = Shader.PropertyToID("_HasSpaceEmissionTexture");
        private static readonly int _SpaceEmissionTexture = Shader.PropertyToID("_SpaceEmissionTexture");
        private static readonly int _SpaceEmissionMultiplier = Shader.PropertyToID("_SpaceEmissionMultiplier");

        private static readonly int _PlanetRotation = Shader.PropertyToID("_PlanetRotation");
        private static readonly int _SpaceRotation = Shader.PropertyToID("_SpaceRotation");

        private static readonly int _PlanetCenterRadius = Shader.PropertyToID("_PlanetCenterRadius");
        private static readonly int _PlanetUpAltitude = Shader.PropertyToID("_PlanetUpAltitude");

        private static readonly int _PBRSkyCameraPosPS = Shader.PropertyToID("_PBRSkyCameraPosPS");

        private static readonly int _CelestialBody_Color = Shader.PropertyToID("_CelestialBody_Color");
        private static readonly int _CelestialBody_Radius = Shader.PropertyToID("_CelestialBody_Radius");
        private static readonly int _CelestialBody_Forward = Shader.PropertyToID("_CelestialBody_Forward");
        private static readonly int _CelestialBody_DistanceFromCamera = Shader.PropertyToID("_CelestialBody_DistanceFromCamera");
        private static readonly int _CelestialBody_Right = Shader.PropertyToID("_CelestialBody_Right");
        private static readonly int _CelestialBody_AngularRadius = Shader.PropertyToID("_CelestialBody_AngularRadius");
        private static readonly int _CelestialBody_Up = Shader.PropertyToID("_CelestialBody_Up");
        private static readonly int _CelestialBody_Type = Shader.PropertyToID("_CelestialBody_Type");
        private static readonly int _CelestialBody_SurfaceColor = Shader.PropertyToID("_CelestialBody_SurfaceColor");
        private static readonly int _CelestialBody_Earthshine = Shader.PropertyToID("_CelestialBody_Earthshine");
        private static readonly int _CelestialBody_SurfaceTextureScaleOffset = Shader.PropertyToID("_CelestialBody_SurfaceTextureScaleOffset");
        private static readonly int _CelestialBody_SunDirection = Shader.PropertyToID("_CelestialBody_SunDirection");
        private static readonly int _CelestialBody_FlareCosInner = Shader.PropertyToID("_CelestialBody_FlareCosInner");
        private static readonly int _CelestialBody_FlareCosOuter = Shader.PropertyToID("_CelestialBody_FlareCosOuter");
        private static readonly int _CelestialBody_FlareSize = Shader.PropertyToID("_CelestialBody_FlareSize");
        private static readonly int _CelestialBody_FlareColor = Shader.PropertyToID("_CelestialBody_FlareColor");
        private static readonly int _CelestialBody_FlareFalloff = Shader.PropertyToID("_CelestialBody_FlareFalloff");

        private static readonly int _MainLightColor = Shader.PropertyToID("_MainLightColor");
        private static readonly int _EnableAtmosphericScattering = Shader.PropertyToID("_EnableAtmosphericScattering");

        private const string PHYSICALLY_BASED_SKY = "PHYSICALLY_BASED_SKY";
        private const string LOCAL_SKY = "LOCAL_SKY";
        private const string SKY_NOT_BAKING = "SKY_NOT_BAKING";

        private SphericalHarmonicsL2 ambientProbe = new SphericalHarmonicsL2();

        private const int fibonacciSamplesCount = 64;
        private static readonly float3[] fibonacciSamples = new float3[] {
            new float3(-0.000000f, -1.000000f, -0.000000f),
            new float3(0.184319f, -0.968254f, 0.168851f),
            new float3(-0.030656f, -0.936508f, -0.349304f),
            new float3(-0.259145f, -0.904762f, 0.338009f),
            new float3(0.480237f, -0.873016f, -0.084947f),
            new float3(-0.456147f, -0.841270f, -0.290163f),
            new float3(0.152410f, -0.809524f, 0.566959f),
            new float3(0.289698f, -0.777778f, -0.557796f),
            new float3(-0.625504f, -0.746032f, 0.228433f),
            new float3(0.646907f, -0.714286f, 0.267034f),
            new float3(-0.309767f, -0.682540f, -0.661955f),
            new float3(-0.227233f, -0.650794f, 0.724454f),
            new float3(0.679497f, -0.619048f, -0.393782f),
            new float3(-0.790490f, -0.587302f, -0.173787f),
            new float3(0.478208f, -0.555556f, 0.680202f),
            new float3(0.109470f, -0.523810f, -0.844772f),
            new float3(-0.665672f, -0.492063f, 0.561029f),
            new float3(0.886996f, -0.460317f, 0.036680f),
            new float3(-0.640433f, -0.428571f, -0.637316f),
            new float3(0.042399f, -0.396825f, 0.916914f),
            new float3(0.596485f, -0.365079f, -0.714788f),
            new float3(-0.934389f, -0.333333f, 0.125721f),
            new float3(0.782638f, -0.301587f, 0.544539f),
            new float3(-0.211340f, -0.269841f, -0.939426f),
            new float3(-0.482883f, -0.238095f, 0.842695f),
            new float3(0.932189f, -0.206349f, -0.297394f),
            new float3(-0.893846f, -0.174603f, -0.412980f),
            new float3(0.382101f, -0.142857f, 0.913012f),
            new float3(0.336357f, -0.111111f, -0.935157f),
            new float3(-0.882397f, -0.079365f, 0.463763f),
            new float3(0.965873f, -0.047619f, 0.254601f),
            new float3(-0.540770f, -0.015873f, -0.841021f),
            new float3(-0.169355f, 0.015873f, 0.985427f),
            new float3(0.789726f, 0.047619f, -0.611608f),
            new float3(-0.993442f, 0.079365f, -0.082305f),
            new float3(0.674874f, 0.111111f, 0.729520f),
            new float3(-0.004828f, 0.142857f, -0.989732f),
            new float3(-0.661564f, 0.174603f, 0.729278f),
            new float3(0.974303f, 0.206349f, -0.090298f),
            new float3(-0.773652f, 0.238095f, -0.587174f),
            new float3(0.172344f, 0.269841f, 0.947356f),
            new float3(0.507807f, 0.301587f, -0.806956f),
            new float3(-0.909280f, 0.333333f, 0.249196f),
            new float3(0.828277f, 0.365079f, 0.425058f),
            new float3(-0.319075f, 0.396825f, -0.860651f),
            new float3(-0.340661f, 0.428571f, 0.836825f),
            new float3(0.802224f, 0.460317f, -0.380189f),
            new float3(-0.831918f, 0.492063f, -0.256489f),
            new float3(0.430707f, 0.523810f, 0.734925f),
            new float3(0.174571f, 0.555556f, -0.812947f),
            new float3(-0.659835f, 0.587302f, 0.468716f),
            new float3(0.779324f, 0.619048f, 0.097126f),
            new float3(-0.492126f, 0.650794f, -0.578169f),
            new float3(-0.026634f, 0.682540f, 0.730363f),
            new float3(0.491237f, 0.714286f, -0.498480f),
            new float3(-0.665042f, 0.746032f, 0.034004f),
            new float3(0.484541f, 0.777778f, 0.400352f),
            new float3(-0.081124f, 0.809524f, -0.581455f),
            new float3(-0.306594f, 0.841270f, 0.445270f),
            new float3(0.475272f, 0.873016f, -0.109359f),
            new float3(-0.370575f, 0.904762f, -0.209953f),
            new float3(0.108202f, 0.936508f, 0.333534f),
            new float3(0.103734f, 0.968254f, -0.227428f),
            new float3(-0.000000f, 1.000000f, 0.000000f)
        };

        public PBSkyPrePass(Material material, CelestialBodyData celestialBodyData)
        {
            this.material = material;
            this.celestialBodyData = celestialBodyData;
        }

        #region Non Render Graph Pass
    #if !UNITY_6000_4_OR_NEWER
        // Passing the final sun color to the Execute() method
        private float3 mainLightColor;

        private Light GetMainLight(LightData lightData)
        {
            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex != -1)
            {
                VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
                Light light = shadowLight.light;
                if ((light.shadows != LightShadows.None || RenderSettings.sun != null && !RenderSettings.sun.isActiveAndEnabled) && shadowLight.lightType == LightType.Directional)
                    return light;
            }

            return RenderSettings.sun;
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            Light mainLight = GetMainLight(renderingData.lightData);

            if (mainLight != null)
            {
                float3 sunAttenuation = EvaluateSunColorAttenuation(float3(camera.transform.position) - visualEnvironment.GetPlanetCenterRadius(camera.transform.position).xyz, -mainLight.transform.forward);

                Color color = mainLight.color.linear * (mainLight.useColorTemperature ? Mathf.CorrelatedColorTemperatureToRGB(mainLight.colorTemperature) : Color.white);
                mainLightColor = float3(color.r, color.g, color.b) * mainLight.intensity * sunAttenuation;

            #if URP_PHYSICAL_LIGHT
                bool isPhysicalLight = mainLight.GetComponent<AdditionalLightData>() != null;

                mainLightColor = isPhysicalLight ? mainLightColor * rcp(PI) : mainLightColor;
            #endif
            }

            UpdateMaterialProperties(mainLight, camera, material);
            lutMaterial.CopyPropertiesFromMaterial(material);

            if (mainLight != null && visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic)
            {
                ambientProbe = UpdateAmbientProbe(ambientProbe, mainLight.transform.forward, mainLightColor);
                RenderSettings.ambientProbe = ambientProbe;
            }
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                bool isReflectionCamera = renderingData.cameraData.camera.cameraType == CameraType.Reflection;
                cmd.SetGlobalFloat(_DisableSunDisk, isReflectionCamera ? 1.0f : 0.0f);
                cmd.SetGlobalVector(_MainLightColor, float4(mainLightColor, 0.0f));
                cmd.EnableShaderKeyword(PHYSICALLY_BASED_SKY);
                cmd.EnableShaderKeyword(SKY_NOT_BAKING);
                cmd.SetGlobalFloat(_EnableAtmosphericScattering, pbrSky.atmosphericScattering.value ? 1.0f : 0.0f);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            
            CommandBufferPool.Release(cmd);
        }
    #endif
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass

        private Light GetMainLight(UniversalLightData lightData)
        {
            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex != -1)
            {
                VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
                Light light = shadowLight.light;
                if ((light.shadows != LightShadows.None || RenderSettings.sun != null && !RenderSettings.sun.isActiveAndEnabled) && shadowLight.lightType == LightType.Directional)
                    return light;
            }

            return RenderSettings.sun;
        }

        private class PassData
        {
            internal Vector3 mainLightColor;
            internal bool enableAtmosphericScattering;
            internal bool isReflectionCamera;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            cmd.SetGlobalFloat(_DisableSunDisk, data.isReflectionCamera ? 1.0f : 0.0f);
            cmd.SetGlobalVector(_MainLightColor, data.mainLightColor);
            cmd.EnableShaderKeyword(PHYSICALLY_BASED_SKY);
            cmd.EnableShaderKeyword(SKY_NOT_BAKING);
            cmd.SetGlobalFloat(_EnableAtmosphericScattering, data.enableAtmosphericScattering ? 1.0f : 0.0f);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(profilerTag, out var passData))
            {
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                Light mainLight = GetMainLight(lightData);
                Camera camera = cameraData.camera;

                float3 mainLightColor = 0.0f;
                if (mainLight != null)
                {
                    float3 sunAttenuation = EvaluateSunColorAttenuation(float3(camera.transform.position) - visualEnvironment.GetPlanetCenterRadius(camera.transform.position).xyz, -mainLight.transform.forward);

                    Color color = mainLight.color.linear * (mainLight.useColorTemperature ? Mathf.CorrelatedColorTemperatureToRGB(mainLight.colorTemperature) : Color.white);
                    mainLightColor = float3(color.r, color.g, color.b) * mainLight.intensity * sunAttenuation;

                #if URP_PHYSICAL_LIGHT
                    bool isPhysicalLight = mainLight.GetComponent<AdditionalLightData>() != null;

                    mainLightColor = isPhysicalLight ? mainLightColor * rcp(PI) : mainLightColor;
                #endif
                }

                UpdateMaterialProperties(mainLight, camera, material);
                lutMaterial.CopyPropertiesFromMaterial(material);

                if (mainLight != null && visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic)
                {
                    ambientProbe = UpdateAmbientProbe(ambientProbe, mainLight.transform.forward, mainLightColor);
                    RenderSettings.ambientProbe = ambientProbe;
                }

                passData.mainLightColor = mainLightColor;
                passData.enableAtmosphericScattering = pbrSky.atmosphericScattering.value;
                passData.isReflectionCamera = cameraData.camera.cameraType == CameraType.Reflection;

                builder.AllowGlobalStateModification(true);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {

        }

        SphericalHarmonicsL2 UpdateAmbientProbe(SphericalHarmonicsL2 ambientProbe, float3 lightDirection, float3 lightColor)
        {
            ambientProbe.Clear();

            float weightOverPdf = 4.0f * PI * rcp(fibonacciSamplesCount);
            for (int i = 0; i < fibonacciSamplesCount; i++)
            {
                float3 V = fibonacciSamples[i];

                pbrSky.RenderSky(-lightDirection, lightColor, V, out float3 skyColor, out _);

                Color color = new Color(skyColor.x, skyColor.y, skyColor.z);
                ambientProbe.AddDirectionalLight(V, color, weightOverPdf);
            }
            return ambientProbe;
        }

        private void UpdateMaterialProperties(Light mainLight, Camera camera, Material material)
        {
            float4 planetCenterRadius = visualEnvironment.GetPlanetCenterRadius(camera.transform.position);

            float R = planetCenterRadius.w;
            float D = pbrSky.GetMaximumAltitude();
            float airH = pbrSky.GetAirScaleHeight();
            float aerH = pbrSky.GetAerosolScaleHeight();
            float aerA = pbrSky.aerosolAnisotropy.value;
            float ozoS = pbrSky.GetOzoneLayerMinimumAltitude();
            float ozoW = pbrSky.GetOzoneLayerWidth();

            float skyIntensityMultiplier = pbrSky.GetIntensityFromSettings();

            float2 expParams = ComputeExponentialInterpolationParams(pbrSky.horizonZenithShift.value);

            material.SetFloat(_AtmosphericDepth, D);
            Shader.SetGlobalFloat(_RcpAtmosphericDepth, 1.0f / D);
            Shader.SetGlobalFloat(_AtmosphericRadius, R + D);
            Shader.SetGlobalFloat(_AerosolAnisotropy, aerA);
            Shader.SetGlobalFloat(_AerosolPhasePartConstant, CornetteShanksPhasePartConstant(aerA));

            Shader.SetGlobalFloat(_AirDensityFalloff, 1.0f / airH);
            Shader.SetGlobalFloat(_AirScaleHeight, airH);
            Shader.SetGlobalFloat(_AerosolDensityFalloff, 1.0f / aerH);
            Shader.SetGlobalFloat(_AerosolScaleHeight, aerH);

            Shader.SetGlobalVector(_AirSeaLevelExtinction, pbrSky.GetAirExtinctionCoefficient());
            Shader.SetGlobalFloat(_AerosolSeaLevelExtinction, pbrSky.GetAerosolExtinctionCoefficient());

            material.SetVector(_AirSeaLevelScattering, pbrSky.GetAirScatteringCoefficient());
            Shader.SetGlobalFloat(_IntensityMultiplier, skyIntensityMultiplier);

            Shader.SetGlobalVector(_AerosolSeaLevelScattering, pbrSky.GetAerosolScatteringCoefficient());
            Shader.SetGlobalFloat(_ColorSaturation, pbrSky.colorSaturation.value);

            Shader.SetGlobalVector(_OzoneSeaLevelExtinction, pbrSky.GetOzoneExtinctionCoefficient());
            Shader.SetGlobalVector(_OzoneScaleOffset, new Vector2(2.0f / ozoW, -2.0f * ozoS / ozoW - 1.0f));
            Shader.SetGlobalFloat(_OzoneLayerStart, R + ozoS);
            Shader.SetGlobalFloat(_OzoneLayerEnd, R + ozoS + ozoW);

            material.SetVector(_GroundAlbedo_PlanetRadius, new Vector4(pbrSky.groundTint.value.r, pbrSky.groundTint.value.g, pbrSky.groundTint.value.b, R));
            Shader.SetGlobalFloat(_AlphaSaturation, pbrSky.alphaSaturation.value);

            Shader.SetGlobalFloat(_AlphaMultiplier, pbrSky.alphaMultiplier.value);

            Shader.SetGlobalVector(_HorizonTint, new Vector3(pbrSky.horizonTint.value.r, pbrSky.horizonTint.value.g, pbrSky.horizonTint.value.b));
            Shader.SetGlobalFloat(_HorizonZenithShiftPower, expParams.x);

            Shader.SetGlobalVector(_ZenithTint, new Vector3(pbrSky.zenithTint.value.r, pbrSky.zenithTint.value.g, pbrSky.zenithTint.value.b));
            Shader.SetGlobalFloat(_HorizonZenithShiftScale, expParams.y);

            var cameraPos = camera.transform.position;
            Vector3 planetCenter = planetCenterRadius.xyz;
            var planetPosRWS = planetCenter - cameraPos;

            // This is not very efficient but necessary for precision
            var planetUp = -planetPosRWS.normalized;
            var cameraHeight = Vector3.Dot(cameraPos - (planetUp * R + planetCenter), planetUp);
            float4 upAltitude = float4(planetUp, cameraHeight);
            Vector3 cameraPosPS = cameraPos - planetCenter;
            if (upAltitude.w < 1.0f) // Ensure camera is not below the ground
                cameraPosPS -= (upAltitude.w - 1.0f) * (Vector3)upAltitude.xyz;

            Shader.SetGlobalVector(_PBRSkyCameraPosPS, cameraPosPS);

            Shader.SetGlobalVector(_PlanetCenterRadius, planetCenterRadius);
            Shader.SetGlobalVector(_PlanetUpAltitude, upAltitude);

            var renderingSpace = visualEnvironment.renderingSpace.value;
            CoreUtils.SetKeyword(material, LOCAL_SKY, renderingSpace == VisualEnvironment.RenderingSpace.World);

            // Precomputation is done, shading is next.
            Quaternion planetRotation = Quaternion.Euler(pbrSky.planetRotation.value.x,
                pbrSky.planetRotation.value.y,
                pbrSky.planetRotation.value.z);

            Quaternion spaceRotation = Quaternion.Euler(pbrSky.spaceRotation.value.x,
                pbrSky.spaceRotation.value.y,
                pbrSky.spaceRotation.value.z);

            var planetRotationMatrix = Matrix4x4.Rotate(planetRotation);
            planetRotationMatrix[0] *= -1;
            planetRotationMatrix[1] *= -1;
            planetRotationMatrix[2] *= -1;

            material.SetInteger(_HasGroundAlbedoTexture, pbrSky.groundColorTexture.value == null ? 0 : 1);
            material.SetTexture(_GroundAlbedoTexture, pbrSky.groundColorTexture.value);

            material.SetInteger(_HasGroundEmissionTexture, pbrSky.groundEmissionTexture.value == null ? 0 : 1);
            material.SetTexture(_GroundEmissionTexture, pbrSky.groundEmissionTexture.value);
            material.SetFloat(_GroundEmissionMultiplier, pbrSky.groundEmissionMultiplier.value);

            material.SetInteger(_HasSpaceEmissionTexture, pbrSky.spaceEmissionTexture.value == null ? 0 : 1);
            material.SetTexture(_SpaceEmissionTexture, pbrSky.spaceEmissionTexture.value);
            material.SetFloat(_SpaceEmissionMultiplier, pbrSky.spaceEmissionMultiplier.value);

            material.SetMatrix(_PlanetRotation, planetRotationMatrix);
            material.SetMatrix(_SpaceRotation, Matrix4x4.Rotate(spaceRotation));

            if (mainLight != null)
            {
                // Celestial Body Data
                material.SetInt(_CelestialLightCount, 1);
                material.SetInt(_CelestialBodyCount, 1);
                material.SetFloat(_CelestialLightExposure, 1.0f);

                const float distanceFromCamera = 1.5e+11f;
                const float angularDiameter = 0.5f;
                var angularRadius = angularDiameter * 0.5f * Mathf.Deg2Rad;
                var flareSize = Mathf.Max(2.0f * Mathf.Deg2Rad, 5.960464478e-8f);
                var flareCosInner = Mathf.Cos(angularRadius);
                float rcpSolidAngle = 1.0f / (Mathf.PI * 2.0f * (1 - flareCosInner));

            #if URP_PHYSICAL_LIGHT
                var color = mainLight.color.linear * mainLight.intensity;

                bool isPhysicalLight = mainLight.GetComponent<AdditionalLightData>() != null;
                color = isPhysicalLight ? color : color * PI;
            #else
                var color = mainLight.color.linear * mainLight.intensity * PI;
            #endif

                color = mainLight.useColorTemperature ? color * Mathf.CorrelatedColorTemperatureToRGB(mainLight.colorTemperature) : color;
                var surfaceColor = Vector4.one;
                var flareColor = Vector4.one;

                surfaceColor *= rcpSolidAngle;
                flareColor *= rcpSolidAngle;

                celestialBodyData.color = float3(color.r, color.g, color.b);

                const float lightingUnitsMultiplier = 50.0f;
                color *= rcp(lightingUnitsMultiplier); // avoid potential precision issues

                surfaceColor = Vector4.Scale(color, surfaceColor);
                flareColor = Vector4.Scale(color, flareColor);

                celestialBodyData.forward = mainLight.transform.forward;
                celestialBodyData.distanceFromCamera = distanceFromCamera;
                celestialBodyData.right = mainLight.transform.right.normalized;
                celestialBodyData.angularRadius = angularRadius;
                celestialBodyData.radius = Mathf.Tan(angularRadius) * distanceFromCamera;
                celestialBodyData.up = mainLight.transform.up.normalized;
                celestialBodyData.type = 0; // sun
                celestialBodyData.surfaceColor = surfaceColor;
                celestialBodyData.earthshine = 1.0f * 0.01f;  // earth reflects about 0.01% of sun light
                celestialBodyData.surfaceTextureScaleOffset = Vector4.zero;
                celestialBodyData.sunDirection = mainLight != null ? mainLight.transform.forward : Vector3.forward;

                // Flare
                celestialBodyData.flareSize = flareSize;
                celestialBodyData.flareFalloff = 4.0f;

                celestialBodyData.flareCosInner = flareCosInner;
                celestialBodyData.flareCosOuter = Mathf.Cos(angularRadius + flareSize);

                celestialBodyData.flareColor = flareColor;

                Shader.SetGlobalVector(_CelestialBody_Color, celestialBodyData.color);
                Shader.SetGlobalVector(_CelestialBody_Forward, celestialBodyData.forward);
                material.SetFloat(_CelestialBody_DistanceFromCamera, celestialBodyData.distanceFromCamera);
                material.SetVector(_CelestialBody_Right, celestialBodyData.right);
                material.SetFloat(_CelestialBody_AngularRadius, celestialBodyData.angularRadius);
                material.SetFloat(_CelestialBody_Radius, celestialBodyData.radius);
                material.SetVector(_CelestialBody_Up, celestialBodyData.up);
                material.SetInt(_CelestialBody_Type, celestialBodyData.type);
                material.SetVector(_CelestialBody_SurfaceColor, celestialBodyData.surfaceColor);
                material.SetFloat(_CelestialBody_Earthshine, celestialBodyData.earthshine);
                material.SetVector(_CelestialBody_SurfaceTextureScaleOffset, celestialBodyData.surfaceTextureScaleOffset);
                material.SetVector(_CelestialBody_SunDirection, celestialBodyData.sunDirection);
                material.SetFloat(_CelestialBody_FlareCosInner, celestialBodyData.flareCosInner);
                material.SetFloat(_CelestialBody_FlareCosOuter, celestialBodyData.flareCosOuter);
                material.SetFloat(_CelestialBody_FlareSize, celestialBodyData.flareSize);
                material.SetVector(_CelestialBody_FlareColor, celestialBodyData.flareColor);
                material.SetFloat(_CelestialBody_FlareFalloff, celestialBodyData.flareFalloff);
            }
        }

        static float CornetteShanksPhasePartConstant(float anisotropy)
        {
            float g = anisotropy;

            return (3.0f / (8.0f * Mathf.PI)) * (1.0f - g * g) / (2.0f + g * g);
        }

        static float2 ComputeExponentialInterpolationParams(float k)
        {
            if (k == 0) k = 1e-6f; // Avoid the numerical explosion around 0

            // Remap t: (exp(10 k t) - 1) / (exp(10 k) - 1) = exp(x t) y - y.
            float x = 10 * k;
            float y = 1 / (exp(x) - 1);

            return float2(x, y);
        }

        static float3 TransmittanceFromOpticalDepth(float3 opticalDepth)
        {
            return exp(-opticalDepth);
        }

        static float Avg3(float a, float b, float c)
        {
            return (a + b + c) * 0.33333333f;
        }

        static float3 Desaturate(float3 value, float3 saturation)
        {
            // Saturation = Colorfulness / Brightness.
            // https://munsell.com/color-blog/difference-chroma-saturation/
            float mean = Avg3(value.x, value.y, value.z);
            float3 dev = value - mean;

            return mean + dev * saturation;
        }

        float3 EvaluateSunColorAttenuation(float3 positionPS, float3 sunDirection, bool estimatePenumbra = false)
        {
            return EvaluateSunColorAttenuation(pbrSky, visualEnvironment, positionPS, sunDirection, estimatePenumbra);
        }

        static float3 EvaluateSunColorAttenuation(PhysicallyBasedSky pbrSky, VisualEnvironment visualEnvironment, float3 positionPS, float3 sunDirection, bool estimatePenumbra = false)
        {
            float r = length(positionPS);
            float cosTheta = dot(positionPS, sunDirection) * rcp(r); // Normalize

            // Point can be below horizon due to precision issues
            float R = visualEnvironment.GetPlanetRadius();
            r = max(r, R);
            float cosHoriz = PhysicallyBasedSky.ComputeCosineOfHorizonAngle(r, R);

            if (cosTheta >= cosHoriz) // Above horizon
            {
                float3 oDepth = PhysicallyBasedSky.ComputeAtmosphericOpticalDepth(
                    pbrSky.GetAirScaleHeight(), pbrSky.GetAerosolScaleHeight(), pbrSky.GetAirExtinctionCoefficient(), pbrSky.GetAerosolExtinctionCoefficient(),
                    pbrSky.GetOzoneLayerMinimumAltitude(), pbrSky.GetOzoneLayerWidth(), pbrSky.GetOzoneExtinctionCoefficient(),
                    R, r, cosTheta, true);
                float3 opacity = 1 - TransmittanceFromOpticalDepth(oDepth);
                float penumbra = saturate((cosTheta - cosHoriz) / 0.0019f); // very scientific value
                float3 attenuation = 1 - (Desaturate(opacity, pbrSky.alphaSaturation.value) * pbrSky.alphaMultiplier.value);
                return estimatePenumbra ? attenuation * penumbra : attenuation;
            }
            else
            {
                return 0;
            }
        }
        #endregion
    }

    /// <summary>
    /// This pass updates the precomputation data for physically based sky.
    /// </summary>
    private class SkyViewLUTPass : ScriptableRenderPass
    {
        private const string profilerTag = "Precompute Physically Based Sky";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(profilerTag);

        public PhysicallyBasedSky pbrSky;
        public VisualEnvironment visualEnvironment;

        public CelestialBodyData celestialBodyData;

        // Store the hash of the parameters each time precomputation is done.
        // If the hash does not match, we must recompute our data.
        private int m_LastPrecomputationParamHash;
        //private int m_LastCelestialBodyDataHash;
        private int m_LastLutDataHash;

        public Material lutMaterial;
        public bool halfResolutionLuts;
        private RTHandle multiScatteringLUTHandle;
        private RTHandle skyViewLUTHandle;
        private RTHandle airSingleScatteringHandle;     // Air SS
        private RTHandle aerosolSingleScatteringHandle; // Aerosol SS
        private RTHandle multipleScatteringHandle;      // Atmosphere MS
        private RTHandle groundIrradianceHandle;
        //private RTHandle atmosphericScatteringLUTHandle;

        private const string _MultiScatteringLUT = "_MultiScatteringLUT";
        private const string _SkyViewLUT = "_SkyViewLUT";
        private const string _AirSingleScatteringTexture = "_InScatteredRadianceTable0";
        private const string _AerosolSingleScatteringTexture = "_InScatteredRadianceTable1";
        private const string _MultipleScatteringTexture = "_InScatteredRadianceTable2";
        private const string _GroundIrradianceTexture = "_GroundIrradianceTable";
        //private const string _AtmosphericScatteringLUT = "_AtmosphericScatteringLUT";

        private const string STEREO_INSTANCING_ON = "STEREO_INSTANCING_ON";

        // Match the texture naming in HDRP
        private static readonly int airSingleScatteringTexture = Shader.PropertyToID("_AirSingleScatteringTexture");
        private static readonly int aerosolSingleScatteringTexture = Shader.PropertyToID("_AerosolSingleScatteringTexture");
        private static readonly int multipleScatteringTexture = Shader.PropertyToID("_MultipleScatteringTexture");
        private static readonly int groundIrradianceTexture = Shader.PropertyToID("_GroundIrradianceTexture");
        //private static readonly int atmosphericScatteringLUT = Shader.PropertyToID(_AtmosphericScatteringLUT);
        private static readonly int multiScatteringLUT = Shader.PropertyToID(_MultiScatteringLUT);
        private static readonly int skyViewLUT = Shader.PropertyToID(_SkyViewLUT);


        private static readonly int PBSky_TableCoord_Z = Shader.PropertyToID("PBSky_TableCoord_Z");

        public const int k_GroundIrradianceTableSize = 256;
        public const int k_InScatteredRadianceTableSizeX = 128; // <N, V>
        public const int k_InScatteredRadianceTableSizeY = 32;  // height
        public const int k_InScatteredRadianceTableSizeZ = 16;  // AzimuthAngle(L) w.r.t. the view vector
        public const int k_InScatteredRadianceTableSizeW = 64;  // <N, L>,

        public const int k_MultiScatteringLutWidth = 32;
        public const int k_MultiScatteringLutHeight = 32;

        public const int k_SkyViewLutWidth = 256;
        public const int k_SkyViewLutHeight = 144;

        public const int k_AtmosphericScatteringLutWidth = 32;
        public const int k_AtmosphericScatteringLutHeight = 32;
        public const int k_AtmosphericScatteringLutDepth = 64;

        private readonly RenderTargetIdentifier[] lutHandles = new RenderTargetIdentifier[3];
        //private readonly RenderTargetIdentifier[] sliceHandles = new RenderTargetIdentifier[2];

        private static readonly Vector4 m_ScaleBias = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);

        public SkyViewLUTPass(Material material, ref CelestialBodyData celestialBodyData)
        {
            lutMaterial = material;
            this.celestialBodyData = celestialBodyData;
        }

        #region Non Render Graph Pass
    #if !UNITY_6000_4_OR_NEWER
        bool lutDataChanged;
    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;
            desc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
            desc.dimension = TextureDimension.Tex2D;
            desc.useDynamicScale = false;

            desc.width = k_MultiScatteringLutWidth;
            desc.height = k_MultiScatteringLutHeight;
        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref multiScatteringLUTHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _MultiScatteringLUT);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref multiScatteringLUTHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _MultiScatteringLUT);
        #endif

            desc.width = k_SkyViewLutWidth;
            desc.height = k_SkyViewLutHeight;
        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref skyViewLUTHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _SkyViewLUT);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref skyViewLUTHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _SkyViewLUT);
        #endif

            desc.width = k_GroundIrradianceTableSize;
            desc.height = 1;
        #if UNITY_6000_0_OR_NEWER
            lutDataChanged = RenderingUtils.ReAllocateHandleIfNeeded(ref groundIrradianceHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _GroundIrradianceTexture);
        #else
            lutDataChanged = RenderingUtils.ReAllocateIfNeeded(ref groundIrradianceHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _GroundIrradianceTexture);
        #endif

            // Switched Y and Z dimension to reduce draw calls.
            desc.memoryless = RenderTextureMemoryless.None;
            desc.dimension = TextureDimension.Tex3D;
            desc.width = halfResolutionLuts ? k_InScatteredRadianceTableSizeX / 2 : k_InScatteredRadianceTableSizeX;
            desc.height = halfResolutionLuts ? (k_InScatteredRadianceTableSizeZ * k_InScatteredRadianceTableSizeW) / 2 : k_InScatteredRadianceTableSizeZ * k_InScatteredRadianceTableSizeW;
            desc.volumeDepth = halfResolutionLuts ? k_InScatteredRadianceTableSizeY / 2 : k_InScatteredRadianceTableSizeY;
        #if UNITY_6000_0_OR_NEWER
            lutDataChanged |= RenderingUtils.ReAllocateHandleIfNeeded(ref airSingleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _AirSingleScatteringTexture);
            lutDataChanged |= RenderingUtils.ReAllocateHandleIfNeeded(ref aerosolSingleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _AerosolSingleScatteringTexture);
            lutDataChanged |= RenderingUtils.ReAllocateHandleIfNeeded(ref multipleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _MultipleScatteringTexture);
        #else
            lutDataChanged |= RenderingUtils.ReAllocateIfNeeded(ref airSingleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _AirSingleScatteringTexture);
            lutDataChanged |= RenderingUtils.ReAllocateIfNeeded(ref aerosolSingleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _AerosolSingleScatteringTexture);
            lutDataChanged |= RenderingUtils.ReAllocateIfNeeded(ref multipleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _MultipleScatteringTexture);
        #endif

            // Unused
            /*
            desc.width = k_AtmosphericScatteringLutWidth;
            desc.height = k_AtmosphericScatteringLutHeight;
            desc.volumeDepth = k_AtmosphericScatteringLutDepth;
        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref atmosphericScatteringLUTHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _AtmosphericScatteringLUT);
            RenderingUtils.ReAllocateHandleIfNeeded(ref skyTransmittanceHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SkyTransmittance");
        #else
            RenderingUtils.ReAllocateIfNeeded(ref atmosphericScatteringLUTHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _AtmosphericScatteringLUT);
            RenderingUtils.ReAllocateIfNeeded(ref skyTransmittanceHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SkyTransmittance");
        #endif

            desc.dimension = TextureDimension.Tex2D;
            desc.volumeDepth = 1;
        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref atmosphericScatteringSliceHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_AtmosphericScatteringSlice");
            RenderingUtils.ReAllocateHandleIfNeeded(ref skyTransmittanceSliceHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SkyTransmittanceSlice");
        #else
            RenderingUtils.ReAllocateIfNeeded(ref atmosphericScatteringSliceHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_AtmosphericScatteringSlice");
            RenderingUtils.ReAllocateIfNeeded(ref skyTransmittanceSliceHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SkyTransmittanceSlice");
        #endif
            */

            lutDataChanged |= HasLutDataChanged();
            m_LastPrecomputationParamHash = lutDataChanged ? 0 : m_LastPrecomputationParamHash;
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            bool precomputationChanged = HasPrecomputationDataChanged() || lutDataChanged;
            //bool celestialBodyDataChanged = HasCelestialBodyDataChanged() || lutDataChanged;

            bool cameraSpaceSky = visualEnvironment.renderingSpace.value == VisualEnvironment.RenderingSpace.Camera;
            bool isStereoEnabled = renderingData.cameraData.camera.stereoEnabled;

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                if (isStereoEnabled)
                    cmd.DisableShaderKeyword(STEREO_INSTANCING_ON);

                if (precomputationChanged)
                {
                    Blitter.BlitCameraTexture(cmd, multiScatteringLUTHandle, multiScatteringLUTHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, lutMaterial, pass: 1);
                }

                lutMaterial.SetTexture(multiScatteringLUT, multiScatteringLUTHandle);

                if (cameraSpaceSky)
                {
                    Blitter.BlitCameraTexture(cmd, skyViewLUTHandle, skyViewLUTHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, lutMaterial, pass: 0);
                }

                cmd.SetGlobalTexture(skyViewLUT, skyViewLUTHandle);

                if (precomputationChanged)
                {
                    // InScattered Radiance LUTs
                    lutHandles[0] = airSingleScatteringHandle;
                    lutHandles[1] = aerosolSingleScatteringHandle;
                    lutHandles[2] = multipleScatteringHandle;

                    int slices = halfResolutionLuts ? k_InScatteredRadianceTableSizeY / 2 : k_InScatteredRadianceTableSizeY;
                    for (int slice = 0; slice < slices; ++slice)
                    {
                        cmd.SetGlobalInteger(PBSky_TableCoord_Z, slice);
                        cmd.SetRenderTarget(lutHandles, airSingleScatteringHandle, 0, CubemapFace.Unknown, slice);

                        Blitter.BlitTexture(cmd, airSingleScatteringHandle, m_ScaleBias, lutMaterial, pass: 2);
                    }

                    if (!cameraSpaceSky)
                    {
                        Blitter.BlitCameraTexture(cmd, groundIrradianceHandle, groundIrradianceHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, lutMaterial, pass: 3);
                    }
                }

                // Unused
                /*
                if (precomputedAtmosphericScattering)
                {
                    for (int slice = 0; slice < k_AtmosphericScatteringLutDepth; ++slice)
                    {
                        sliceHandles[0] = atmosphericScatteringLUTHandle;
                        sliceHandles[1] = skyTransmittanceHandle;

                        cmd.SetGlobalInteger(PBSky_TableCoord_Z, slice);
                        cmd.SetRenderTarget(sliceHandles, atmosphericScatteringLUTHandle, 0, CubemapFace.Unknown, slice);

                        Blitter.BlitTexture(cmd, atmosphericScatteringLUTHandle, m_ScaleBias, lutMaterial, pass: 5);

                        cmd.CopyTexture(atmosphericScatteringLUTHandle, slice, 0, 0, 0, k_AtmosphericScatteringLutWidth, k_AtmosphericScatteringLutHeight, atmosphericScatteringSliceHandle, 0, 0, 0, 0);
                        cmd.CopyTexture(skyTransmittanceHandle, slice, 0, 0, 0, k_AtmosphericScatteringLutWidth, k_AtmosphericScatteringLutHeight, skyTransmittanceSliceHandle, 0, 0, 0, 0);
                    }
                }

                lutMaterial.SetTexture("_AtmosphericScatteringSlice", atmosphericScatteringSliceHandle);
                lutMaterial.SetTexture("_SkyTransmittanceSlice", skyTransmittanceSliceHandle);

                cmd.SetGlobalTexture(atmosphericScatteringLUT, atmosphericScatteringLUTHandle);
                */

                cmd.SetGlobalTexture(airSingleScatteringTexture, airSingleScatteringHandle);
                cmd.SetGlobalTexture(aerosolSingleScatteringTexture, aerosolSingleScatteringHandle);
                cmd.SetGlobalTexture(multipleScatteringTexture, multipleScatteringHandle);
                cmd.SetGlobalTexture(groundIrradianceTexture, groundIrradianceHandle);

                cmd.SetRenderTarget(renderingData.cameraData.renderer.cameraColorTargetHandle, renderingData.cameraData.renderer.cameraDepthTargetHandle);

                if (isStereoEnabled)
                    cmd.EnableShaderKeyword(STEREO_INSTANCING_ON);
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            
            CommandBufferPool.Release(cmd);
        }
    #endif
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        private class PassData
        {
            internal Material lutMaterial;

            internal TextureHandle multiScatteringLUTHandle;
            internal TextureHandle skyViewLUTHandle;

            internal TextureHandle airSingleScatteringHandle;
            internal TextureHandle aerosolSingleScatteringHandle;
            internal TextureHandle multipleScatteringHandle;

            internal TextureHandle groundIrradianceHandle;
            //internal TextureHandle atmosphericScatteringLUTHandle;
            //internal TextureHandle skyTransmittanceHandle;
            //internal TextureHandle atmosphericScatteringSliceHandle;
            //internal TextureHandle skyTransmittanceSliceHandle;

            internal RenderTargetIdentifier[] lutHandles;
            //internal RenderTargetIdentifier[] sliceHandles;

            internal bool cameraSpaceSky;
            internal bool precomputedAtmosphericScattering;
            internal bool halfResolutionLuts;

            internal bool precomputationChanged;
            //internal bool celestialBodyDataChanged;

            internal bool isStereoEnabled;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            if (data.isStereoEnabled)
                cmd.DisableShaderKeyword(STEREO_INSTANCING_ON);

            if (data.precomputationChanged)
            {
                Blitter.BlitCameraTexture(cmd, data.multiScatteringLUTHandle, data.multiScatteringLUTHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, data.lutMaterial, pass: 1);
            }

            data.lutMaterial.SetTexture(multiScatteringLUT, data.multiScatteringLUTHandle);
            
            if (data.cameraSpaceSky)
            {
                Blitter.BlitCameraTexture(cmd, data.skyViewLUTHandle, data.skyViewLUTHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, data.lutMaterial, pass: 0);
            }

            cmd.SetGlobalTexture(skyViewLUT, data.skyViewLUTHandle);

            if (data.precomputationChanged)
            {
                // InScattered Radiance LUTs
                data.lutHandles[0] = data.airSingleScatteringHandle;
                data.lutHandles[1] = data.aerosolSingleScatteringHandle;
                data.lutHandles[2] = data.multipleScatteringHandle;

                int slices = data.halfResolutionLuts ? k_InScatteredRadianceTableSizeY / 2 : k_InScatteredRadianceTableSizeY;
                for (int slice = 0; slice < slices; ++slice)
                {
                    cmd.SetGlobalInteger(PBSky_TableCoord_Z, slice);
                    cmd.SetRenderTarget(data.lutHandles, data.airSingleScatteringHandle, 0, CubemapFace.Unknown, slice);

                    Blitter.BlitTexture(cmd, data.airSingleScatteringHandle, m_ScaleBias, data.lutMaterial, pass: 2);
                }

                if (!data.cameraSpaceSky)
                {
                    Blitter.BlitCameraTexture(cmd, data.groundIrradianceHandle, data.groundIrradianceHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, data.lutMaterial, pass: 3);
                }
            }

            // Unused
            /*
            if (data.precomputedAtmosphericScattering)
            {
                for (int slice = 0; slice < k_AtmosphericScatteringLutDepth; ++slice)
                {
                    data.sliceHandles[0] = data.atmosphericScatteringLUTHandle;
                    data.sliceHandles[1] = data.skyTransmittanceHandle;

                    cmd.SetGlobalInteger(PBSky_TableCoord_Z, slice);
                    cmd.SetRenderTarget(data.sliceHandles, data.atmosphericScatteringLUTHandle, 0, CubemapFace.Unknown, slice);

                    Blitter.BlitTexture(cmd, data.atmosphericScatteringLUTHandle, m_ScaleBias, data.lutMaterial, pass: 5);

                    cmd.CopyTexture(data.atmosphericScatteringLUTHandle, slice, 0, 0, 0, k_AtmosphericScatteringLutWidth, k_AtmosphericScatteringLutHeight, data.atmosphericScatteringSliceHandle, 0, 0, 0, 0);
                    cmd.CopyTexture(data.skyTransmittanceHandle, slice, 0, 0, 0, k_AtmosphericScatteringLutWidth, k_AtmosphericScatteringLutHeight, data.skyTransmittanceSliceHandle, 0, 0, 0, 0);
                }
            }

            data.lutMaterial.SetTexture("_AtmosphericScatteringSlice", data.atmosphericScatteringSliceHandle);
            data.lutMaterial.SetTexture("_SkyTransmittanceSlice", data.skyTransmittanceSliceHandle);

            cmd.SetGlobalTexture(atmosphericScatteringLUT, data.atmosphericScatteringLUTHandle);
            */

            cmd.SetGlobalTexture(airSingleScatteringTexture, data.airSingleScatteringHandle);
            cmd.SetGlobalTexture(aerosolSingleScatteringTexture, data.aerosolSingleScatteringHandle);
            cmd.SetGlobalTexture(multipleScatteringTexture, data.multipleScatteringHandle);
            cmd.SetGlobalTexture(groundIrradianceTexture, data.groundIrradianceHandle);

            if (data.isStereoEnabled)
                cmd.EnableShaderKeyword(STEREO_INSTANCING_ON);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(profilerTag, out var passData))
            {
                // UniversalResourceData contains all the texture handles used by the renderer, including the active color and depth textures
                // The active color and depth textures are the main color and depth buffers that the camera renders into
                UniversalRenderingData universalRenderingData = frameData.Get<UniversalRenderingData>();
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                bool precomputationChanged = HasPrecomputationDataChanged();
                //bool celestialBodyDataChanged = HasCelestialBodyDataChanged();
                bool lutDataChanged;

                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.useMipMap = false;
                desc.autoGenerateMips = false;
                desc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                desc.dimension = TextureDimension.Tex2D;
                desc.useDynamicScale = false;

                desc.width = k_MultiScatteringLutWidth;
                desc.height = k_MultiScatteringLutHeight;
                RenderingUtils.ReAllocateHandleIfNeeded(ref multiScatteringLUTHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _MultiScatteringLUT);
                TextureHandle multiScatteringLUTTextureHandle = renderGraph.ImportTexture(multiScatteringLUTHandle);

                desc.width = k_SkyViewLutWidth;
                desc.height = k_SkyViewLutHeight;
                RenderingUtils.ReAllocateHandleIfNeeded(ref skyViewLUTHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _SkyViewLUT);
                TextureHandle skyViewLUTTextureHandle = renderGraph.ImportTexture(skyViewLUTHandle);

                desc.width = k_GroundIrradianceTableSize;
                desc.height = 1;
                lutDataChanged = RenderingUtils.ReAllocateHandleIfNeeded(ref groundIrradianceHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _GroundIrradianceTexture);
                TextureHandle groundIrradianceTextureHandle = renderGraph.ImportTexture(groundIrradianceHandle);

                // Switched Y and Z dimension to reduce draw calls.
                desc.dimension = TextureDimension.Tex3D;
                desc.width = halfResolutionLuts ? k_InScatteredRadianceTableSizeX / 2 : k_InScatteredRadianceTableSizeX;
                desc.height = halfResolutionLuts ? (k_InScatteredRadianceTableSizeZ * k_InScatteredRadianceTableSizeW) / 2 : k_InScatteredRadianceTableSizeZ * k_InScatteredRadianceTableSizeW;
                desc.volumeDepth = halfResolutionLuts ? k_InScatteredRadianceTableSizeY / 2 : k_InScatteredRadianceTableSizeY;
                lutDataChanged |= RenderingUtils.ReAllocateHandleIfNeeded(ref airSingleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _AirSingleScatteringTexture);
                TextureHandle airSingleScatteringTextureHandle = renderGraph.ImportTexture(airSingleScatteringHandle);

                lutDataChanged |= RenderingUtils.ReAllocateHandleIfNeeded(ref aerosolSingleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _AerosolSingleScatteringTexture);
                TextureHandle aerosolSingleScatteringTextureHandle = renderGraph.ImportTexture(aerosolSingleScatteringHandle);

                lutDataChanged |= RenderingUtils.ReAllocateHandleIfNeeded(ref multipleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _MultipleScatteringTexture);
                TextureHandle multipleScatteringTextureHandle = renderGraph.ImportTexture(multipleScatteringHandle);

                // Unused
                /*
                desc.width = k_AtmosphericScatteringLutWidth;
                desc.height = k_AtmosphericScatteringLutHeight;
                desc.volumeDepth = k_AtmosphericScatteringLutDepth;
                RenderingUtils.ReAllocateHandleIfNeeded(ref atmosphericScatteringLUTHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _AtmosphericScatteringLUT);
                TextureHandle atmosphericScatteringLUTTextureHandle = renderGraph.ImportTexture(atmosphericScatteringLUTHandle);
                TextureHandle skyTransmittanceHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, name: "_SkyTransmittance", false, FilterMode.Point, TextureWrapMode.Clamp);

                desc.dimension = TextureDimension.Tex2D;
                desc.volumeDepth = 1;
                TextureHandle atmosphericScatteringSliceHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, name: "_AtmosphericScatteringSlice", false, FilterMode.Point, TextureWrapMode.Clamp);
                TextureHandle skyTransmittanceSliceHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, name: "_SkyTransmittanceSlice", false, FilterMode.Point, TextureWrapMode.Clamp);
                */

                lutDataChanged |= HasLutDataChanged();
                m_LastPrecomputationParamHash = lutDataChanged ? 0 : m_LastPrecomputationParamHash;

                passData.lutHandles = lutHandles;
                //passData.sliceHandles = sliceHandles;
                passData.multiScatteringLUTHandle = multiScatteringLUTTextureHandle;
                passData.skyViewLUTHandle = skyViewLUTTextureHandle;
                passData.airSingleScatteringHandle = airSingleScatteringTextureHandle;
                passData.aerosolSingleScatteringHandle = aerosolSingleScatteringTextureHandle;
                passData.multipleScatteringHandle = multipleScatteringTextureHandle;
                passData.groundIrradianceHandle = groundIrradianceTextureHandle;

                // Unused
                /*
                passData.atmosphericScatteringLUTHandle = atmosphericScatteringLUTTextureHandle;
                passData.skyTransmittanceHandle = skyTransmittanceHandle;
                passData.atmosphericScatteringSliceHandle = atmosphericScatteringSliceHandle;
                passData.skyTransmittanceSliceHandle = skyTransmittanceSliceHandle;

                builder.UseTexture(passData.atmosphericScatteringLUTHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.atmosphericScatteringSliceHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.skyTransmittanceHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.skyTransmittanceSliceHandle, AccessFlags.ReadWrite);
                */

                passData.cameraSpaceSky = visualEnvironment.renderingSpace.value == VisualEnvironment.RenderingSpace.Camera;
                passData.precomputedAtmosphericScattering = pbrSky.atmosphericScattering.value;
                passData.halfResolutionLuts = halfResolutionLuts;
                passData.precomputationChanged = precomputationChanged || lutDataChanged;
                //passData.celestialBodyDataChanged = celestialBodyDataChanged || lutDataChanged;
                passData.isStereoEnabled = cameraData.camera.stereoEnabled;
                passData.lutMaterial = lutMaterial;

                // UnsafePasses don't setup the outputs using UseTextureFragment/UseTextureFragmentDepth, you should specify your writes with UseTexture instead
                builder.UseTexture(passData.multiScatteringLUTHandle, AccessFlags.ReadWrite);
                builder.UseTexture(passData.skyViewLUTHandle, AccessFlags.ReadWrite);

                builder.AllowGlobalStateModification(true);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {
            m_LastPrecomputationParamHash = 0;
            //m_LastCelestialBodyDataHash = 0;
            m_LastLutDataHash = 0;

            multiScatteringLUTHandle?.Release();
            skyViewLUTHandle?.Release();
            airSingleScatteringHandle?.Release();
            aerosolSingleScatteringHandle?.Release();
            multipleScatteringHandle?.Release();
            groundIrradianceHandle?.Release();
            //atmosphericScatteringLUTHandle?.Release();
        }

        /*
        // Computes hash code of light parameters used during sky view lut precomputation
        int GetLightsHash()
        {
            int hash = 13;
            //for (int i = 0; i < s_CelestialLightCount; i++)
            {
                //ref var data = ref celestialBodyData;
                hash = hash * 23 + celestialBodyData.forward.GetHashCode();
                hash = hash * 23 + celestialBodyData.color.GetHashCode();
            }
            return hash;
        }
        */

        // Computes hash code of LUT RTHandles used during sky view lut precomputation
        int GetLutDataHash()
        {
            int hash = 13;
            hash = hash * 23 + airSingleScatteringHandle.GetHashCode();
            hash = hash * 23 + aerosolSingleScatteringHandle.GetHashCode();
            hash = hash * 23 + multipleScatteringHandle.GetHashCode();
            hash = hash * 23 + groundIrradianceHandle.GetHashCode();
            return hash;
        }

        bool HasPrecomputationDataChanged()
        {
            int currPrecomputationParamHash = pbrSky.GetPrecomputationHashCode();
            // Calculate the parameter hash in the Visual Environment override.
            currPrecomputationParamHash = currPrecomputationParamHash * 23 + visualEnvironment.planetRadius.GetHashCode();
            currPrecomputationParamHash = currPrecomputationParamHash * 23 + visualEnvironment.renderingSpace.GetHashCode();
            currPrecomputationParamHash += halfResolutionLuts.GetHashCode();
            if (currPrecomputationParamHash != m_LastPrecomputationParamHash || m_LastPrecomputationParamHash == 0)
            {
                m_LastPrecomputationParamHash = currPrecomputationParamHash;
                return true;
            }
            return false;
        }

        /*
        bool HasCelestialBodyDataChanged()
        {
            int currCelestialBodyDataHash = GetLightsHash();
            if (currCelestialBodyDataHash != m_LastCelestialBodyDataHash || m_LastCelestialBodyDataHash == 0)
            {
                m_LastCelestialBodyDataHash = currCelestialBodyDataHash;
                return true;
            }
            return false;
        }
        */

        bool HasLutDataChanged()
        {
            int currLutDataHash = GetLutDataHash();
            if (currLutDataHash != m_LastLutDataHash || m_LastLutDataHash == 0)
            {
                m_LastLutDataHash = currLutDataHash;
                return true;
            }
            return false;
        }
        #endregion
    }

    /// <summary>
    /// This pass computes atmospheric scattering (PBSky only) or height-based fog.
    /// </summary>
    private class AtmosphericScatteringPass : ScriptableRenderPass
    {
        private const string profilerTag = "Opaque Atmospheric Scattering";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(profilerTag);

        public PhysicallyBasedSky pbrSky;
        public VisualEnvironment visualEnvironment;
        public Fog fog;

        public Material lutMaterial;

        private static readonly int _FogEnabled = Shader.PropertyToID("_FogEnabled");
        private static readonly int _MaxFogDistance = Shader.PropertyToID("_MaxFogDistance");
        private static readonly int _FogColor = Shader.PropertyToID("_FogColor");
        private static readonly int _FogColorMode = Shader.PropertyToID("_FogColorMode");
        private static readonly int _MipFogParameters = Shader.PropertyToID("_MipFogParameters");
        private static readonly int _HeightFogBaseScattering = Shader.PropertyToID("_HeightFogBaseScattering");
        private static readonly int _HeightFogBaseExtinction = Shader.PropertyToID("_HeightFogBaseExtinction");
        private static readonly int _HeightFogBaseHeight = Shader.PropertyToID("_HeightFogBaseHeight");
        private static readonly int _HeightFogExponents = Shader.PropertyToID("_HeightFogExponents");
        private static readonly int _PlanetUpAltitude = Shader.PropertyToID("_PlanetUpAltitude");
        private static readonly int _UnderWaterEnabled = Shader.PropertyToID("_UnderWaterEnabled");
        private static readonly int _FogWaterHeight = Shader.PropertyToID("_FogWaterHeight");

        // "_ScreenSize" that supports dynamic resolution
        private static readonly int _ScreenResolution = Shader.PropertyToID("_ScreenResolution");

        public AtmosphericScatteringPass(Material lutMaterial)
        {
            this.lutMaterial = lutMaterial;
        }

        #region Non Render Graph Pass
    #if !UNITY_6000_4_OR_NEWER
    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            bool isFogEnabled = fog != null && fog.IsActive();
            if (isFogEnabled)
                UpdateFogProperties(renderingData.cameraData.camera);

            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                bool isFogEnabled = fog != null && fog.IsActive();
                cmd.SetGlobalInteger(_FogEnabled, isFogEnabled ? 1 : 0);

                var cameraColorHandle = renderingData.cameraData.renderer.cameraColorTargetHandle;

                if (cameraColorHandle != null)
                {
                    CalculateActualScreenResolution(cmd, cameraColorHandle);

                    Blitter.BlitCameraTexture(cmd, cameraColorHandle, cameraColorHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, lutMaterial, pass: 4);
                }
                    
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            CommandBufferPool.Release(cmd);
        }
    #endif
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        private class PassData
        {
            internal Material lutMaterial;

            internal TextureHandle cameraColorHandle;
            internal bool enableFog;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            if (data.cameraColorHandle.IsValid())
                CalculateActualScreenResolution(cmd, data.cameraColorHandle);

            cmd.SetGlobalInteger(_FogEnabled, data.enableFog ? 1 : 0);

            Blitter.BlitCameraTexture(cmd, data.cameraColorHandle, data.cameraColorHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, data.lutMaterial, pass: 4);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(profilerTag, out var passData))
            {
                // UniversalResourceData contains all the texture handles used by the renderer, including the active color and depth textures
                // The active color and depth textures are the main color and depth buffers that the camera renders into
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                bool isFogEnabled = fog != null && fog.IsActive();
                if (isFogEnabled)
                    UpdateFogProperties(cameraData.camera);

                passData.lutMaterial = lutMaterial;
                passData.cameraColorHandle = resourceData.activeColorTexture;
                passData.enableFog = isFogEnabled;

                ConfigureInput(ScriptableRenderPassInput.Depth);

                // UnsafePasses don't setup the outputs using UseTextureFragment/UseTextureFragmentDepth, you should specify your writes with UseTexture instead
                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);

                builder.AllowGlobalStateModification(true);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {

        }

        public void UpdateFogProperties(Camera camera)
        {
            var cameraPos = camera.transform.position;

            float4 planetCenterRadius = visualEnvironment.GetPlanetCenterRadius(cameraPos);
            float R = planetCenterRadius.w;

            Vector3 planetCenter = planetCenterRadius.xyz;
            var planetPosRWS = planetCenter - cameraPos;

            // This is not very efficient but necessary for precision
            var planetUp = -planetPosRWS.normalized;
            var cameraHeight = Vector3.Dot(cameraPos - (planetUp * R + planetCenter), planetUp);
            float4 upAltitude = float4(planetUp, cameraHeight);

            Shader.SetGlobalInteger(_FogEnabled, 1);
            Shader.SetGlobalFloat(_MaxFogDistance, fog.maxFogDistance.value);

            Color fogColor = (fog.colorMode.value == Fog.FogColorMode.ConstantColor) ? fog.color.value : fog.tint.value;
            Shader.SetGlobalFloat(_FogColorMode, (float)fog.colorMode.value);
            Shader.SetGlobalVector(_FogColor, new Color(fogColor.r, fogColor.g, fogColor.b, 0.0f));
            Shader.SetGlobalVector(_MipFogParameters, new Vector4(fog.mipFogNear.value, fog.mipFogFar.value, fog.mipFogMaxMip.value, 0.0f));

            // When volumetric fog is disabled, we don't want its color to affect the heightfog. So we pass neutral values here.
            var extinction = ExtinctionFromMeanFreePath(fog.meanFreePath.value);
            Shader.SetGlobalVector(_HeightFogBaseScattering, Vector4.one * extinction);
            Shader.SetGlobalFloat(_HeightFogBaseExtinction, extinction);

            float crBaseHeight = fog.baseHeight.value;
            Shader.SetGlobalVector(_PlanetUpAltitude, upAltitude);
            Shader.SetGlobalFloat(_HeightFogBaseHeight, crBaseHeight - upAltitude.w);

            float layerDepth = Mathf.Max(0.01f, fog.maximumHeight.value - fog.baseHeight.value);
            float H = ScaleHeightFromLayerDepth(layerDepth);
            Shader.SetGlobalVector(_HeightFogExponents, new Vector2(1.0f / H, H));
            //_GlobalFogAnisotropy = anisotropy.value;

            Shader.SetGlobalFloat(_UnderWaterEnabled, fog.underWater.value ? 1.0f : 0.0f);
            Shader.SetGlobalFloat(_FogWaterHeight, fog.waterHeight.value);
        }

        static float ExtinctionFromMeanFreePath(float meanFreePath)
        {
            return 1.0f / meanFreePath;
        }

        static float ScaleHeightFromLayerDepth(float d)
        {
            // Exp[-d / H] = 0.001
            // -d / H = Log[0.001]
            // H = d / -Log[0.001]
            return d * 0.144765f;
        }

        static void CalculateActualScreenResolution(CommandBuffer cmd, RTHandle cameraTargetHandle)
        {
            float width = cameraTargetHandle.rt.width;
            float height = cameraTargetHandle.rt.height;
            if (cameraTargetHandle.rt.useDynamicScale)
            {
             #if ENABLE_VR && ENABLE_XR_MODULE
                if (cameraTargetHandle.rt.vrUsage != VRTextureUsage.None)
                {
                    width = XRSystem.ScaleTextureWidthForXR(cameraTargetHandle.rt);
                    height = XRSystem.ScaleTextureHeightForXR(cameraTargetHandle.rt);
                }
                else
            #endif
                {
                    width *= ScalableBufferManager.widthScaleFactor;
                    height *= ScalableBufferManager.heightScaleFactor;
                }
            }
            cmd.SetGlobalVector(_ScreenResolution, new Vector4(width, height, 1.0f / width, 1.0f / height));
        }
        #endregion
    }

    /// <summary>
    /// This pass cleans up the global shader properties of physically based sky.
    /// </summary>
    private class PBSkyPostPass : ScriptableRenderPass
    {
        private const string profilerTag = "Cleanup Physically Based Sky";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(profilerTag);

        public PhysicallyBasedSky pbrSky;

        private const string PHYSICALLY_BASED_SKY = "PHYSICALLY_BASED_SKY";
        private const string SKY_NOT_BAKING = "SKY_NOT_BAKING";

        private static readonly int _EnableAtmosphericScattering = Shader.PropertyToID("_EnableAtmosphericScattering");
        private static readonly int _FogEnabled = Shader.PropertyToID("_FogEnabled");
        private static readonly int _SkyTextureMipCounts = Shader.PropertyToID("_SkyTextureMipCounts");

        public PBSkyPostPass()
        {

        }

        #region Non Render Graph Pass
    #if !UNITY_6000_4_OR_NEWER
    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {

        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                cmd.SetGlobalFloat(_EnableAtmosphericScattering, 0.0f);
                cmd.SetGlobalInteger(_FogEnabled, 0);
                cmd.SetGlobalFloat(_SkyTextureMipCounts, 0.0f);
                cmd.DisableShaderKeyword(PHYSICALLY_BASED_SKY);
                cmd.DisableShaderKeyword(SKY_NOT_BAKING);
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            
            CommandBufferPool.Release(cmd);
        }
    #endif
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass

        private class PassData
        {

        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            cmd.SetGlobalFloat(_EnableAtmosphericScattering, 0.0f);
            cmd.SetGlobalInteger(_FogEnabled, 0);
            cmd.SetGlobalFloat(_SkyTextureMipCounts, 0.0f);
            cmd.DisableShaderKeyword(PHYSICALLY_BASED_SKY);
            cmd.DisableShaderKeyword(SKY_NOT_BAKING);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(profilerTag, out var passData))
            {
                builder.AllowGlobalStateModification(true);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(context));
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {

        }
        #endregion
    }

    /// <summary>
    /// This pass updates the sky and environment reflection.
    /// </summary>
    private class AmbientProbePass : ScriptableRenderPass
    {
        private const string profilerTag = "Update Environment Reflection";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(profilerTag);

        public VisualEnvironment visualEnvironment;
        public Material cloudsMaterial;
        public bool isPbrSky;

        private RTHandle probeColorHandle;
        private RTHandle skyColorHandle;

        // TODO: expose this property
        private static readonly int reflectionResolution = 128;

        private const string _GlossyEnvironmentCubeMap = "_GlossyEnvironmentCubeMap";
        private const string _SkyTexture = "_SkyTexture";

        private const string VOLUMETRIC_CLOUDS = "VOLUMETRIC_CLOUDS";
        private const string STEREO_INSTANCING_ON = "STEREO_INSTANCING_ON";

        private static readonly int glossyEnvironmentCubeMap = Shader.PropertyToID(_GlossyEnvironmentCubeMap);
        private static readonly int skyTexture = Shader.PropertyToID(_SkyTexture);

        private static readonly int worldSpaceCameraPos = Shader.PropertyToID("_WorldSpaceCameraPos");
        private static readonly int disableSunDisk = Shader.PropertyToID("_DisableSunDisk");
        //private static readonly int unity_MatrixVP = Shader.PropertyToID("unity_MatrixVP");
        private static readonly int unity_MatrixInvVP = Shader.PropertyToID("unity_MatrixInvVP");
        private static readonly int scaledScreenParams = Shader.PropertyToID("_ScaledScreenParams");
        private static readonly int screenSize = Shader.PropertyToID("_ScreenSize");
        private static readonly int skyTextureMipCounts = Shader.PropertyToID("_SkyTextureMipCounts");

        // Modified from CoreUtils.lookAtList to swap the directions of up and down faces
        private static readonly Matrix4x4 frontView = new Matrix4x4(float4(-1, 0, 0, 0), float4(0, -1, 0, 0), float4(0, 0, -1, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 backView = new Matrix4x4(float4(1, 0, 0, 0), float4(0, -1, 0, 0), float4(0, 0, 1, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 upView = new Matrix4x4(float4(1, 0, 0, 0), float4(0, 0, -1, 0), float4(0, -1, 0, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 downView = new Matrix4x4(float4(1, 0, 0, 0), float4(0, 0, 1, 0), float4(0, 1, 0, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 rightView = new Matrix4x4(float4(0, 0, -1, 0), float4(0, -1, 0, 0), float4(1, 0, 0, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 leftView = new Matrix4x4(float4(0, 0, 1, 0), float4(0, -1, 0, 0), float4(-1, 0, 0, 0), float4(0, 0, 0, 1));

        // Cubemap Order: right, left, up, down, back, front. (+X, -X, +Y, -Y, +Z, -Z)
        private static readonly Matrix4x4[] skyViews = { rightView, leftView, upView, downView, backView, frontView };

    #if UNITY_6000_0_OR_NEWER
        private readonly RendererListHandle[] rendererListHandles = new RendererListHandle[6];
    #endif
        private readonly Matrix4x4[] skyViewMatrices = new Matrix4x4[6];


        private static readonly Vector4 m_ScaleBias = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);

        private static readonly Matrix4x4 skyProjectionMatrix = Matrix4x4.Perspective(90.0f, 1.0f, 0.1f, 10.0f);
        private static readonly Vector4 skyViewScreenParams = new Vector4(reflectionResolution, reflectionResolution, 1.0f + rcp(reflectionResolution), 1.0f + rcp(reflectionResolution));
        private static readonly Vector4 skyViewScreenSize = new Vector4(reflectionResolution, reflectionResolution, rcp(reflectionResolution), rcp(reflectionResolution));

        public AmbientProbePass(Material material)
        {
            cloudsMaterial = material;
        }

        #region Non Render Graph Pass
    #if !UNITY_6000_4_OR_NEWER
    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.msaaSamples = 1;
            desc.useMipMap = true;
            desc.autoGenerateMips = true;
            desc.width = reflectionResolution;
            desc.height = reflectionResolution;
            desc.dimension = TextureDimension.Cube;
            desc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
            desc.depthStencilFormat = GraphicsFormat.None;
            desc.depthBufferBits = 0;
            desc.useDynamicScale = false;

            bool hasVolumetricClouds = cloudsMaterial != null && Shader.IsKeywordEnabled(VOLUMETRIC_CLOUDS);

        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref probeColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _GlossyEnvironmentCubeMap);
            if (hasVolumetricClouds)
                RenderingUtils.ReAllocateHandleIfNeeded(ref skyColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _SkyTexture);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref probeColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _GlossyEnvironmentCubeMap);
            if (hasVolumetricClouds)
                RenderingUtils.ReAllocateIfNeeded(ref skyColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _SkyTexture);
        #endif

            ConfigureTarget(probeColorHandle, probeColorHandle);
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            Camera camera = renderingData.cameraData.camera;
            var desc = renderingData.cameraData.cameraTargetDescriptor;

            bool isStereoEnabled = camera.stereoEnabled;

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                if (isStereoEnabled)
                    cmd.DisableShaderKeyword(STEREO_INSTANCING_ON);

                float2 cameraResolution = float2(desc.width, desc.height);
                Vector3 cameraPositionWS = camera.transform.position;
                Vector4 cameraScreenSize = new Vector4(cameraResolution.x, cameraResolution.y, rcp(cameraResolution.x), rcp(cameraResolution.y));
                Vector4 cameraScreenParams = new Vector4(cameraResolution.x, cameraResolution.y, 1.0f + cameraScreenSize.z, 1.0f + cameraScreenSize.w);
                bool isDynamicAmbientMode = visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic;

                Matrix4x4 skyMatrixP = GL.GetGPUProjectionMatrix(skyProjectionMatrix, true);

                cmd.SetGlobalVector(worldSpaceCameraPos, Vector3.zero);
                cmd.SetGlobalFloat(disableSunDisk, 1.0f);

                cmd.SetGlobalVector(scaledScreenParams, skyViewScreenParams);
                cmd.SetGlobalVector(screenSize, skyViewScreenSize);

                bool hasVolumetricClouds = cloudsMaterial != null && Shader.IsKeywordEnabled(VOLUMETRIC_CLOUDS);

                for (int i = 0; i < 6; i++)
                {
                    CoreUtils.SetRenderTarget(cmd, hasVolumetricClouds ? skyColorHandle : probeColorHandle, ClearFlag.None, 0, (CubemapFace)i);

                    //var lookAt = Matrix4x4.LookAt(Vector3.zero, CoreUtils.lookAtList[i], CoreUtils.upVectorList[i]);
                    //Matrix4x4 viewMatrix = lookAt * Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)); // Need to scale -1.0 on Z to match what is being done in the camera.wolrdToCameraMatrix API. ...

                    Matrix4x4 viewMatrix = skyViews[i];
                    viewMatrix *= Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)); // Need to scale -1.0 on Z to match what is being done in the camera.wolrdToCameraMatrix API. ...
                    skyViewMatrices[i] = viewMatrix;

                    Matrix4x4 skyMatrixVP = skyMatrixP * skyViewMatrices[i];

                    // Camera matrices for skybox rendering
                    cmd.SetViewMatrix(skyViewMatrices[i]);
                    //cmd.SetGlobalMatrix(unity_MatrixVP, skyMatrixVP);
                    cmd.SetGlobalMatrix(unity_MatrixInvVP, skyMatrixVP.inverse);

                    if (isPbrSky)
                    {
                        Blitter.BlitTexture(cmd, probeColorHandle, m_ScaleBias, RenderSettings.skybox, pass: 1);
                    }
                    else
                    {
                        RendererList rendererList = context.CreateSkyboxRendererList(camera, skyProjectionMatrix, skyViewMatrices[i]);
                        cmd.DrawRendererList(rendererList);
                    }
                }

                cmd.SetGlobalTexture(skyTexture, hasVolumetricClouds ? skyColorHandle : probeColorHandle);
                int skyTextureMips = visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic ?
                    hasVolumetricClouds ? skyColorHandle.rt.mipmapCount : probeColorHandle.rt.mipmapCount : 0;
                cmd.SetGlobalFloat(skyTextureMipCounts, skyTextureMips);

                if (hasVolumetricClouds)
                {
                    // We split the rendering into 2 loops to avoid calling CopyTexture() multiple times, which can be slow on the GPU side.
                    cmd.CopyTexture(skyColorHandle, probeColorHandle);

                    for (int i = 0; i < 6; i++)
                    {
                        Matrix4x4 skyMatrixVP = skyMatrixP * skyViewMatrices[i];

                        // Camera matrices for skybox rendering
                        cmd.SetViewMatrix(skyViewMatrices[i]);
                        //cmd.SetGlobalMatrix(unity_MatrixVP, skyMatrixVP);
                        cmd.SetGlobalMatrix(unity_MatrixInvVP, skyMatrixVP.inverse);

                        CoreUtils.SetRenderTarget(cmd, probeColorHandle, ClearFlag.None, 0, (CubemapFace)i);
                        Blitter.BlitTexture(cmd, probeColorHandle, m_ScaleBias, cloudsMaterial, pass: 8);
                    }
                }

                cmd.SetGlobalTexture(glossyEnvironmentCubeMap, probeColorHandle);
                RenderSettings.defaultReflectionMode = isDynamicAmbientMode ? DefaultReflectionMode.Custom : RenderSettings.defaultReflectionMode;
                RenderSettings.customReflectionTexture = isDynamicAmbientMode ? probeColorHandle : null;
                cmd.SetGlobalVector(worldSpaceCameraPos, cameraPositionWS);
                cmd.SetGlobalFloat(disableSunDisk, 0.0f);

                Matrix4x4 matrixVP = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix;

                // Camera matrices for objects rendering
                cmd.SetViewMatrix(camera.worldToCameraMatrix);
                //cmd.SetGlobalMatrix(unity_MatrixVP, matrixVP);
                cmd.SetGlobalMatrix(unity_MatrixInvVP, matrixVP.inverse);
                cmd.SetGlobalVector(scaledScreenParams, cameraScreenParams);
                cmd.SetGlobalVector(screenSize, cameraScreenSize);

                if (isStereoEnabled)
                    cmd.EnableShaderKeyword(STEREO_INSTANCING_ON);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            
            CommandBufferPool.Release(cmd);
        }
    #endif
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        private class PassData
        {
            internal Material cloudsMaterial;

            internal TextureHandle probeColorHandle;
            internal TextureHandle skyColorHandle;

            internal Vector3 cameraPositionWS;
            internal Vector4 cameraScreenParams;
            internal Vector4 cameraScreenSize;
            internal Matrix4x4 worldToCameraMatrix;
            internal Matrix4x4 projectionMatrix;

            internal RendererListHandle[] rendererListHandles;
            internal Matrix4x4[] skyViewMatrices;
            internal Matrix4x4 skyProjectionMatrix;

            internal bool isDynamicAmbientMode;
            internal bool isPbrSky;
            internal bool hasVolumetricClouds;
            internal bool isStereoEnabled;

            internal int skyTextureMipCounts;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            if (data.isStereoEnabled)
                cmd.DisableShaderKeyword(STEREO_INSTANCING_ON);

            context.cmd.SetGlobalVector(worldSpaceCameraPos, Vector3.zero);
            context.cmd.SetGlobalFloat(disableSunDisk, 1.0f);

            context.cmd.SetGlobalVector(scaledScreenParams, skyViewScreenParams);
            context.cmd.SetGlobalVector(screenSize, skyViewScreenSize);

            Matrix4x4 skyMatrixP = GL.GetGPUProjectionMatrix(data.skyProjectionMatrix, true);

            for (int i = 0; i < 6; i++)
            {
                CoreUtils.SetRenderTarget(cmd, data.hasVolumetricClouds ? data.skyColorHandle : data.probeColorHandle, ClearFlag.None, 0, (CubemapFace)i);

                Matrix4x4 skyMatrixVP = skyMatrixP * data.skyViewMatrices[i];

                // Camera matrices for skybox rendering
                cmd.SetViewMatrix(data.skyViewMatrices[i]);
                //cmd.SetProjectionMatrix(skyMatrixP);
                //context.cmd.SetGlobalMatrix(unity_MatrixVP, skyMatrixVP);
                context.cmd.SetGlobalMatrix(unity_MatrixInvVP, skyMatrixVP.inverse);
                
                if (data.isPbrSky)
                {
                    Blitter.BlitTexture(cmd, data.probeColorHandle, m_ScaleBias, RenderSettings.skybox, pass: 1);
                }
                else
                {
                    context.cmd.DrawRendererList(data.rendererListHandles[i]);
                }
            }

            cmd.SetGlobalTexture(skyTexture, data.hasVolumetricClouds ? data.skyColorHandle : data.probeColorHandle);
            cmd.SetGlobalFloat(skyTextureMipCounts, data.skyTextureMipCounts);

            if (data.hasVolumetricClouds)
            {
                // We split the rendering into 2 loops to avoid calling CopyTexture() multiple times, which can be slow on the GPU side.
                cmd.CopyTexture(data.skyColorHandle, data.probeColorHandle);

                for (int i = 0; i < 6; i++)
                {
                    Matrix4x4 skyMatrixVP = skyMatrixP * data.skyViewMatrices[i];
                    // Camera matrices for skybox rendering
                    cmd.SetViewMatrix(data.skyViewMatrices[i]);
                    //cmd.SetProjectionMatrix(skyMatrixP);
                    //context.cmd.SetGlobalMatrix(unity_MatrixVP, skyMatrixVP);
                    context.cmd.SetGlobalMatrix(unity_MatrixInvVP, skyMatrixVP.inverse);

                    CoreUtils.SetRenderTarget(cmd, data.probeColorHandle, ClearFlag.None, 0, (CubemapFace)i);
                    Blitter.BlitTexture(cmd, data.probeColorHandle, m_ScaleBias, data.cloudsMaterial, pass: 8);
                }
            }

            context.cmd.SetGlobalTexture(glossyEnvironmentCubeMap, data.probeColorHandle);
            RenderSettings.defaultReflectionMode = data.isDynamicAmbientMode ? DefaultReflectionMode.Custom : RenderSettings.defaultReflectionMode;
            RenderSettings.customReflectionTexture = data.isDynamicAmbientMode ? data.probeColorHandle : null;
            context.cmd.SetGlobalVector(worldSpaceCameraPos, data.cameraPositionWS);
            context.cmd.SetGlobalFloat(disableSunDisk, 0.0f);

            Matrix4x4 matrixVP = GL.GetGPUProjectionMatrix(data.projectionMatrix, true) * data.worldToCameraMatrix;

            // Camera matrices for objects rendering
            cmd.SetViewMatrix(data.worldToCameraMatrix);
            //cmd.SetProjectionMatrix(data.projectionMatrix);
            //context.cmd.SetGlobalMatrix(unity_MatrixVP, matrixVP);
            context.cmd.SetGlobalMatrix(unity_MatrixInvVP, matrixVP.inverse);
            context.cmd.SetGlobalVector(scaledScreenParams, data.cameraScreenParams);
            context.cmd.SetGlobalVector(screenSize, data.cameraScreenSize);

            if (data.isStereoEnabled)
                cmd.EnableShaderKeyword(STEREO_INSTANCING_ON);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(profilerTag, out var passData))
            {
                // UniversalResourceData contains all the texture handles used by the renderer, including the active color and depth textures
                // The active color and depth textures are the main color and depth buffers that the camera renders into
                UniversalRenderingData universalRenderingData = frameData.Get<UniversalRenderingData>();
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                bool hasVolumetricClouds = cloudsMaterial != null && Shader.IsKeywordEnabled(VOLUMETRIC_CLOUDS);

                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;

                float2 cameraResolution = float2(desc.width, desc.height);
                
                desc.msaaSamples = 1;
                desc.useMipMap = true;
                desc.autoGenerateMips = true;
                desc.width = reflectionResolution;
                desc.height = reflectionResolution;
                desc.dimension = TextureDimension.Cube;
                desc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                desc.depthBufferBits = 0;
                desc.useDynamicScale = false;

                RenderingUtils.ReAllocateHandleIfNeeded(ref probeColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _GlossyEnvironmentCubeMap);
                TextureHandle probeColorTextureHandle = renderGraph.ImportTexture(probeColorHandle);
                passData.probeColorHandle = probeColorTextureHandle;

                if (hasVolumetricClouds)
                {
                    RenderingUtils.ReAllocateHandleIfNeeded(ref skyColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _SkyTexture);
                    TextureHandle skyColorTextureHandle = renderGraph.ImportTexture(skyColorHandle);
                    passData.skyColorHandle = skyColorTextureHandle;
                }

                passData.skyTextureMipCounts = visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic ?
                    hasVolumetricClouds ? skyColorHandle.rt.mipmapCount : probeColorHandle.rt.mipmapCount : 0;

                passData.cloudsMaterial = cloudsMaterial;

                for (int i = 0; i < 6; i++)
                {
                    //var lookAt = Matrix4x4.LookAt(Vector3.zero, CoreUtils.lookAtList[i], CoreUtils.upVectorList[i]);
                    //Matrix4x4 viewMatrix = lookAt * Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)); // Need to scale -1.0 on Z to match what is being done in the camera.wolrdToCameraMatrix API. ...

                    Matrix4x4 viewMatrix = skyViews[i];
                    viewMatrix *= Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)); // Need to scale -1.0 on Z to match what is being done in the camera.wolrdToCameraMatrix API. ...
                    skyViewMatrices[i] = viewMatrix;
                    rendererListHandles[i] = renderGraph.CreateSkyboxRendererList(cameraData.camera, skyProjectionMatrix, viewMatrix);
                    builder.UseRendererList(rendererListHandles[i]);
                }

                // Fill up the passData with the data needed by the pass
                passData.rendererListHandles = rendererListHandles;
                passData.skyViewMatrices = skyViewMatrices;
                passData.skyProjectionMatrix = skyProjectionMatrix;
                passData.cloudsMaterial = cloudsMaterial;
                passData.cameraPositionWS = cameraData.camera.transform.position;
                passData.cameraScreenSize = new Vector4(cameraResolution.x, cameraResolution.y, rcp(cameraResolution.x), rcp(cameraResolution.y));
                passData.cameraScreenParams = new Vector4(cameraResolution.x, cameraResolution.y, 1.0f + passData.cameraScreenSize.z, 1.0f + passData.cameraScreenSize.w);
                passData.worldToCameraMatrix = cameraData.camera.worldToCameraMatrix;
                passData.projectionMatrix = cameraData.camera.projectionMatrix;
                passData.isDynamicAmbientMode = visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic;
                passData.isPbrSky = isPbrSky;
                passData.hasVolumetricClouds = hasVolumetricClouds;
                passData.isStereoEnabled = cameraData.camera.stereoEnabled;

                // UnsafePasses don't setup the outputs using UseTextureFragment/UseTextureFragmentDepth, you should specify your writes with UseTexture instead
                builder.UseTexture(passData.probeColorHandle, AccessFlags.Write);

                if (hasVolumetricClouds)
                    builder.UseTexture(passData.skyColorHandle, AccessFlags.Write);

                // Shader keyword changes are considered as global state modifications
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {
            probeColorHandle?.Release();
            skyColorHandle?.Release();
        }

        #endregion
    }

}
