using Godot;
using System;

public partial class Health : Node
{
	[Export]
	public int health = 100; // different kits = different health
	[Export]
	public int maxHealth = 100; // different kits = different health
	[Export]
	public int regenRate = 1; // life steal kits will have 0 regenRate, tank has high regen rate, ranged kits have low regen rate, etc.

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		// no idea
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
		// regen if out of combat
	}
}
