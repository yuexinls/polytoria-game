// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Client;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Services;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mesh = Polytoria.Datamodel.Mesh;

namespace Polytoria.Renderer;

public partial class RendererViewport : SubViewport
{
	private const string EnvironmentScene = "res://scenes/renderer/env.tscn";
	public World Root = null!;
	public NetworkService NetworkService = null!;

	public RendererViewport()
	{
		World3D = new();
		TransparentBg = true;
		Msaa3D = Msaa.Msaa8X;
		Size = new(800, 800);
	}
	
	/// <summary>
	/// This replaces the previous constructor body; it's called once after adding this node
	/// to the tree
	/// </summary>
	public void Initialize()
	{
		Root = Globals.LoadInstance<World>();
		
		DatamodelBridge bridge = new() { Name = "DatamodelBridge" };
		AddChild(bridge, true);

		NetworkService networkService = new() { Name = "NetworkService" };
		NetworkService = networkService;

		Root.SessionType = World.SessionTypeEnum.Renderer;
		networkService.Attach(Root);
		networkService.IsServer = true;
		networkService.NetworkMode = NetworkService.NetworkModeEnum.Renderer;
		networkService.NetworkParent = Root;

		AddChild(Root.GDNode, true);
		Root.Root = Root;
		Root.World3D = World3D;
		Root.InitEntry();

		bridge.Attach(Root);
	}

	public void Setup()
	{
		Root.Setup();
		Root.Lighting.SunBrightness = 0;

		Node n = Globals.CreateInstanceFromScene<Node>(EnvironmentScene);
		Root.GDNode.AddChild(n);
		Root.World3D.Environment = n.GetNode<WorldEnvironment>("WorldEnvironment").Environment;
	}

	public async Task AddAvatar(int id, AvatarPhotoTypeEnum photoType = AvatarPhotoTypeEnum.FullAvatar)
	{
		NPC npc = Root.Insert.DefaultNPC();
		npc.Parent = Root.Environment;
		npc.UseNametag = false;
		npc.GDNode3D.RotationDegrees = new(0, 15, 0);
		
		PolytorianModel ptm = (PolytorianModel)npc.Character!;
		ptm.SetAnimationOverrideTo(true);
		
		AnimationPlayer ply = ptm.AnimTree.GetNode<AnimationPlayer>(ptm.AnimTree.AnimPlayer);
		PolytorianModel.AvatarLoadResponse loadRes = await ptm.InternalLoadAppearance(id, loadToolNpc: true);

		if (loadRes.HasTool)
		{
			ply.Play("ToolHoldR");
		}

		await ptm.WaitForAppearanceLoad();
		
		// Resolve camera after all awaits bc CurrentCamera may change during load
		Camera cam = Root.Environment.CurrentCamera!;
		Camera3D c3d = cam.Camera3D;

		switch (photoType)
		{
			case AvatarPhotoTypeEnum.FullAvatar:
				{
					c3d.GlobalPosition = new(0, 1.75f, 5);
					c3d.GlobalRotationDegrees = new(-15, 0, 0);
					break;
				}
			case AvatarPhotoTypeEnum.Headshot:
				{
					c3d.GlobalPosition = new(-0.05f, 1.7f, 2.5f);
					c3d.GlobalRotationDegrees = new(0, 0, 0);
					break;
				}
		}
	}

	public async Task AddAccessory(int id)
	{
		Accessory? accessory = await Root.Insert.AccessoryAsync(id);
		if (accessory == null) return;

		accessory.Parent = Root.Environment;
		
		List<Task> pendingLoads = [];
		foreach (Instance item in accessory.GetDescendants())
		{
			if (item is Mesh m && m.Loading)
				pendingLoads.Add(m.Loaded.Wait());
		}
		if (pendingLoads.Count > 0)
			await Task.WhenAll(pendingLoads);
		
		// same rationale as AddAvatar
		Camera cam = Root.Environment.CurrentCamera!;
		Camera3D c3d = cam.Camera3D;
		FocusToBounds(accessory.GDNode3D, c3d);
	}

	/// <summary>
	/// Capture the current viewport as a png byte array
	/// </summary>
	public async Task<byte[]> SavePng(CancellationToken ct = default)
	{
		RenderTargetClearMode = ClearMode.Once;
		RenderTargetUpdateMode = UpdateMode.Once;
		
		var _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw)
			.OnCompleted(() => _tcs.TrySetResult());
		Task frameSignal = _tcs.Task;
			
		if (ct.CanBeCanceled)
		{
			Task cancelled = Task.Delay(Timeout.Infinite, ct);
			Task completed = await Task.WhenAny(frameSignal, cancelled);
			if (completed == cancelled)
				ct.ThrowIfCancellationRequested();
		}
		else
		{
			await frameSignal;
		}
		
