using Client.Data;
using Client.Data.BMD;
using Godot;
using SysVector3 = System.Numerics.Vector3;
using SysQuaternion = System.Numerics.Quaternion;
using SysMatrix = System.Numerics.Matrix4x4;

namespace MuGodot;

/// <summary>
/// Loads BMD models from MU Online data and converts them to Godot ArrayMesh objects.
/// Supports skeletal bind-pose transformation for proper mesh rendering.
/// </summary>
public class MuModelBuilder
{
    private readonly BMDReader _reader = new();
    private readonly Dictionary<string, BMD> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Load a BMD file and return a Godot ArrayMesh with the idle-pose geometry.
    /// Each BMD mesh becomes a separate surface in the ArrayMesh.
    /// </summary>
    private readonly HashSet<string> _loggedMissing = new();

    public async Task<ArrayMesh?> LoadModelAsync(string relativePath)
    {
        var bmd = await LoadBmdAsync(relativePath);
        if (bmd == null)
            return null;

        return BuildMesh(bmd, actionIndex: 0, framePos: 0f);
    }

    public async Task<BMD?> LoadBmdAsync(string relativePath, bool logMissing = true)
    {
        var fullPath = System.IO.Path.Combine(MuConfig.DataPath, relativePath);

        if (!System.IO.File.Exists(fullPath))
            fullPath = FindFileInsensitive(fullPath);

        if (fullPath == null || !System.IO.File.Exists(fullPath))
        {
            if (logMissing && _loggedMissing.Add(relativePath))
                GD.Print($"  [BMD] Not found: {relativePath}");
            return null;
        }

        if (_cache.TryGetValue(fullPath, out var cached))
            return cached;

        var bmd = await _reader.Load(fullPath);
        _cache[fullPath] = bmd;
        return bmd;
    }

    public bool HasAnimatedAction(BMD bmd, int actionIndex = 0)
    {
        if (bmd.Bones == null || bmd.Bones.Length == 0 || bmd.Actions == null || bmd.Actions.Length == 0)
            return false;

        actionIndex = Math.Clamp(actionIndex, 0, bmd.Actions.Length - 1);
        return bmd.Actions[actionIndex].NumAnimationKeys > 1;
    }

    public float GetActionPlaySpeed(BMD bmd, int actionIndex = 0)
    {
        if (bmd.Actions == null || bmd.Actions.Length == 0)
            return 1f;

        actionIndex = Math.Clamp(actionIndex, 0, bmd.Actions.Length - 1);
        var speed = bmd.Actions[actionIndex].PlaySpeed;
        return speed <= 0f ? 1f : speed;
    }

    /// <summary>
    /// Convert a BMD model to a Godot ArrayMesh.
    /// Uses the selected action/frame position for bone transforms.
    /// </summary>
    public ArrayMesh? BuildMesh(BMD bmd, int actionIndex = 0, float framePos = 0f, ArrayMesh? targetMesh = null)
    {
        if (bmd.Meshes == null || bmd.Meshes.Length == 0)
            return null;

        var boneMatrices = ComputeBoneMatrices(bmd, actionIndex, framePos);

        var arrayMesh = targetMesh ?? new ArrayMesh();
        arrayMesh.ClearSurfaces();

        for (int meshIdx = 0; meshIdx < bmd.Meshes.Length; meshIdx++)
        {
            var mesh = bmd.Meshes[meshIdx];
            if (mesh.Triangles == null || mesh.Triangles.Length == 0)
                continue;

            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);

            foreach (var tri in mesh.Triangles)
            {
                int vertexCount = Math.Min((int)tri.Polygon, 4);
                if (vertexCount < 3) continue;

                // Build triangle(s) from the polygon
                // For triangles (3 vertices): one triangle
                // For quads (4 vertices): two triangles
                int[] indices = vertexCount == 3
                    ? new[] { 0, 1, 2 }
                    : new[] { 0, 1, 2, 0, 2, 3 };

                foreach (int idx in indices)
                {
                    if (idx >= vertexCount) break;

                    var vertIdx = tri.VertexIndex[idx];
                    var tcIdx = tri.TexCoordIndex[idx];

                    if (vertIdx < 0 || vertIdx >= mesh.Vertices.Length) continue;

                    var vert = mesh.Vertices[vertIdx];
                    var pos = vert.Position;

                    // Apply bone transform
                    if (vert.Node >= 0 && vert.Node < boneMatrices.Length)
                    {
                        var boneMatrix = boneMatrices[vert.Node];
                        pos = SysVector3.Transform(pos, boneMatrix);
                    }

                    // Convert MU coords to Godot coords
                    // MU: X=right, Y=forward, Z=up -> Godot: X=right, Y=up, Z=-forward
                    var gdPos = MuToGodot(pos);

                    if (tcIdx >= 0 && tcIdx < mesh.TexCoords.Length)
                    {
                        var tc = mesh.TexCoords[tcIdx];
                        st.SetUV(new Vector2(tc.U, tc.V));
                    }

                    st.AddVertex(gdPos);
                }
            }

            st.Index();
            st.GenerateNormals();
            var surfaceMesh = st.Commit();
            if (surfaceMesh != null)
            {
                // Merge surface into the main mesh
                for (int s = 0; s < surfaceMesh.GetSurfaceCount(); s++)
                {
                    var arrays = surfaceMesh.SurfaceGetArrays(s);
                    arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                }
            }
        }

