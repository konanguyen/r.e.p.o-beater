using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace r.e.p.o_cheat;

/// <summary>
/// 码放撤离点（移植自 D4rkks/r.e.p.o-cheat 的 ItemPacker / ExtractionZone / PackSupervisor，
/// 适配本分支：物品移动统一走游戏自身 PhysGrabObject.Teleport 保持同步）。
/// 高度图式 3D 码放：每件物品放平（最小轴朝上），落在网格上支撑充足、位置最低的格子。
/// </summary>
public static class ExtractionPacker
{
	private const float Cell = 0.05f;
	private const float Gap = 0.04f;
	private const float DropHeight = 0.05f;
	private const float MinSupport = 0.6f;
	private const float SupportTolerance = 0.03f;
	private const float MaxItemSize = 3f;

	internal class Box
	{
		public ItemTeleport.GameItem Item;
		public Transform Transform;
		public Vector3 LocalCenter; // 物品自身旋转坐标系下的中心（世界单位）
		public Vector3 LocalSize;
		public float Volume => LocalSize.x * LocalSize.y * LocalSize.z;
	}

	internal class Zone
	{
		public Vector3 Center; // 地面高度
		public Quaternion Rotation;
		public float Width, Depth;
		public int Nx, Nz;
		public float[,] Heights;

		public Zone(Vector3 center, Quaternion rotation, float width, float depth)
		{
			Center = center;
			Rotation = rotation;
			Width = width;
			Depth = depth;
			Nx = Mathf.Max(1, Mathf.FloorToInt(width / Cell));
			Nz = Mathf.Max(1, Mathf.FloorToInt(depth / Cell));
			Heights = new float[Nx, Nz];
		}

		public Vector3 ToZone(Vector3 world) => Quaternion.Inverse(Rotation) * (world - Center);
		public Vector3 ToWorld(Vector3 local) => Center + Rotation * local;
	}

	/// <summary>把所有可选物品码放到撤离点（菜单按钮入口）。</summary>
	public static void PackAllToExtraction()
	{
		ExtractionPoint target = ExtractionZone.TargetPoint;
		if ((Object)(object)target == (Object)null)
		{
			ToastNotification.ShowWarning(L.T("pack.no_extraction"));
			return;
		}
		List<ItemTeleport.GameItem> items = ItemTeleport.GetItemList().FindAll(x => x.ItemObject != null);
		if (items.Count == 0)
		{
			ToastNotification.ShowInfo(L.T("pack.no_items"));
			return;
		}
		ExtractionZone.GetZone(target, out Vector3 center, out Quaternion rotation);
		int count = Pack(items, center, rotation, ExtractionZone.sizeX, ExtractionZone.sizeZ);
		ToastNotification.ShowInfo(L.T("pack.done_fmt", count));
	}

	public static int Pack(List<ItemTeleport.GameItem> items, Vector3 center, Quaternion rotation, float width, float depth)
	{
		List<Box> boxes = new List<Box>();
		foreach (ItemTeleport.GameItem item in items)
		{
			Transform t = ResolveTransform(item);
			if ((Object)(object)t == (Object)null)
			{
				continue;
			}
			boxes.Add(Measure(item, t));
		}

		// 大件先放：构成底层，小件填缝并堆顶
		boxes.Sort((a, b) => b.Volume.CompareTo(a.Volume));

		Zone zone = new Zone(center, rotation, width, depth);
		foreach (Box box in boxes)
		{
			Place(box, zone);
		}

		float maxHeight = 0f;
		foreach (float h in zone.Heights)
		{
			maxHeight = Mathf.Max(maxHeight, h);
		}
		Debug.Log((object)$"[Pack] Packed {boxes.Count} items, stack height {maxHeight:F2}m");

		new GameObject("PackSupervisor").AddComponent<PackSupervisor>().Init(boxes, zone);
		return boxes.Count;
	}

