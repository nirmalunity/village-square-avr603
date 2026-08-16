using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PerlinNoiseTerrain : MonoBehaviour
{
    // ── Modes ─────────────────────────────────────────────────────────────────
    public enum NoiseMode { Basic, Fractal, Island, Animated }
    static readonly string[] ModeDescriptions =
    {
        "MODE 0 — Basic Noise\nSingle octave of raw Perlin noise.\nSmooth but not very detailed.",
        "MODE 1 — Fractal Noise (fBm)\nMultiple octaves layered together.\nAdds fine detail on top of large hills.",
        "MODE 2 — Island\nFractal noise × circular falloff.\nEdges fade to ocean — shows noise masking.",
        "MODE 3 — Animated\nTime used as 3rd noise axis.\nTerrain morphs smoothly — good for water/clouds.",
    };

    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Terrain")]
    public int   width           = 100;
    public int   depth           = 100;
    public float heightMultiplier = 14f;

    [Header("Noise")]
    public NoiseMode mode  = NoiseMode.Fractal;
    public float     scale = 22f;          // lower = zoomed in, higher = zoomed out

    [Header("Fractal Octaves")]
    [Tooltip("How many noise layers to combine (Fractal & Island modes)")]
    public int   octaves     = 4;
    [Tooltip("How much each successive octave contributes (0-1). Lower = less detail.")]
    public float persistence = 0.5f;
    [Tooltip("How much frequency increases per octave. 2 = each octave is twice as fine.")]
    public float lacunarity  = 2f;

    [Header("Animation (Mode 3)")]
    public float animSpeed = 0.25f;

    [Header("Seed")]
    public int  seed       = 0;
    public bool randomSeed = true;

    // ── References (set by editor script) ─────────────────────────────────────
    [HideInInspector] public GameObject previewQuad;   // shows raw greyscale noise

    // ── Height colour gradient ─────────────────────────────────────────────────
    // Thresholds and matching colours from low → high
    static readonly float[] Thresholds =
    {
        0.28f,                              // deep water / ocean
        0.36f,                              // water
        0.41f,                              // sand / shore
        0.55f,                              // grass / plains
        0.67f,                              // forest / hills
        0.80f,                              // rocky mountain
        1.00f                               // snow peak
    };
    static readonly Color[] Colours =
    {
        new Color(0.04f, 0.12f, 0.52f),    // deep water
        new Color(0.10f, 0.30f, 0.78f),    // water
        new Color(0.85f, 0.82f, 0.58f),    // sand
        new Color(0.32f, 0.66f, 0.22f),    // grass
        new Color(0.14f, 0.40f, 0.10f),    // forest
        new Color(0.52f, 0.47f, 0.42f),    // rock
        Color.white                         // snow
    };

    // ── Private ───────────────────────────────────────────────────────────────
    MeshFilter   mf;
    MeshRenderer mr;
    Mesh         mesh;
    Texture2D    colourTex;
    Texture2D    greyTex;
    Material     terrainMat;
    Material     previewMat;

    float offsetX, offsetZ;
    float animTime = 0f;

    // ─────────────────────────────────────────────────────────────────────────
    void Awake()
    {
        mf = GetComponent<MeshFilter>();
        mr = GetComponent<MeshRenderer>();
    }

    void Start()
    {
        if (randomSeed) seed = Random.Range(0, 99999);
        ApplySeed();

        // Create materials
        terrainMat       = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        terrainMat.name  = "TerrainMat";
        terrainMat.SetFloat("_Smoothness", 0f);
        mr.material      = terrainMat;

        if (previewQuad != null)
        {
            previewMat      = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            previewMat.name = "PreviewMat";
            previewMat.SetFloat("_Smoothness", 0f);
            previewQuad.GetComponent<Renderer>().material = previewMat;
        }

        GenerateTerrain();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.R))           Regenerate();
        if (Input.GetKeyDown(KeyCode.M))           CycleMode();
        if (Input.GetKeyDown(KeyCode.Equals))      AdjustScale(-2f);   // + key = zoom in
        if (Input.GetKeyDown(KeyCode.Minus))       AdjustScale(+2f);   // - key = zoom out
        if (Input.GetKeyDown(KeyCode.UpArrow))     AdjustOctaves(+1);
        if (Input.GetKeyDown(KeyCode.DownArrow))   AdjustOctaves(-1);

        if (mode == NoiseMode.Animated)
        {
            animTime += Time.deltaTime * animSpeed;
            GenerateTerrain();
        }
    }

    void OnGUI()
    {
        // Instructions overlay
        GUIStyle box = new GUIStyle(GUI.skin.box);
        box.fontSize  = 13;
        box.alignment = TextAnchor.UpperLeft;
        box.padding   = new RectOffset(10, 10, 8, 8);

        string info = ModeDescriptions[(int)mode] + "\n\n"
            + "Scale: " + scale.ToString("F0") + "   Octaves: " + octaves + "   Seed: " + seed + "\n\n"
            + "[R] New seed   [M] Cycle mode   [+/-] Scale   [↑↓] Octaves";

        GUI.Box(new Rect(10, 10, 420, 160), info, box);
    }

    // ── Controls ──────────────────────────────────────────────────────────────

    void Regenerate()
    {
        seed     = Random.Range(0, 99999);
        animTime = 0f;
        ApplySeed();
        GenerateTerrain();
    }

    void CycleMode()
    {
        mode = (NoiseMode)(((int)mode + 1) % 4);
        animTime = 0f;
        GenerateTerrain();
    }

    void AdjustScale(float delta)
    {
        scale = Mathf.Max(2f, scale + delta);
        GenerateTerrain();
    }

    void AdjustOctaves(int delta)
    {
        octaves = Mathf.Clamp(octaves + delta, 1, 8);
        GenerateTerrain();
    }

    void ApplySeed()
    {
        // Each seed gives a different region of noise space
        var rng = new System.Random(seed);
        offsetX = (float)(rng.NextDouble() * 10000.0);
        offsetZ = (float)(rng.NextDouble() * 10000.0);
    }

    // ── Terrain generation ────────────────────────────────────────────────────

    void GenerateTerrain()
    {
        // 1. Sample noise for every grid point
        float[,] heights = SampleAllHeights();

        // 2. Normalise to 0-1 so colour mapping is consistent
        Normalise(ref heights);

        // 3. Build mesh
        BuildMesh(heights);

        // 4. Build colour texture (terrain) and grey texture (preview)
        BuildTextures(heights);
    }

    // ── Noise sampling ────────────────────────────────────────────────────────

    float[,] SampleAllHeights()
    {
        var h = new float[width, depth];
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
                h[x, z] = Sample(x, z);
        return h;
    }

    float Sample(int x, int z)
    {
        // Map grid coords to noise space
        float nx = x / (float)width  * scale + offsetX;
        float nz = z / (float)depth  * scale + offsetZ;

        switch (mode)
        {
            case NoiseMode.Basic:
                // Pure single-frequency Perlin noise
                return Mathf.PerlinNoise(nx, nz);

            case NoiseMode.Fractal:
                return FractalNoise(nx, nz);

            case NoiseMode.Island:
                // Same as Fractal but multiplied by circular falloff
                float h = FractalNoise(nx, nz);
                float cx = x / (float)width  - 0.5f;   // -0.5 to 0.5
                float cz = z / (float)depth  - 0.5f;
                float falloff = Mathf.Clamp01(1f - (cx * cx + cz * cz) * 4f);
                falloff = Mathf.Pow(falloff, 1.8f);     // sharper coastline
                return h * falloff;

            case NoiseMode.Animated:
                // Add time as an offset — noise evolves over time
                return FractalNoise(nx + animTime, nz + animTime * 0.6f);
        }
        return 0f;
    }

    /// Fractal Brownian Motion (fBm):
    /// Adds 'octaves' layers of noise, each at double the frequency
    /// and half the amplitude, creating natural-looking terrain detail.
    float FractalNoise(float x, float z)
    {
        float total     = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float maxPossible = 0f;   // for normalisation

        for (int i = 0; i < octaves; i++)
        {
            total       += Mathf.PerlinNoise(x * frequency, z * frequency) * amplitude;
            maxPossible += amplitude;

            // Each octave: higher frequency (more detail), lower amplitude (less influence)
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        return total / maxPossible;
    }

    void Normalise(ref float[,] h)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
            {
                if (h[x, z] < min) min = h[x, z];
                if (h[x, z] > max) max = h[x, z];
            }
        float range = Mathf.Max(max - min, 0.0001f);
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
                h[x, z] = (h[x, z] - min) / range;
    }

    // ── Mesh builder ──────────────────────────────────────────────────────────

    void BuildMesh(float[,] heights)
    {
        if (mesh == null) { mesh = new Mesh(); mesh.name = "PerlinTerrain"; }
        mesh.Clear();

        int   vCount = width * depth;
        var   verts  = new Vector3[vCount];
        var   uvs    = new Vector2[vCount];
        var   tris   = new int[(width - 1) * (depth - 1) * 6];

        float cx = width  * 0.5f;
        float cz = depth  * 0.5f;

        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
        {
            int i    = z * width + x;
            verts[i] = new Vector3(x - cx, heights[x, z] * heightMultiplier, z - cz);
            uvs[i]   = new Vector2((float)x / (width - 1), (float)z / (depth - 1));
        }

        int t = 0;
        for (int z = 0; z < depth - 1; z++)
        for (int x = 0; x < width - 1; x++)
        {
            int bl = z * width + x;
            int br = bl + 1;
            int tl = bl + width;
            int tr = tl + 1;
            tris[t++] = bl; tris[t++] = tl; tris[t++] = tr;
            tris[t++] = bl; tris[t++] = tr; tris[t++] = br;
        }

        mesh.vertices  = verts;
        mesh.triangles = tris;
        mesh.uv        = uvs;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mf.mesh = mesh;
    }

    // ── Texture builder ───────────────────────────────────────────────────────

    void BuildTextures(float[,] heights)
    {
        // Colour texture for terrain surface
        if (colourTex == null)
            colourTex = new Texture2D(width, depth, TextureFormat.RGB24, false);

        // Grey texture for 2D noise preview
        if (greyTex == null)
            greyTex = new Texture2D(width, depth, TextureFormat.RGB24, false);

        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
        {
            float h = heights[x, z];
            colourTex.SetPixel(x, z, HeightToColour(h));
            greyTex.SetPixel(x, z, new Color(h, h, h));
        }

        colourTex.Apply();
        greyTex.Apply();

        colourTex.filterMode = FilterMode.Bilinear;
        greyTex.filterMode   = FilterMode.Bilinear;

        if (terrainMat != null)
            terrainMat.mainTexture = colourTex;

        if (previewMat != null)
            previewMat.mainTexture = greyTex;
    }

    // ── Colour mapping ────────────────────────────────────────────────────────

    Color HeightToColour(float h)
    {
        for (int i = 0; i < Thresholds.Length; i++)
            if (h <= Thresholds[i]) return Colours[i];
        return Colours[Colours.Length - 1];
    }
}
