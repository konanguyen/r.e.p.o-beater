using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace r.e.p.o_cheat;

/// <summary>
/// EnemyHealth.healthCurrent 只有房主会更新：房客只会收到 HurtRPC(damage, direction) / DeathRPC，
/// 游戏不会把伤害应用到房客的本地副本，导致房客侧敌人 ESP 血量始终显示满血。
/// 这里监听传入的 PUN RPC 事件，自己把伤害扣到本地 healthCurrent 上。
/// 敌人重生时 OnSpawn/Awake 会本地重置，所以不会累积漂移。（移植自 D4rkks/r.e.p.o-cheat）
/// </summary>
public static class EnemyHealthSync
{
	private const byte RpcEventCode = 200;
	private static readonly FieldInfo HealthCurrentField = typeof(EnemyHealth).GetField("healthCurrent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

	private static LoadBalancingClient subscribedClient;

	/// <summary>每帧调用：网络客户端实例变化时重新订阅。</summary>
	public static void Tick()
	{
		LoadBalancingClient client = PhotonNetwork.NetworkingClient;
		if (client == subscribedClient)
		{
			return;
		}
		Unsubscribe();
		if ((object)client == null)
		{
			return;
		}
		client.EventReceived += OnEvent;
		subscribedClient = client;
	}

	/// <summary>卸载时必须调用，否则热重载后的副本会把每次伤害扣两次。</summary>
	public static void Unsubscribe()
	{
		if ((object)subscribedClient != null)
		{
			subscribedClient.EventReceived -= OnEvent;
		}
		subscribedClient = null;
	}

	private static void OnEvent(EventData photonEvent)
	{
		if (photonEvent.Code != RpcEventCode || PhotonNetwork.IsMasterClient || HealthCurrentField == null)
		{
			return;
		}
		try
		{
			if (!(photonEvent.CustomData is IDictionary rpc) || !rpc.Contains((byte)0))
			{
				return;
			}

			string method = null;
			if (rpc.Contains((byte)5))
			{
				int index = Convert.ToInt32(rpc[(byte)5]);
				List<string> list = PhotonNetwork.PhotonServerSettings?.RpcList;
				if (list != null && index >= 0 && index < list.Count)
				{
					method = list[index];
				}
			}
			else if (rpc.Contains((byte)3))
			{
				method = rpc[(byte)3] as string;
			}
			if (method != "HurtRPC" && method != "DeathRPC")
			{
				return;
			}

			PhotonView view = PhotonView.Find(Convert.ToInt32(rpc[(byte)0]));
			if ((Object)(object)view == (Object)null)
			{
				return;
			}
			EnemyHealth health = view.GetComponent<EnemyHealth>() ?? view.GetComponentInChildren<EnemyHealth>();
			if ((Object)(object)health == (Object)null)
			{
				return;
			}

			int current = (int)HealthCurrentField.GetValue(health);
			if (method == "DeathRPC")
			{
				HealthCurrentField.SetValue(health, 0);
				return;
			}

			object[] parameters = rpc.Contains((byte)4) ? rpc[(byte)4] as object[] : null;
			if (parameters == null || parameters.Length == 0)
			{
				return;
			}
			int damage = Convert.ToInt32(parameters[0]);
			HealthCurrentField.SetValue(health, Mathf.Max(0, current - damage));
		}
		catch (Exception e)
		{
			Debug.LogWarning((object)$"[EnemyHealthSync] error: {e.Message}");
		}
	}
}
