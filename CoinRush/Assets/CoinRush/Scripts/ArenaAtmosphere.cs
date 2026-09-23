using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CoinRush
{
    /// <summary>
    /// The look of the arena: post-processing, background, ambient light, a glowing grid on the
    /// floor and a neon rim along the top of the walls.
    ///
    /// Built in code at startup, like the HUD and the level layout, so the look is readable in one
    /// file and nothing depends on settings clicked into the scene.
    ///
    /// Bloom only picks up pixels brighter than its threshold, so it relies on the coin, hazard and
    /// trim materials having HDR emission. Those are set on the material assets on purpose: the
    /// build strips the emission shader variant unless a material in the build already uses it,
    /// so switching emission on from code alone would glow in the Editor and not on a phone.
    /// </summary>
    public sealed class ArenaAtmosphere : MonoBehaviour
    {
        [Header("Scene")]
        [Tooltip("Defaults to the main camera.")]
        [SerializeField] Camera sceneCamera;

        [Tooltip("Defaults to the first directional light in the scene.")]
        [SerializeField] Light sun;

        [Tooltip("The floor. Gets the glowing grid. Left empty, the floor keeps its plain material.")]
        [SerializeField] Renderer ground;

        [Header("Background")]
        [SerializeField] Color background = new Color(0.035f, 0.045f, 0.08f);
        [SerializeField] Color ambientSky = new Color(0.22f, 0.27f, 0.42f);
        [SerializeField] Color ambientEquator = new Color(0.12f, 0.12f, 0.2f);
        [SerializeField] Color ambientGround = new Color(0.04f, 0.04f, 0.06f);

        [Header("Sun")]
        [SerializeField] Vector3 sunAngles = new Vector3(55f, -35f, 0f);
        [SerializeField] Color sunColor = new Color(1f, 0.93f, 0.85f);
        [SerializeField] float sunIntensity = 1.1f;
        [SerializeField, Range(0f, 1f)] float shadowStrength = 0.7f;

        [Header("Floor grid")]
        [Tooltip("Size of one grid square in world units.")]
        [SerializeField] float gridCellSize = 2f;
        [SerializeField, ColorUsage(false, true)] Color gridGlow = new Color(0.1f, 0.35f, 0.6f);

        [Header("Wall rim")]
        [SerializeField] Material trimMaterial;

        [Tooltip("Distance from the centre to the inside face of each wall.")]
        [SerializeField] float arenaInnerHalfSize = 14.5f;
        [SerializeField] float wallHeight = 2f;
        [SerializeField] float trimWidth = 0.2f;

        [Header("Post-processing")]
        [SerializeField] float bloomThreshold = 0.95f;
        [SerializeField] float bloomIntensity = 0.9f;
        [SerializeField, Range(0f, 1f)] float bloomScatter = 0.65f;
        [SerializeField] float postExposure = 0.25f;
        [SerializeField, Range(0f, 1f)] float vignette = 0.3f;

        VolumeProfile _profile;
        Texture2D _gridTexture;
        Material _groundMaterial;

        void Awake()
        {
            DressCamera();
            DressLighting();
            DressFloor();
            BuildTrim();
            BuildVolume();
        }

        void OnDestroy()
        {
            // Everything created with `new` or CreateInstance lives until destroyed by hand.
            if (_profile != null) Destroy(_profile);
            if (_gridTexture != null) Destroy(_gridTexture);
            if (_groundMaterial != null) Destroy(_groundMaterial);
        }

        void DressCamera()
        {
            if (sceneCamera == null) sceneCamera = Camera.main;
            if (sceneCamera == null) return;

            // A flat colour instead of the template skybox, so the arena floats in a dark space and
            // the glowing parts carry the image.
            sceneCamera.clearFlags = CameraClearFlags.SolidColor;
            sceneCamera.backgroundColor = background;

            // URP cameras ignore every Volume until post-processing is switched on per camera.
            var data = sceneCamera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
        }

        void DressLighting()
        {
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = ambientSky;
            RenderSettings.ambientEquatorColor = ambientEquator;
            RenderSettings.ambientGroundColor = ambientGround;

            if (sun == null)
            {
                foreach (var candidate in FindObjectsByType<Light>())
                {
                    if (candidate.type == LightType.Directional)
                    {
                        sun = candidate;
                        break;
                    }
                }
            }

            if (sun == null) return;

            sun.transform.rotation = Quaternion.Euler(sunAngles);
            sun.color = sunColor;
            sun.intensity = sunIntensity;
            sun.shadowStrength = shadowStrength;
        }

        void DressFloor()
        {
            if (ground == null) return;

            // The walls share the floor's material asset, so the grid goes on a copy that only the
            // floor uses.
            _groundMaterial = new Material(ground.sharedMaterial) { name = "Ground (grid)" };
            _gridTexture = CreateGridTexture();

            _groundMaterial.EnableKeyword("_EMISSION");
            _groundMaterial.SetTexture("_EmissionMap", _gridTexture);
            _groundMaterial.SetColor("_EmissionColor", gridGlow);

            // One texture repeat per grid cell. The floor's world size comes from its bounds, so the
            // grid stays square if the floor is rescaled. URP's Lit shader tiles every map by the
            // base map's scale, so that is the one to set; a scale on the emission map is ignored.
            var size = ground.bounds.size;
            _groundMaterial.SetTextureScale(
                "_BaseMap", new Vector2(size.x / gridCellSize, size.z / gridCellSize));

            ground.sharedMaterial = _groundMaterial;
        }

        static Texture2D CreateGridTexture()
        {
            // White lines on black, used as an emission mask. One line on each edge; tiled, the
            // pair meets as a single two-pixel line. Mipmaps fade the lines out with distance
            // instead of letting them shimmer at a shallow viewing angle.
            const int size = 64;
            const int line = 1;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true)
            {
                name = "Grid",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };

            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var onLine = x < line || y < line || x >= size - line || y >= size - line;
                    pixels[y * size + x] = onLine
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(0, 0, 0, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return texture;
        }

        void BuildTrim()
        {
            if (trimMaterial == null) return;

            var root = new GameObject("Wall Rim").transform;
            root.SetParent(transform, worldPositionStays: false);

            // A strip on top of each wall, flush with its inside face. Each is long enough to cover
            // the corner so the four meet without a gap.
            const float thickness = 0.06f;
            var length = (arenaInnerHalfSize + trimWidth) * 2f;
            var offset = arenaInnerHalfSize + trimWidth * 0.5f;
            var y = wallHeight + thickness * 0.5f;

            CreateStrip(root, new Vector3(0f, y, offset), new Vector3(length, thickness, trimWidth));
            CreateStrip(root, new Vector3(0f, y, -offset), new Vector3(length, thickness, trimWidth));
            CreateStrip(root, new Vector3(offset, y, 0f), new Vector3(trimWidth, thickness, length));
            CreateStrip(root, new Vector3(-offset, y, 0f), new Vector3(trimWidth, thickness, length));
        }

        void CreateStrip(Transform parent, Vector3 position, Vector3 scale)
        {
            var strip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            strip.name = "Rim";
            Destroy(strip.GetComponent<Collider>());
            strip.transform.SetParent(parent, worldPositionStays: false);
            strip.transform.localPosition = position;
            strip.transform.localScale = scale;

            var renderer = strip.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = trimMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        void BuildVolume()
        {
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();

            // Add<T>(true) marks every setting as overridden, so anything not set below takes the
            // effect's own default instead of whatever the project's default profile says.
            var bloom = _profile.Add<Bloom>(true);
            bloom.threshold.value = bloomThreshold;
            bloom.intensity.value = bloomIntensity;
            bloom.scatter.value = bloomScatter;
            bloom.highQualityFiltering.value = false;

            var tonemapping = _profile.Add<Tonemapping>(true);
            tonemapping.mode.value = TonemappingMode.ACES;

            var colour = _profile.Add<ColorAdjustments>(true);
            colour.postExposure.value = postExposure;
            colour.contrast.value = 12f;
            colour.saturation.value = 10f;

            var edges = _profile.Add<Vignette>(true);
            edges.intensity.value = vignette;
            edges.smoothness.value = 0.45f;

            var volumeObject = new GameObject("Post-processing");
            volumeObject.transform.SetParent(transform, worldPositionStays: false);

            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1f;
            volume.sharedProfile = _profile;
        }
    }
}
