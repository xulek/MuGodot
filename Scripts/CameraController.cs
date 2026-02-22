using Godot;

namespace MuGodot;

[Tool]
public partial class CameraController : Node3D
{
	private const float CameraNear = MuConfig.WorldToGodot * 10f;
	private const float CameraFar = MuConfig.WorldToGodot * 3600f;
	private static readonly float DefaultYaw = Mathf.DegToRad(-41.99f);
	private static readonly float DefaultPitch = Mathf.DegToRad(135.87f);
	private const float MinPitch = 110f * Mathf.Pi / 180f;
	private const float MaxPitch = 160f * Mathf.Pi / 180f;
	private static readonly float DefaultDistance = MuConfig.WorldToGodot * 1700f;
	private static readonly float MinDistance = MuConfig.WorldToGodot * 800f;
	private static readonly float MaxDistance = MuConfig.WorldToGodot * 1800f;

	[ExportGroup("MonoGame Camera")]
	[Export] public float CameraMouseSensitivity { get; set; } = 0.003f;
	[Export] public float CameraZoomSpeed { get; set; } = 4f;
	[Export] public float CameraMinDistance { get; set; } = MinDistance;
	[Export] public float CameraMaxDistance { get; set; } = MaxDistance;
	[Export] public float CameraDefaultDistance { get; set; } = DefaultDistance;
	[Export] public float CameraDefaultYaw { get; set; } = DefaultYaw;
	[Export] public float CameraDefaultPitch { get; set; } = DefaultPitch;
	[Export] public float CameraFollowSmoothness { get; set; } = 14f;

	private Camera3D _camera = null!;
	private float _yaw = DefaultYaw;
	private float _pitch = DefaultPitch;
	private float _distance = DefaultDistance;
	private float _targetDistance = DefaultDistance;
	private bool _rotatePressed;
	private bool _wasRotated;
	private Vector3 _smoothedTarget;
	private bool _smoothedTargetInitialized;
	private Vector2 _previousMousePosition;
	private Func<Vector3>? _getTarget;

	public Camera3D Camera => _camera;

	public void Initialize(Func<Vector3> getTarget)
	{
		_getTarget = getTarget;

		_camera = GetNodeOrNull<Camera3D>("Camera3D");
		if (_camera == null)
		{
			_camera = new Camera3D { Name = "Camera3D" };
			AddChild(_camera);
		}

		_camera.Near = CameraNear;
		_camera.Far = CameraFar;
		_camera.Fov = 35f;
		_camera.Current = true;

		ResetToDefaults();
		_camera.Position = getTarget() + new Vector3(8f, 11f, 9f);
		_previousMousePosition = GetViewport().GetMousePosition();
	}

	public void Update(double delta)
	{
		if (_camera == null || !GodotObject.IsInstanceValid(_camera) || _getTarget == null)
			return;

		_targetDistance = Mathf.Clamp(
			_targetDistance,
			Mathf.Min(CameraMinDistance, CameraMaxDistance),
			Mathf.Max(CameraMinDistance, CameraMaxDistance));
		_distance = Mathf.Lerp(
			_distance,
			_targetDistance,
			Mathf.Clamp(CameraZoomSpeed, 0.01f, 30f) * (float)delta);
		_distance = Mathf.Clamp(
			_distance,
			Mathf.Min(CameraMinDistance, CameraMaxDistance),
			Mathf.Max(CameraMinDistance, CameraMaxDistance));

		Vector3 targetRaw = _getTarget();
		if (!_smoothedTargetInitialized)
		{
			_smoothedTarget = targetRaw;
			_smoothedTargetInitialized = true;
		}

		float smoothness = MathF.Max(0f, CameraFollowSmoothness);
		if (smoothness <= 0.001f)
			_smoothedTarget = targetRaw;
		else
		{
			float t = 1f - MathF.Exp(-smoothness * (float)delta);
			_smoothedTarget = _smoothedTarget.Lerp(targetRaw, Mathf.Clamp(t, 0f, 1f));
		}

		Vector3 offset = ComputeOffset(_distance, _yaw, _pitch);
		_camera.GlobalPosition = _smoothedTarget + offset;
		_camera.LookAt(_smoothedTarget, Vector3.Up);
	}

	public override void _Input(InputEvent @event)
	{
		if (Engine.IsEditorHint())
			return;

		if (@event is InputEventMouseButton mouseButton)
		{
			if (mouseButton.ButtonIndex == MouseButton.Middle)
			{
				if (mouseButton.Pressed)
				{
					_rotatePressed = true;
					_wasRotated = false;
					_previousMousePosition = mouseButton.Position;
				}
				else if (_rotatePressed)
				{
					if (!_wasRotated)
						ResetToDefaults();

					_rotatePressed = false;
					_wasRotated = false;
				}
			}

			if (!mouseButton.Pressed)
				return;

			float zoomStep = 100f * MuConfig.WorldToGodot;
			float minD = Mathf.Min(CameraMinDistance, CameraMaxDistance);
			float maxD = Mathf.Max(CameraMinDistance, CameraMaxDistance);

			if (mouseButton.ButtonIndex == MouseButton.WheelUp)
				_targetDistance = Mathf.Clamp(_targetDistance - zoomStep, minD, maxD);
			else if (mouseButton.ButtonIndex == MouseButton.WheelDown)
				_targetDistance = Mathf.Clamp(_targetDistance + zoomStep, minD, maxD);
		}

		if (@event is InputEventMouseMotion mouseMotion)
		{
			if (_rotatePressed)
			{
				var delta = mouseMotion.Position - _previousMousePosition;
				_previousMousePosition = mouseMotion.Position;

				if (delta.LengthSquared() > 0f)
				{
					_yaw -= delta.X * CameraMouseSensitivity;
					_pitch = Mathf.Clamp(_pitch - delta.Y * CameraMouseSensitivity, MinPitch, MaxPitch);
					_yaw = Mathf.Wrap(_yaw, -Mathf.Pi, Mathf.Pi);
					_wasRotated = true;
				}
			}
			else
			{
				_previousMousePosition = mouseMotion.Position;
			}
		}
	}

	/// <summary>Returns the camera's current global position, or Vector3.Zero if invalid.</summary>
	public Vector3 GetPosition()
	{
		if (_camera != null && GodotObject.IsInstanceValid(_camera))
			return _camera.GlobalPosition;
		return Vector3.Zero;
	}

	/// <summary>Returns the editor camera position in editor mode, game camera otherwise.</summary>
	public Vector3 GetEditorAwarePosition()
	{
		if (Engine.IsEditorHint())
		{
			var editorCamera = GetViewport()?.GetCamera3D();
			if (editorCamera != null && GodotObject.IsInstanceValid(editorCamera))
				return editorCamera.GlobalPosition;
		}

		return GetPosition();
	}

	public void ResetAndUpdate()
	{
		ResetToDefaults();
		Update(0d);
	}

	private void ResetToDefaults()
	{
		_yaw = CameraDefaultYaw;
		_pitch = Mathf.Clamp(CameraDefaultPitch, MinPitch, MaxPitch);
		_distance = CameraDefaultDistance;
		_targetDistance = CameraDefaultDistance;
		_smoothedTargetInitialized = false;
	}

	private static Vector3 ComputeOffset(float distance, float yaw, float pitch)
	{
		float x = distance * MathF.Cos(pitch) * MathF.Sin(yaw);
		float y = distance * MathF.Cos(pitch) * MathF.Cos(yaw);
		float z = distance * MathF.Sin(pitch);
		return new Vector3(x, z, -y);
	}
}