		Image img = GetTexture().GetImage();
		img.FixAlphaEdges();
		return img.SavePngToBuffer();
	}

	private static void FocusToBounds(
		Node3D target, 
		Camera3D cam, 
		float yawDeg = 20f, 
		float pitchDeg = -15f, 
		float padding = 0.01f, 
		Vector3? up = null)
	{
		if (target == null || cam == null)
		{
			GD.PushError("Target or camera is null.");
			return;
		}

		if (!TryGetWorldAabb(target, out Vector3 worldMin, out Vector3 worldMax))
			return;

		Vector3 upVec = up ?? Vector3.Up;
		Vector3 size = worldMax - worldMin;
		Vector3 center = worldMin + size * 0.5f;
		float radius = size.Length() * 0.5f;

		float vFov = Mathf.DegToRad(cam.Fov);
		float aspect = 1.0f;
		Viewport? vp = cam.GetViewport();

		if (vp != null)
		{
			Rect2 r = vp.GetVisibleRect();
			if (r.Size.Y != 0f)
				aspect = r.Size.X / r.Size.Y;
		}

		float hFov = 2f * Mathf.Atan(Mathf.Tan(vFov * 0.5f) * aspect);
		float halfMinFov = Mathf.Min(vFov, hFov) * 0.5f;
		float paddedR = radius * (1f + MathF.Max(0f, padding));
		float distance = paddedR / MathF.Tan(MathF.Max(0.001f, halfMinFov));

		float yaw = Mathf.DegToRad(yawDeg);
		float pitch = Mathf.DegToRad(pitchDeg);

		Basis yawB = new(upVec, yaw);
		Vector3 right = yawB * Vector3.Right;
		Basis pitchB = new(right, pitch);
		Basis viewBasis = pitchB * yawB;

		Vector3 forward = -viewBasis.Z;
		Vector3 camPos = center - forward * distance;
		
		cam.GlobalTransform = new Transform3D(Basis.Identity, camPos).LookingAt(center, upVec);

		cam.Near = MathF.Max(0.01f, distance - paddedR * 2f);
		cam.Far = MathF.Max(cam.Near + 1f, distance + paddedR * 4f);
	}

	private static bool TryGetWorldAabb(Node3D root, out Vector3 min, out Vector3 max)
	{
		min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
		max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
		bool found = false;

		// Stack reuse could be added here if this becomes hot
		Stack<Node> stack = new();
		stack.Push(root);

		while (stack.Count > 0)
		{
			Node n = stack.Pop();
			
			// GetChild(i) doesn't allocate
			int childCount = n.GetChildCount();
			for (int i = 0; i < childCount; i++)
				stack.Push(n.GetChild(i));

			if (n is MeshInstance3D mi && mi.Mesh != null)
			{
				var a = mi.Mesh.GetAabb();
				EncapsulateTransformedAabb(ref min, ref max, a, mi.GlobalTransform);
				found = true;
			}
		}
		return found;
	}

	private static void EncapsulateTransformedAabb(
		ref Vector3 min, 
		ref Vector3 max, 
		Aabb local, 
		Transform3D xf)
	{
		// World-space center of the aabb
		Vector3 worldCenter = xf * (local.Position + local.Size * 0.5f);
		
		// Half size in local space
		Vector3 h = local.Size * 0.5f;
		
		// We compute each world-axis half-extent by summing absolute dot
		// products with the basis vectors. It's the same as transforming all
		// corners but it's faster with only three dot products per axis.
		Vector3 hx = xf.Basis.X * h.X;
		Vector3 hy = xf.Basis.Y * h.Y;
		Vector3 hz = xf.Basis.Z * h.Z;
		
		Vector3 worldHalf = new(
			MathF.Abs(hx.X) + MathF.Abs(hy.X) + MathF.Abs(hz.X),
			MathF.Abs(hx.Y) + MathF.Abs(hy.Y) + MathF.Abs(hz.Y),
			MathF.Abs(hx.Z) + MathF.Abs(hy.Z) + MathF.Abs(hz.Z)
		);
		
		Vector3 wMin = worldCenter - worldHalf;
		Vector3 wMax = worldCenter + worldHalf;
		
		if (wMin.X < min.X) min.X = wMin.X;
		if (wMin.Y < min.Y) min.Y = wMin.Y;
		if (wMin.Z < min.Z) min.Z = wMin.Z;
		if (wMax.X > max.X) max.X = wMax.X;
		if (wMax.Y > max.Y) max.Y = wMax.Y;
		if (wMax.Z > max.Z) max.Z = wMax.Z;
	}

	public enum AvatarPhotoTypeEnum
	{
		FullAvatar,
		Headshot
	}
}
