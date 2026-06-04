using Godot;
using System;

public partial class FPSController : CharacterBody3D
{
	[Export] public float Speed = 8.0f;
	[Export] public float JumpVelocity = 6f;
	[Export] public float Sensitivity = 3f;

    public override void _Ready()
	{
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	public override void _PhysicsProcess(double delta)
	{
		Vector3 velocity = Velocity;

		if (!IsOnFloor()) velocity += GetGravity() * (float)delta;

		if (Input.IsActionJustPressed("player_jump") && IsOnFloor()) velocity.Y = JumpVelocity;

		Vector2 inputDir = Input.GetVector("player_left", "player_right", "player_forward", "player_backward");
		Vector3 direction = (Transform.Basis * new Vector3(inputDir.X, 0, inputDir.Y)).Normalized();
		if (direction != Vector3.Zero)
		{
			velocity.X = direction.X * Speed;
			velocity.Z = direction.Z * Speed;
		}
		else
		{
			velocity.X = Mathf.MoveToward(Velocity.X, 0, Speed);
			velocity.Z = Mathf.MoveToward(Velocity.Z, 0, Speed);
		}

		Velocity = velocity;
		MoveAndSlide();
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventMouseMotion motion)
		{
			RotateY(-motion.Relative.X / 1_000 * Sensitivity);
			Camera3D camera = GetNode<Camera3D>("Camera3D");
			camera.RotateX(-motion.Relative.Y / 1_000 * Sensitivity);
			camera.RotationDegrees = new Vector3(
				Mathf.Clamp(camera.RotationDegrees.X, -90, 90),
				camera.RotationDegrees.Y,
				camera.RotationDegrees.Z
			);
		}
	}
}