	internal static void Place(Box box, Zone zone)
	{
		// 放平：最小局部轴朝上
		Vector3 s = box.LocalSize;
		Vector3 vertical = (s.x <= s.y && s.x <= s.z) ? Vector3.right : ((s.z < s.y) ? Vector3.forward : Vector3.up);
		Quaternion flat = Quaternion.FromToRotation(vertical, Vector3.up);

		bool found = false;
		int bestX = 0, bestZ = 0, bestFx = 0, bestFz = 0;
		float bestBase = float.MaxValue;
		Quaternion bestOrientation = flat;
		Vector3 bestDims = Vector3.zero;

		for (int pass = 0; pass < 2 && !found; pass++)
		{
			bool requireSupport = pass == 0;
			foreach (float yaw in new float[2] { 0f, 90f })
			{
				Quaternion orientation = Quaternion.Euler(0f, yaw, 0f) * flat;
				Vector3 dims = Abs(orientation * s);
				int fx = Mathf.CeilToInt((dims.x + Gap) / Cell);
				int fz = Mathf.CeilToInt((dims.z + Gap) / Cell);
				if (fx > zone.Nx || fz > zone.Nz)
				{
					continue;
				}

				for (int iz = 0; iz <= zone.Nz - fz; iz++)
				{
					for (int ix = 0; ix <= zone.Nx - fx; ix++)
					{
						float baseHeight = FootprintMax(zone, ix, iz, fx, fz);
						if (baseHeight >= bestBase)
						{
							continue;
						}
						if (requireSupport && baseHeight > 0.001f && Support(zone, ix, iz, fx, fz, baseHeight) < MinSupport)
						{
							continue;
						}

						found = true;
						bestBase = baseHeight;
						bestX = ix;
						bestZ = iz;
						bestFx = fx;
						bestFz = fz;
						bestOrientation = orientation;
						bestDims = dims;
					}
				}
			}
		}

		if (!found)
		{
			// 比区域还大：居中放在其余物品上方
			bestOrientation = flat;
			bestDims = Abs(flat * s);
			bestFx = Mathf.Min(zone.Nx, Mathf.CeilToInt((bestDims.x + Gap) / Cell));
			bestFz = Mathf.Min(zone.Nz, Mathf.CeilToInt((bestDims.z + Gap) / Cell));
			bestX = (zone.Nx - bestFx) / 2;
			bestZ = (zone.Nz - bestFz) / 2;
			bestBase = FootprintMax(zone, bestX, bestZ, bestFx, bestFz);
		}

		float top = bestBase + bestDims.y + Gap / 2f;
		for (int x = bestX; x < bestX + bestFx; x++)
		{
			for (int z = bestZ; z < bestZ + bestFz; z++)
			{
				zone.Heights[x, z] = top;
			}
		}

		Vector3 local = new Vector3(
			(bestX + bestFx / 2f) * Cell - zone.Nx * Cell / 2f,
			bestBase + bestDims.y / 2f + DropHeight,
			(bestZ + bestFz / 2f) * Cell - zone.Nz * Cell / 2f);

		Quaternion worldRotation = zone.Rotation * bestOrientation;
		Vector3 rootPosition = zone.ToWorld(local) - worldRotation * box.LocalCenter;
		MoveItem(box.Item, box.Transform, rootPosition, worldRotation);
	}

	/// <summary>把已经躺在区域内的物品按实际姿态栅格化进高度图。</summary>
	internal static void Stamp(Box box, Zone zone)
	{
		Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
		foreach (Vector3 corner in Corners(box.LocalCenter, box.LocalSize))
		{
			Vector3 p = zone.ToZone(box.Transform.position + box.Transform.rotation * corner);
			min = Vector3.Min(min, p);
			max = Vector3.Max(max, p);
		}

		int x0 = Mathf.Clamp(Mathf.FloorToInt((min.x + zone.Nx * Cell / 2f) / Cell), 0, zone.Nx - 1);
		int x1 = Mathf.Clamp(Mathf.CeilToInt((max.x + zone.Nx * Cell / 2f) / Cell), 0, zone.Nx);
		int z0 = Mathf.Clamp(Mathf.FloorToInt((min.z + zone.Nz * Cell / 2f) / Cell), 0, zone.Nz - 1);
		int z1 = Mathf.Clamp(Mathf.CeilToInt((max.z + zone.Nz * Cell / 2f) / Cell), 0, zone.Nz);
		for (int x = x0; x < x1; x++)
		{
			for (int z = z0; z < z1; z++)
			{
				zone.Heights[x, z] = Mathf.Max(zone.Heights[x, z], max.y + Gap / 2f);
			}
		}
	}