        return arrayMesh.GetSurfaceCount() > 0 ? arrayMesh : null;
    }

    /// <summary>
    /// Compute world-space bone matrices for a specific animation action and frame position.
    /// </summary>
    private static SysMatrix[] ComputeBoneMatrices(BMD bmd, int actionIndex, float framePos)
    {
        if (bmd.Bones == null || bmd.Bones.Length == 0)
            return Array.Empty<SysMatrix>();

        BMDTextureAction? action = null;
        int frame0 = 0;
        int frame1 = 0;
        float frameLerp = 0f;
        bool lockPositions = false;

        if (bmd.Actions != null && bmd.Actions.Length > 0)
        {
            actionIndex = Math.Clamp(actionIndex, 0, bmd.Actions.Length - 1);
            action = bmd.Actions[actionIndex];
            lockPositions = action.LockPositions;

            int keyCount = Math.Max(action.NumAnimationKeys, 1);
            int totalFrames = Math.Max(lockPositions ? keyCount - 1 : keyCount, 1);
            float normalizedFrame = PositiveModulo(framePos, totalFrames);

            frame0 = (int)MathF.Floor(normalizedFrame);
            if (totalFrames > 1)
            {
                frame1 = (frame0 + 1) % totalFrames;
                frameLerp = normalizedFrame - frame0;
            }
            else
            {
                frame1 = frame0;
            }
        }

        var matrices = new SysMatrix[bmd.Bones.Length];

        for (int i = 0; i < bmd.Bones.Length; i++)
        {
            var bone = bmd.Bones[i];
            SysMatrix localMatrix = SysMatrix.Identity;

            if (action != null && bone.Matrixes != null && actionIndex < bone.Matrixes.Length)
            {
                var boneMatrix = bone.Matrixes[actionIndex];
                int maxFrame = Math.Min(boneMatrix.Position?.Length ?? 0, boneMatrix.Quaternion?.Length ?? 0) - 1;

                if (maxFrame >= 0)
                {
                    int f0 = Math.Clamp(frame0, 0, maxFrame);
                    int f1 = Math.Clamp(frame1, 0, maxFrame);
                    float t = f0 == f1 ? 0f : frameLerp;

                    var p0 = boneMatrix.Position![f0];
                    var p1 = boneMatrix.Position![f1];
                    var position = SysVector3.Lerp(p0, p1, t);

                    var q0 = boneMatrix.Quaternion![f0];
                    var q1 = boneMatrix.Quaternion![f1];
                    var rotation = SysQuaternion.Normalize(SysQuaternion.Slerp(q0, q1, t));

                    localMatrix = SysMatrix.CreateFromQuaternion(rotation) *
                                  SysMatrix.CreateTranslation(position);

                    if (i == 0 && lockPositions && boneMatrix.Position.Length > 0)
                    {
                        var rootPos = boneMatrix.Position[0];
                        localMatrix.M41 = rootPos.X;
                        localMatrix.M42 = rootPos.Y;
                    }
                }
            }

            if (bone.Parent >= 0 && bone.Parent < i)
                matrices[i] = localMatrix * matrices[bone.Parent];
            else
                matrices[i] = localMatrix;
        }

        return matrices;
    }

    private static float PositiveModulo(float value, int modulo)
    {
        if (modulo <= 0)
            return 0f;

        float m = value % modulo;
        return m < 0f ? m + modulo : m;
    }

    /// <summary>
    /// Load a BMD model's textures and return them as Godot materials.
    /// </summary>
    public async Task<StandardMaterial3D[]> LoadModelTexturesAsync(string relativePath)
    {
        var fullPath = System.IO.Path.Combine(MuConfig.DataPath, relativePath);
        if (!System.IO.File.Exists(fullPath))
            fullPath = FindFileInsensitive(fullPath);

        if (fullPath == null || !_cache.TryGetValue(fullPath, out var bmd))
            return Array.Empty<StandardMaterial3D>();

        var dir = System.IO.Path.GetDirectoryName(relativePath) ?? "";
        var materials = new StandardMaterial3D[bmd.Meshes.Length];

        for (int i = 0; i < bmd.Meshes.Length; i++)
        {
            var mesh = bmd.Meshes[i];
            var material = new StandardMaterial3D();
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;

            if (!string.IsNullOrEmpty(mesh.TexturePath))
            {
                var texPath = System.IO.Path.Combine(MuConfig.DataPath, dir, mesh.TexturePath);

                // Use extension-aware MU resolution (e.g. .tga request -> .ozt file),
                // with case-insensitive lookup and "texture/" folder fallback.
                var loadedTexture = await TryLoadTexture(texPath);
                if (loadedTexture.HasValue)
                {
                    material.AlbedoTexture = loadedTexture.Value.Texture;

                    // Match MonoGame behavior where Components==4 meshes use alpha-tested pass.
                    if (loadedTexture.Value.HasSourceAlpha)
                        ApplyAlphaCutout(material);
                }
            }

            materials[i] = material;
        }

        return materials;
    }

    private static async Task<MuTextureHelper.LoadedTexture?> TryLoadTexture(string basePath)
    {
        var candidates = EnumerateTextureCandidates(basePath);
        foreach (var candidate in candidates)
        {
            // MonoGame object textures are effectively sampled without generated mipmaps.
            var tex = await MuTextureHelper.LoadTextureWithInfoAsync(candidate, generateMipmaps: false);
            if (tex != null)
                return tex;
        }

        return null;
    }

    private static void ApplyAlphaCutout(StandardMaterial3D material)
    {
        material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
        material.AlphaScissorThreshold = 0.01f;
        material.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
        material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
        material.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
    }

    private static IEnumerable<string> EnumerateTextureCandidates(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            yield break;

        string? dir = System.IO.Path.GetDirectoryName(requestedPath);
        if (string.IsNullOrEmpty(dir))
            yield break;

        string fileNoExt = System.IO.Path.GetFileNameWithoutExtension(requestedPath);
        if (string.IsNullOrEmpty(fileNoExt))
            yield break;

        // Same mapping strategy as MonoGame TextureLoader:
        // .tga -> OZT, .jpg -> OZJ, .png -> OZP.
        string ext = System.IO.Path.GetExtension(requestedPath).ToLowerInvariant();
        string[] preferredExts = ext switch
        {
            ".tga" or ".ozt" => new[] { ".ozt", ".tga", ".ozj", ".jpg", ".ozp", ".png" },
            ".jpg" or ".jpeg" or ".ozj" => new[] { ".ozj", ".jpg", ".jpeg", ".ozt", ".tga", ".ozp", ".png" },
            ".png" or ".ozp" => new[] { ".ozp", ".png", ".ozj", ".jpg", ".ozt", ".tga" },
            _ => new[] { ext, ".ozt", ".ozj", ".ozp", ".tga", ".jpg", ".png" }
        };

        // 1) direct directory
        foreach (var candidate in BuildPathCandidates(dir, fileNoExt, preferredExts))
            yield return candidate;

        // 2) MU fallback directory: "<dir>/texture"
        var textureDir = System.IO.Path.Combine(dir, "texture");
        foreach (var candidate in BuildPathCandidates(textureDir, fileNoExt, preferredExts))
            yield return candidate;
    }

    private static IEnumerable<string> BuildPathCandidates(string directory, string fileNoExt, IEnumerable<string> exts)
    {
        foreach (var ext in exts)
        {
            if (string.IsNullOrEmpty(ext))
                continue;

            var path = System.IO.Path.Combine(directory, fileNoExt + ext);

            if (System.IO.File.Exists(path))
            {
                yield return path;
                continue;
            }

            var resolved = FindFileInsensitive(path);
            if (resolved != null)
                yield return resolved;
        }
    }

    /// <summary>
    /// Case-insensitive file lookup. Checks directory for matching filename.
    /// </summary>
    private static string? FindFileInsensitive(string fullPath)
    {
        var dir = System.IO.Path.GetDirectoryName(fullPath);
        var fileName = System.IO.Path.GetFileName(fullPath);
        if (dir == null || !System.IO.Directory.Exists(dir))
        {
            // Try case-insensitive directory match too
            var parentDir = System.IO.Path.GetDirectoryName(dir);
            var dirName = System.IO.Path.GetFileName(dir);
            if (parentDir == null || dirName == null || !System.IO.Directory.Exists(parentDir))
                return null;

            var matchDir = System.IO.Directory.GetDirectories(parentDir)
                .FirstOrDefault(d => string.Equals(System.IO.Path.GetFileName(d), dirName, StringComparison.OrdinalIgnoreCase));
            if (matchDir == null) return null;
            dir = matchDir;
        }

        var matchFile = System.IO.Directory.GetFiles(dir)
            .FirstOrDefault(f => string.Equals(System.IO.Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        return matchFile;
    }

    /// <summary>
    /// Diagnostic: list what's in the Object folder for a given world.
    /// </summary>
    public static void DiagnoseObjectFolder(int worldIndex)
    {
        var objectDir = System.IO.Path.Combine(MuConfig.DataPath, $"Object{worldIndex}");
        if (!System.IO.Directory.Exists(objectDir))
        {
            // Check for case-insensitive match
            var dataDir = MuConfig.DataPath;
            if (System.IO.Directory.Exists(dataDir))
            {
                var dirs = System.IO.Directory.GetDirectories(dataDir)
                    .Where(d => System.IO.Path.GetFileName(d).StartsWith("Object", StringComparison.OrdinalIgnoreCase))
                    .Take(10)
                    .Select(d => System.IO.Path.GetFileName(d));
                GD.Print($"  [DIAG] Object{worldIndex} not found. Available Object folders: {string.Join(", ", dirs)}");
            }
            else
            {
                GD.PrintErr($"  [DIAG] DataPath does not exist: {dataDir}");
            }
            return;
        }

        var bmdFiles = System.IO.Directory.GetFiles(objectDir, "*.bmd").Take(5);
        GD.Print($"  [DIAG] Object{worldIndex}/ contains {System.IO.Directory.GetFiles(objectDir, "*.bmd").Length} BMD files. First 5: {string.Join(", ", bmdFiles.Select(System.IO.Path.GetFileName))}");
    }

    /// <summary>
    /// Convert MU position (X-right, Y-forward, Z-up) to Godot (X-right, Y-up, Z-back).
    /// Applies the world scale factor.
    /// </summary>
    private static Vector3 MuToGodot(SysVector3 v)
    {
        return new Vector3(v.X * MuConfig.WorldToGodot, v.Z * MuConfig.WorldToGodot, -v.Y * MuConfig.WorldToGodot);
    }

    /// <summary>
    /// Convert MU direction vector to Godot (no scaling).
    /// </summary>
    private static Vector3 MuToGodotDir(SysVector3 v)
    {
        return new Vector3(v.X, v.Z, -v.Y);
    }
}
