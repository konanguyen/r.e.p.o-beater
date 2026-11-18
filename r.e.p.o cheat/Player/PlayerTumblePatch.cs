using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace r.e.p.o_cheat;

[HarmonyPatch(typeof(PlayerTumble))]
internal class PlayerTumblePatch
{
	public static bool Debounce;

	// Theo dõi chuyển tiếp tumble -> hết tumble của người chơi cục bộ để mở khoá grab ngay.
	private static bool _wasLocalTumbling;
	private static FieldInfo _localTumbleStateField;
	private static FieldInfo _localGrabberField;
	private static FieldInfo _localAvatarField;
	private static Type _cachedPcType;
	private static Type _cachedAvatarType;

	[HarmonyPrefix]
	[HarmonyPatch("Update")]
	public static void PreFixUpdate(PlayerTumble __instance, PhysGrabObject ___physGrabObject)
	{
		if (!((Object)(object)__instance == (Object)null) && !((Object)(object)___physGrabObject == (Object)null) && ___physGrabObject.grabbed && __instance.playerAvatar.photonView.IsMine && Hax2.debounce)
		{
			___physGrabObject.playerGrabbing.ForEach(delegate(PhysGrabber physGrabber)
			{
				physGrabber.photonView.RPC("ReleaseObjectRPC", (RpcTarget)0, new object[2] { true, 0.01f });
			});
		}

		TrackLocalTumbleEnd(__instance);
	}

	/// <summary>
	/// Khi người chơi cục bộ vừa kết thúc tumble (true -> false), xoá
	/// PhysGrabber.grabDisableTimer để nhặt được đồ ngay, không phải chờ cooldown.
	/// Hỗ trợ cả schema cũ (tumbleActive) lẫn v0.5.2+ (tumbleRequestIsTumbling).
	/// </summary>
	private static void TrackLocalTumbleEnd(PlayerTumble instance)
	{
		try
		{
			if ((Object)(object)instance == (Object)null || instance.playerAvatar == null || !instance.playerAvatar.photonView.IsMine)
			{
				return;
			}
			bool tumbling = IsTumbling(instance);
			if (_wasLocalTumbling && !tumbling)
			{
				RecoverLocalGrab();
			}
			_wasLocalTumbling = tumbling;
		}
		catch
		{
		}
	}

	private static bool IsTumbling(PlayerTumble instance)
	{
		Type type = typeof(PlayerTumble);
		if (_localTumbleStateField == null)
		{
			foreach (string name in new[] { "tumbleActive", "tumbleRequestIsTumbling", "tumbleOverride" })
			{
				_localTumbleStateField = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (_localTumbleStateField != null)
				{
					break;
				}
			}
		}
		if (_localTumbleStateField == null)
		{
			return false;
		}
		return _localTumbleStateField.GetValue(instance) is bool tumbling && tumbling;
	}

	/// <summary>
	/// Mở khoá grab của người chơi cục bộ: xoá PhysGrabber.grabDisableTimer.
	/// Gọi tự động khi tumble kết thúc và từ nút "Mở khoá nhặt đồ" trong menu.
	/// </summary>
	public static void RecoverLocalGrab()
	{
		try
		{
			object pc = PlayerReflectionCache.PlayerControllerInstance;
			if (pc == null)
			{
				return;
			}
			Type pcType = pc.GetType();
			if (_localAvatarField == null || pcType != _cachedPcType)
			{
				_localAvatarField = pcType.GetField("playerAvatarScript", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				_cachedPcType = pcType;
			}
			object avatar = _localAvatarField?.GetValue(pc);
			if (avatar == null)
			{
				return;
			}
			Type avatarType = avatar.GetType();
			if (_localGrabberField == null || avatarType != _cachedAvatarType)
			{
				_localGrabberField = avatarType.GetField("physGrabber", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				_cachedAvatarType = avatarType;
			}
			object grabber = _localGrabberField?.GetValue(avatar);
			if (grabber == null)
			{
				return;
			}
			FieldInfo timerField = grabber.GetType().GetField("grabDisableTimer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (timerField != null && timerField.GetValue(grabber) is float timer && timer > 0f)
			{
				timerField.SetValue(grabber, 0f);
				Debug.Log((object)"[PlayerTumblePatch] grabDisableTimer reset - grab unlocked after tumble");
			}
		}
		catch (Exception ex)
		{
			Debug.LogWarning((object)$"[PlayerTumblePatch] RecoverLocalGrab failed: {ex.Message}");
		}
	}
}