	internal static bool IsOutside(Box box, Zone zone)
	{
		Vector3 c = zone.ToZone(box.Transform.position + box.Transform.rotation * box.LocalCenter);
		return Mathf.Abs(c.x) > zone.Width / 2f + 0.1f || Mathf.Abs(c.z) > zone.Depth / 2f + 0.1f || c.y < -0.5f;
	}

	private static float FootprintMax(Zone zone, int ix, int iz, int fx, int fz)
	{
		float max = 0f;
		for (int x = ix; x < ix + fx; x++)
		{
			for (int z = iz; z < iz + fz; z++)
			{
				if (zone.Heights[x, z] > max)
				{
					max = zone.Heights[x, z];
				}
			}
		}
		return max;
	}

	private static float Support(Zone zone, int ix, int iz, int fx, int fz, float baseHeight)
	{
		int supported = 0;
		for (int x = ix; x < ix + fx; x++)
		{
			for (int z = iz; z < iz + fz; z++)
			{
				if (zone.Heights[x, z] >= baseHeight - SupportTolerance)
				{
					supported++;
				}
			}
		}
		return (float)supported / (fx * fz);
	}

	private static Box Measure(ItemTeleport.GameItem item, Transform t)
	{
		Rigidbody rb = t.GetComponent<Rigidbody>() ?? t.GetComponentInParent<Rigidbody>();
		Quaternion inv = Quaternion.Inverse(t.rotation);
		Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
		bool found = false;

		void Add(Vector3 world)
		{
			Vector3 p = inv * (world - t.position);
			min = Vector3.Min(min, p);
			max = Vector3.Max(max, p);
			found = true;
		}

		Collider[] colliders = t.GetComponentsInChildren<Collider>();
		foreach (Collider col in colliders)
		{
			if (!col.enabled || col.isTrigger)
			{
				continue;
			}
			if ((Object)(object)rb != (Object)null && col.attachedRigidbody != rb)
			{
				continue;
			}

			if (TryGetColliderLocalBox(col, out Vector3 c, out Vector3 size))
			{
				foreach (Vector3 corner in Corners(c, size))
				{
					Add(col.transform.TransformPoint(corner));
				}
			}
			else
			{
				Bounds b = col.bounds;
				foreach (Vector3 corner in Corners(b.center, b.size))
				{
					Add(corner);
				}
			}
		}

		if (!found)
		{
			Renderer[] renderers = t.GetComponentsInChildren<Renderer>();
			foreach (Renderer r in renderers)
			{
				if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer))
				{
					continue;
				}
				Bounds b = r.bounds;
				foreach (Vector3 corner in Corners(b.center, b.size))
				{
					Add(corner);
				}
			}
		}

		Vector3 center = found ? (min + max) / 2f : Vector3.zero;
		Vector3 extent = found ? max - min : Vector3.one * 0.5f;
		if (extent.x > MaxItemSize || extent.y > MaxItemSize || extent.z > MaxItemSize)
		{
			Debug.Log((object)$"[Pack] Item '{t.name}' has suspicious size {extent}, clamping to {MaxItemSize}m");
			extent = Vector3.Min(extent, Vector3.one * MaxItemSize);
		}

		return new Box
		{
			Item = item,
			Transform = t,
			LocalCenter = center,
			LocalSize = extent
		};
	}

	private static bool TryGetColliderLocalBox(Collider col, out Vector3 center, out Vector3 size)
	{
		switch (col)
		{
			case BoxCollider box:
				center = box.center;
				size = box.size;
				return true;
			case SphereCollider sphere:
				center = sphere.center;
				size = Vector3.one * sphere.radius * 2f;
				return true;
			case CapsuleCollider capsule:
				center = capsule.center;
				size = Vector3.one * capsule.radius * 2f;
				size[capsule.direction] = Mathf.Max(capsule.height, capsule.radius * 2f);
				return true;
			case MeshCollider mesh when mesh.sharedMesh != null:
				center = mesh.sharedMesh.bounds.center;
				size = mesh.sharedMesh.bounds.size;
				return true;
			default:
				center = (size = Vector3.zero);
				return false;
		}
	}

	private static IEnumerable<Vector3> Corners(Vector3 center, Vector3 size)
	{
		Vector3 e = size / 2f;
		for (int i = 0; i < 8; i++)
		{
			yield return center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
		}
	}

	private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

	private static Transform ResolveTransform(ItemTeleport.GameItem item)
	{
		if (item?.ItemObject == null)
		{
			return null;
		}
		if (item.ItemObject is Component component)
		{
			return component.transform;
		}
		if (item.ItemObject is GameObject go)
		{
			return go.transform;
		}
		return null;
	}

	/// <summary>
	/// 码放专用移动：所有权转移 + 关闭撞击检测 + 走游戏 PhysGrabObject.Teleport（多人同步），
	/// 不做贴地吸附，保证高度图堆叠位置不被覆盖。
	/// </summary>
	internal static void MoveItem(ItemTeleport.GameItem item, Transform itemTransform, Vector3 targetPosition, Quaternion targetRotation)
	{
		try
		{
			if ((Object)(object)itemTransform == (Object)null)
			{
				return;
			}
			Component source = item.ItemObject as Component;
			if ((Object)(object)source == (Object)null && item.ItemObject is GameObject go)
			{
				source = go.transform;
			}
			if ((Object)(object)source == (Object)null)
			{
				return;
			}

			// 死亡头颅需要先 Trigger 才能被物理搬运
			PlayerDeathHead head = source as PlayerDeathHead ?? source.GetComponent<PlayerDeathHead>() ?? source.GetComponentInParent<PlayerDeathHead>();
			if ((Object)(object)head != (Object)null)
			{
				ItemTeleport.PrepareDeathHead(source);
			}

			PhysGrabObject pgo = ItemTeleport.ResolvePhysGrabObject(source);
			if (PhotonNetwork.IsConnected)
			{
				PhotonView pv = source.GetComponentInParent<PhotonView>();
				if ((Object)(object)pv != (Object)null && !pv.IsMine)
				{
					pv.RequestOwnership();
				}
			}

			if ((Object)(object)pgo != (Object)null)
			{
				PhysGrabObjectImpactDetector impact = pgo.GetComponent<PhysGrabObjectImpactDetector>();
				if ((Object)(object)impact != (Object)null)
				{
					impact.ImpactDisable(8f);
				}
				Rigidbody rb = pgo.GetComponent<Rigidbody>() ?? pgo.GetComponentInChildren<Rigidbody>();
				if ((Object)(object)rb != (Object)null)
				{
					rb.isKinematic = false;
					rb.velocity = Vector3.zero;
					rb.angularVelocity = Vector3.zero;
				}
				pgo.Teleport(targetPosition, targetRotation);
			}
			else
			{
				itemTransform.SetPositionAndRotation(targetPosition, targetRotation);
			}
		}
		catch (Exception ex)
		{
			Debug.LogWarning((object)$"[Pack] MoveItem '{item?.Name}': {ex.Message}");
		}
	}
}

