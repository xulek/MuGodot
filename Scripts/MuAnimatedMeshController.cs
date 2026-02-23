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
    private bool _externalAnimationEnabled = true;
    private bool _useRealtimeInterpolation;
    private MuModelBuilder? _modelBuilder;
    private BMD? _modelBmd;
    private BMD? _animationSourceBmd;
    private Material[] _materials = [];
    private int _actionIndex;
    private ArrayMesh? _realtimeMesh;
    public int ActionIndex => _actionIndex;
    public float FramePosition => _framePos;

    public void Initialize(
        MuModelBuilder modelBuilder,
        BMD bmd,
        Material[]? materials,
        int actionIndex = 0,
        float animationSpeed = 4f,
        float startFrame = 0f,
        int subFrameSamples = 0,
        BMD? animationSourceBmd = null,
        float? syncStartTimeSeconds = null,
        bool useRealtimeInterpolation = false)
    {
        var animationTimelineBmd = animationSourceBmd ?? bmd;
        _useRealtimeInterpolation = useRealtimeInterpolation;
        _modelBuilder = modelBuilder;
        _modelBmd = bmd;
        _animationSourceBmd = animationSourceBmd;
        _materials = materials ?? [];
        _actionIndex = actionIndex;
        _animationSpeed = animationSpeed;
        _actionPlaySpeed = modelBuilder.GetActionPlaySpeed(animationTimelineBmd, actionIndex);
        _baseFrameCount = GetTotalFrames(animationTimelineBmd, actionIndex);
        if (_useRealtimeInterpolation)
        {
            _samplesPerFrame = 1;
            _frameCount = Math.Max(1, _baseFrameCount);
            _frames = [];
        }
        else
        {
            int vertexCostPerFrame = EstimateFrameVertexCost(bmd);
            _samplesPerFrame = ResolveSamplesPerFrame(_baseFrameCount, vertexCostPerFrame, subFrameSamples);
            _frameCount = Math.Max(1, _baseFrameCount * _samplesPerFrame);

            _frames = new ArrayMesh[_frameCount];
            for (int i = 0; i < _frameCount; i++)
            {
                float sourceFramePos = (float)i / _samplesPerFrame;
                var mesh = modelBuilder.BuildMesh(
                    bmd,
                    actionIndex: actionIndex,
                    framePos: sourceFramePos,
                    animationSourceBmd: animationSourceBmd);
                if (mesh == null)
                    continue;

                ApplyMaterials(mesh, _materials);
                _frames[i] = mesh;
            }
        }

        _startFrame = WrapFrame(startFrame, _baseFrameCount);
        _startTimeSeconds = syncStartTimeSeconds ?? (Time.GetTicksMsec() * 0.001f);
        _framePos = _startFrame;
        _currentFrame = (int)_framePos;

        if (_useRealtimeInterpolation)
        {
            UpdateRealtimeMesh(_framePos);
        }
        else if (_frames[Math.Clamp(_currentFrame, 0, _frames.Length - 1)] == null)
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

        RefreshProcessState();
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
        if (_useRealtimeInterpolation)
            return _realtimeMesh;

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

    public void SetExternalAnimationEnabled(bool enabled)
    {
        if (_externalAnimationEnabled == enabled)
            return;

        _externalAnimationEnabled = enabled;
        RefreshProcessState();
    }

    public bool HasAnyVisibleTargetWithinDistance(Vector3 cameraPosition, float maxDistanceSq)
    {
        if (_targets.Count == 0)
            return false;

        for (int i = 0; i < _targets.Count; i++)
        {
            var target = _targets[i];
            if (target == null || !IsInstanceValid(target) || !target.Visible)
                continue;

            if (target.GlobalPosition.DistanceSquaredTo(cameraPosition) <= maxDistanceSq)
                return true;
        }

        return false;
    }

    public bool HasAnyVisibleTarget()
    {
        if (_targets.Count == 0)
            return false;

        for (int i = 0; i < _targets.Count; i++)
        {
            var target = _targets[i];
            if (target == null || !IsInstanceValid(target))
                continue;

            if (target.Visible)
                return true;
        }

        return false;
    }

    public override void _Process(double delta)
    {
        if (_baseFrameCount <= 1)
            return;

        // Use absolute time to reduce jitter from variable frame delta.
        float nowSeconds = Time.GetTicksMsec() * 0.001f;
        float elapsed = nowSeconds - _startTimeSeconds;
        float frameAdvance = elapsed * _actionPlaySpeed * _animationSpeed;
        _framePos = WrapFrame(_startFrame + frameAdvance, _baseFrameCount);

        if (_useRealtimeInterpolation)
        {
            UpdateRealtimeMesh(_framePos);
            return;
        }

        if (_frames.Length == 0)
            return;

        int newFrame = (int)(_framePos * _samplesPerFrame);
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

    private void UpdateRealtimeMesh(float framePos)
    {
        if (_modelBuilder == null || _modelBmd == null)
            return;

        var mesh = _modelBuilder.BuildMesh(
            _modelBmd,
            actionIndex: _actionIndex,
            framePos: framePos,
            targetMesh: _realtimeMesh,
            animationSourceBmd: _animationSourceBmd);
        if (mesh == null)
            return;

        _realtimeMesh = mesh;
        ApplyMaterials(_realtimeMesh, _materials);

        for (int i = 0; i < _targets.Count; i++)
        {
            var target = _targets[i];
            if (target == null || !IsInstanceValid(target))
                continue;

            if (target.Mesh != _realtimeMesh)
                target.Mesh = _realtimeMesh;
        }
    }

    private void RefreshProcessState()
    {
        int timelineFrames = _useRealtimeInterpolation ? _baseFrameCount : _frameCount;
        SetProcess(timelineFrames > 1 && _externalAnimationEnabled);
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

    private static void ApplyMaterials(ArrayMesh mesh, Material[] materials)
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
