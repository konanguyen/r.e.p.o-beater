using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using r.e.p.o_cheat.Compatibility;

namespace r.e.p.o_cheat;

/// <summary>
/// Reflection-based access helpers for game members that changed between the build
/// baseline (v0.4.4.3) and the running game (e.g. v0.5.2 renamed SteamManager's
/// singleton and added a third OverrideSpeed parameter).
/// Every lookup is cached; failures are reported ONCE and then stay silent, so a
/// game update can never spam the player log or freeze the frame loop.
/// </summary>
public static class GameApiSafe
{
	private static SteamManager _steam;
	private static float _steamNextScan;

	private static readonly Dictionary<string, MethodInfo> MethodCache = new Dictionary<string, MethodInfo>();
	private static readonly HashSet<string> ReportedMissing = new HashSet<string>();

	/// <summary>Báo lỗi một member game bị thiếu đúng MỘT lần duy nhất.</summary>
	public static void ReportMissing(string member)
	{
		if (ReportedMissing.Add(member))
		{
			Debug.LogWarning((object)$"[GameApiSafe] Game member '{member}' not found on this game version - feature disabled gracefully (reported once). " +
				"Game is newer than the build baseline (" + GameVersionInfo.KnownGameVersion + ").");
		}
	}

	/// <summary>
	/// SteamManager singleton. v0.5.2 removed the static `instance` field, so the
	/// instance is located via FindObjectOfType and cached (rescanned when null).
	/// </summary>
	public static SteamManager Steam()
	{
		if ((Object)(object)_steam != (Object)null)
		{
			return _steam;
		}
		if (Time.unscaledTime < _steamNextScan)
		{
			return null;
		}
		_steamNextScan = Time.unscaledTime + 5f;
		try
		{
			_steam = Object.FindObjectOfType<SteamManager>();
		}
		catch
		{
		}
		return _steam;
	}

	/// <summary>
	/// Invoke an instance method by name regardless of signature. Silent + log-once
	/// when the method is missing (game update). Returns true when invoked.
	/// </summary>
	public static bool Invoke(object target, string method, params object[] args)
	{
		if (target == null)
		{
			return false;
		}
		Type type = target.GetType();
		string cacheKey = type.FullName + "::" + method;
		try
		{
			if (!MethodCache.TryGetValue(cacheKey, out MethodInfo mi))
			{
				mi = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				MethodCache[cacheKey] = mi;
			}
			if (mi == null)
			{
				ReportMissing(type.Name + "." + method);
				return false;
			}
			ParameterInfo[] parameters = mi.GetParameters();
			if (parameters.Length != args.Length)
			{
				// Signature drift (e.g. new optional parameter added by a game update):
				// pad with type defaults so the call still goes through.
				object[] padded = new object[parameters.Length];
				for (int i = 0; i < parameters.Length; i++)
				{
					if (i < args.Length)
					{
						padded[i] = args[i];
					}
					else if (parameters[i].HasDefaultValue)
					{
						padded[i] = parameters[i].DefaultValue;
					}
					else
					{
						padded[i] = parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null;
					}
				}
				args = padded;
			}
			mi.Invoke(target, args);
			return true;
		}
		catch (Exception ex)
		{
			ReportMissing(type.Name + "." + method + " (" + ex.GetType().Name + ")");
			return false;
		}
	}
}