/// <summary>物理落定后把滚出/掉出区域的物品重新码放。</summary>
public class PackSupervisor : MonoBehaviour
{
	private static readonly float[] CheckTimes = { 2f, 4f };

	private List<ExtractionPacker.Box> boxes;
	private ExtractionPacker.Zone zone;
	private float startTime;
	private int nextCheck;

	internal void Init(List<ExtractionPacker.Box> packedBoxes, ExtractionPacker.Zone packZone)
	{
		boxes = packedBoxes;
		zone = packZone;
		startTime = Time.time;
	}

	private void Update()
	{
		if (boxes == null || nextCheck >= CheckTimes.Length)
		{
			Destroy(gameObject);
			return;
		}
		if (Time.time - startTime < CheckTimes[nextCheck])
		{
			return;
		}
		nextCheck++;

		boxes.RemoveAll(b => b.Transform == null);
		List<ExtractionPacker.Box> outside = boxes.FindAll(b => ExtractionPacker.IsOutside(b, zone));
		if (outside.Count == 0)
		{
			Destroy(gameObject);
			return;
		}

		ExtractionPacker.Zone fresh = new ExtractionPacker.Zone(zone.Center, zone.Rotation, zone.Width, zone.Depth);
		foreach (ExtractionPacker.Box b in boxes)
		{
			if (!outside.Contains(b))
			{
				ExtractionPacker.Stamp(b, fresh);
			}
		}

		outside.Sort((a, b) => b.Volume.CompareTo(a.Volume));
		foreach (ExtractionPacker.Box b in outside)
		{
			ExtractionPacker.Place(b, fresh);
		}
		zone = fresh;
		ToastNotification.ShowInfo(L.T("pack.replaced_fmt", outside.Count));
	}
}

