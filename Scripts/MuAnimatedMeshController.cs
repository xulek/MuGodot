using Client.Data.BMD;
using Godot;
using System.Collections.Generic;

namespace MuGodot;

/// <summary>
/// Plays cached mesh frames for a BMD action.
/// All registered MeshInstance3D nodes animate in sync without per-frame geometry rebuild.
/// </summary>
[Tool]
public partial class MuAnimatedMeshController : Node
{
    private const int MaxCachedFrames = 240;
    private const int TargetVertexBudget = 900_000;

    private readonly List<MeshInstance3D> _targets = new();
    private ArrayMesh[] _frames = [];
    private int _baseFrameCount = 1;
    private int _samplesPerFrame = 1;
    private int _frameCount = 1;
    private int _currentFrame;
    private float _animationSpeed = 4f;
    private float _actionPlaySpeed = 1f;
    private float _startFrame;
    private float _startTimeSeconds;
    private float _framePos;

    public void Initialize(
        MuModelBuilder modelBuilder,
        BMD bmd,
        StandardMaterial3D[] materials,
        int actionIndex = 0,
        float animationSpeed = 4f,
        float startFrame = 0f,
        int subFrameSamples = 0)
    {
        _animationSpeed = animationSpeed;
        _actionPlaySpeed = modelBuilder.GetActionPlaySpeed(bmd, actionIndex);
        _baseFrameCount = GetTotalFrames(bmd, actionIndex);
        int vertexCostPerFrame = EstimateFrameVertexCost(bmd);
        _samplesPerFrame = ResolveSamplesPerFrame(_baseFrameCount, vertexCostPerFrame, subFrameSamples);
        _frameCount = Math.Max(1, _baseFrameCount * _samplesPerFrame);

        _frames = new ArrayMesh[_frameCount];
        for (int i = 0; i < _frameCount; i++)
        {
            float sourceFramePos = (float)i / _samplesPerFrame;
            var mesh = modelBuilder.BuildMesh(bmd, actionIndex: actionIndex, framePos: sourceFramePos);
            if (mesh == null)
                continue;

            ApplyMaterials(mesh, materials);
            _frames[i] = mesh;
        }

        _startFrame = WrapFrame(startFrame, _frameCount);
        _startTimeSeconds = Time.GetTicksMsec() * 0.001f;
        _framePos = _startFrame;
        _currentFrame = (int)_framePos;
        if (_frames[Math.Clamp(_currentFrame, 0, _frames.Length - 1)] == null)
        {
            for (int i = 0; i < _frames.Length; i++)
            {
                if (_frames[i] == null)
                    continue;

                _currentFrame = i;
                _framePos = i;
                break;
            }
        }
        SetProcess(_frameCount > 1);
    }

    public void RegisterInstance(MeshInstance3D instance)
    {
        if (instance == null)
            return;

        _targets.Add(instance);
        instance.Mesh = GetCurrentMesh();
    }

    public Mesh? GetCurrentMesh()
    {
        if (_frames.Length == 0)
            return null;
        var mesh = _frames[Math.Clamp(_currentFrame, 0, _frames.Length - 1)];
        if (mesh != null)
            return mesh;

        for (int i = 0; i < _frames.Length; i++)
            if (_frames[i] != null)
                return _frames[i];

        return null;
    }

    public override void _Process(double delta)
    {
        if (_frameCount <= 1 || _frames.Length == 0)
            return;

        // Use absolute time to reduce jitter from variable frame delta.
        float nowSeconds = Time.GetTicksMsec() * 0.001f;
        float elapsed = nowSeconds - _startTimeSeconds;
        float frameAdvance = elapsed * _actionPlaySpeed * _animationSpeed * _samplesPerFrame;
        _framePos = WrapFrame(_startFrame + frameAdvance, _frameCount);
        int newFrame = (int)_framePos;
        if (newFrame == _currentFrame)
            return;

        _currentFrame = newFrame;
        var mesh = GetCurrentMesh();
        if (mesh == null)
            return;

        for (int i = 0; i < _targets.Count; i++)
        {
            var target = _targets[i];
            if (target == null || !IsInstanceValid(target))
                continue;

            target.Mesh = mesh;
        }
    }

    private static int GetTotalFrames(BMD bmd, int actionIndex)
    {
        if (bmd.Actions == null || bmd.Actions.Length == 0)
            return 1;

        actionIndex = Math.Clamp(actionIndex, 0, bmd.Actions.Length - 1);
        var action = bmd.Actions[actionIndex];
        int keyCount = Math.Max(action.NumAnimationKeys, 1);
        return Math.Max(action.LockPositions ? keyCount - 1 : keyCount, 1);
    }

    private static int ResolveSamplesPerFrame(int baseFrameCount, int frameVertexCost, int requestedSamples)
    {
        if (baseFrameCount <= 1)
            return 1;

        int samples;
        if (requestedSamples > 0)
        {
            samples = requestedSamples;
        }
        else if (baseFrameCount <= 4)
        {
            samples = 32;
        }
        else if (baseFrameCount <= 8)
        {
            samples = 24;
        }
        else if (baseFrameCount <= 16)
        {
            samples = 12;
        }
        else if (baseFrameCount <= 24)
        {
            samples = 8;
        }
        else
        {
            samples = 4;
        }

        int maxAllowedByCap = Math.Max(1, MaxCachedFrames / baseFrameCount);
        int perFrameCost = Math.Max(1, frameVertexCost);
        int maxAllowedByBudget = Math.Max(1, TargetVertexBudget / (perFrameCost * baseFrameCount));

        int maxAllowed = Math.Min(maxAllowedByCap, maxAllowedByBudget);
        return Math.Clamp(samples, 1, Math.Max(1, maxAllowed));
    }

    private static int EstimateFrameVertexCost(BMD bmd)
    {
        if (bmd.Meshes == null || bmd.Meshes.Length == 0)
            return 1;

        long total = 0;
        for (int i = 0; i < bmd.Meshes.Length; i++)
        {
            var mesh = bmd.Meshes[i];
            if (mesh?.Triangles == null)
                continue;

            for (int t = 0; t < mesh.Triangles.Length; t++)
            {
                int vertexCount = Math.Min((int)mesh.Triangles[t].Polygon, 4);
                if (vertexCount == 3)
                    total += 3;
                else if (vertexCount == 4)
                    total += 6;
            }
        }

        return (int)Math.Clamp(total, 1, int.MaxValue);
    }

    private static float WrapFrame(float value, int frameCount)
    {
        if (frameCount <= 0)
            return 0f;

        float wrapped = value % frameCount;
        return wrapped < 0f ? wrapped + frameCount : wrapped;
    }

    private static void ApplyMaterials(ArrayMesh mesh, StandardMaterial3D[] materials)
    {
        int surfaceCount = mesh.GetSurfaceCount();
        int count = Math.Min(surfaceCount, materials.Length);

        for (int i = 0; i < count; i++)
        {
            var mat = materials[i];
            if (mat == null)
                continue;

            mesh.SurfaceSetMaterial(i, mat);
        }
    }
}