/// <summary>
/// 以撤离点为基准的码放区域：在其仅偏航（yaw-only）局部坐标系内定义偏移/尺寸/附加偏航，可校准。
/// </summary>
public static class ExtractionZone
{
	// 在撤离平台上实测的默认值
	private const float DefaultOffsetX = 0f, DefaultOffsetY = 0.15f, DefaultOffsetZ = 0f;
	private const float DefaultSizeX = 3.3f, DefaultSizeZ = 2.9f, DefaultYaw = 0f;

	private static readonly FieldInfo CurrentStateField = AccessTools.Field(typeof(ExtractionPoint), "currentState");

	public static bool showZone = false;
	public static float offsetX = DefaultOffsetX, offsetY = DefaultOffsetY, offsetZ = DefaultOffsetZ;
	public static float sizeX = DefaultSizeX, sizeZ = DefaultSizeZ, yaw = DefaultYaw;

	private static ExtractionPoint cachedTarget;
	private static float lastTargetLookup = -10f;

	/// <summary>当前撤离点，否则取最近一个开放的撤离点（与 ItemTeleport.TryGetOpenExtractionDrop 同一选择规则）。</summary>
	public static ExtractionPoint TargetPoint
	{
		get
		{
			if ((Object)(object)cachedTarget != (Object)null && Time.time - lastTargetLookup < 1f)
			{
				return cachedTarget;
			}
			cachedTarget = FindTargetPoint();
			lastTargetLookup = Time.time;
			return cachedTarget;
		}
	}

	private static ExtractionPoint FindTargetPoint()
	{
		try
		{
			RoundDirector rd = RoundDirector.instance;
			ExtractionPoint current = CurrentStateField != null && rd != null
				? AccessTools.Field(typeof(RoundDirector), "extractionPointCurrent")?.GetValue(rd) as ExtractionPoint
				: null;
			if ((Object)(object)current != (Object)null && ((Component)current).gameObject.activeInHierarchy && GetState(current) != "Complete")
			{
				return current;
			}
		}
		catch
		{
		}

		GameObject localPlayer = DebugCheats.GetLocalPlayer();
		Vector3 playerPos = (Object)(object)localPlayer != (Object)null ? localPlayer.transform.position : Vector3.zero;
		ExtractionPoint bestOpen = null;
		ExtractionPoint bestReady = null;
		float bestOpenDist = float.MaxValue;
		float bestReadyDist = float.MaxValue;
		ExtractionPoint[] points = SceneCache.GetObjects<ExtractionPoint>(0.5f);
		if (points == null)
		{
			return null;
		}
		foreach (ExtractionPoint point in points)
		{
			if ((Object)(object)point == (Object)null || !((Component)point).gameObject.activeInHierarchy)
			{
				continue;
			}
			string state = GetState(point);
			float dist = Vector3.Distance(playerPos, ((Component)point).transform.position);
			if (state == "Active" || state == "Success" || state == "Surplus" || state == "Warning" || state == "Extracting" || state == "TaxReturn")
			{
				if (dist < bestOpenDist)
				{
					bestOpenDist = dist;
					bestOpen = point;
				}
			}
			else if (state == "Idle" && !point.isLocked && dist < bestReadyDist)
			{
				bestReadyDist = dist;
				bestReady = point;
			}
		}
		return (Object)(object)bestOpen != (Object)null ? bestOpen : bestReady;
	}

	private static string GetState(ExtractionPoint point)
	{
		try
		{
			return CurrentStateField?.GetValue(point)?.ToString() ?? "";
		}
		catch
		{
			return "";
		}
	}

	private static Transform PoseAnchor(ExtractionPoint point)
	{
		return (Object)(object)point.platform != (Object)null ? point.platform : ((Component)point).transform;
	}

	public static void GetZone(ExtractionPoint point, out Vector3 center, out Quaternion rotation)
	{
		Transform anchor = PoseAnchor(point);
		Quaternion baseRot = Quaternion.Euler(0f, anchor.eulerAngles.y, 0f);
		center = anchor.position + baseRot * new Vector3(offsetX, offsetY, offsetZ);
		rotation = baseRot * Quaternion.Euler(0f, yaw, 0f);
	}

	public static void ResetToDefaults()
	{
		offsetX = DefaultOffsetX;
		offsetY = DefaultOffsetY;
		offsetZ = DefaultOffsetZ;
		sizeX = DefaultSizeX;
		sizeZ = DefaultSizeZ;
		yaw = DefaultYaw;
	}

	/// <summary>码放区域线框叠加：绿框 + 红色前沿(+Z) + 原点/区域中心标记。在 Hax2.OnGUI 中调用。</summary>
	public static void DrawZoneEsp()
	{
		if (!showZone)
		{
			return;
		}
		ExtractionPoint point = TargetPoint;
		Camera cam = Camera.main;
		if ((Object)(object)point == (Object)null || (Object)(object)cam == (Object)null)
		{
			return;
		}

		GetZone(point, out Vector3 center, out Quaternion rot);
		Vector3 r = rot * Vector3.right * (sizeX / 2f);
		Vector3 f = rot * Vector3.forward * (sizeZ / 2f);

		Vector3 backLeft = center - r - f;
		Vector3 backRight = center + r - f;
		Vector3 frontRight = center + r + f;
		Vector3 frontLeft = center - r + f;

		DrawWorldLine(cam, backLeft, backRight, Color.green);
		DrawWorldLine(cam, backRight, frontRight, Color.green);
		DrawWorldLine(cam, frontLeft, backLeft, Color.green);
		DrawWorldLine(cam, frontRight, frontLeft, Color.red); // +Z 前沿

		DrawWorldPoint(cam, ((Component)point).transform.position, Color.magenta, "origin");
		DrawWorldPoint(cam, center, Color.cyan, "zone");
	}

	private static bool ToScreen(Camera cam, Vector3 world, out Vector2 screen)
	{
		Vector3 sp = cam.WorldToScreenPoint(world);
		float scaleX = (float)Screen.width / cam.pixelWidth;
		float scaleY = (float)Screen.height / cam.pixelHeight;
		screen = new Vector2(sp.x * scaleX, Screen.height - sp.y * scaleY);
		return sp.z > 0f;
	}

	private static void DrawWorldLine(Camera cam, Vector3 a, Vector3 b, Color color)
	{
		if (!ToScreen(cam, a, out Vector2 sa) || !ToScreen(cam, b, out Vector2 sb))
		{
			return;
		}

		float length = Vector2.Distance(sa, sb);
		float angle = Mathf.Atan2(sb.y - sa.y, sb.x - sa.x) * Mathf.Rad2Deg;
		Matrix4x4 matrix = GUI.matrix;
		GUI.color = color;
		GUIUtility.RotateAroundPivot(angle, sa);
		GUI.DrawTexture(new Rect(sa.x, sa.y - 1f, length, 2f), Texture2D.whiteTexture);
		GUI.matrix = matrix;
		GUI.color = Color.white;
	}

	private static void DrawWorldPoint(Camera cam, Vector3 world, Color color, string label)
	{
		if (!ToScreen(cam, world, out Vector2 s))
		{
			return;
		}
		GUI.color = color;
		GUI.DrawTexture(new Rect(s.x - 4f, s.y - 4f, 8f, 8f), Texture2D.whiteTexture);
		GUI.Label(new Rect(s.x + 6f, s.y - 10f, 100f, 20f), label);
		GUI.color = Color.white;
	}
}
