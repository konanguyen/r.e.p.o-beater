using System;
using System.Reflection;
using Photon.Pun;
using UnityEngine;

namespace r.e.p.o_cheat;

/// <summary>
/// 自瞄系统 — 持枪时自动瞄准最近敌人
/// </summary>
public static class Aimbot
{
    public static bool isEnabled = false;
    public static float smoothness = 10f;
    public static float maxDistance = 100f;
    public static KeyCode aimKey = KeyCode.Mouse1; // 右键按住激活

    private static Enemy currentTarget = null;

    // 反射缓存 — 字段按运行时类型解析一次，避免每帧 GetField
    private static Type _aimPcType;
    private static FieldInfo _aimAvatarField;
    private static Type _aimAvatarType;
    private static FieldInfo _aimGrabberField;
    private static Type _aimGrabberType;
    private static FieldInfo _aimGrabbedField;
    private static FieldInfo _aimGrabbedTransformField;
    private static Type _centerFieldType;
    private static FieldInfo _centerField;

    /// <summary>
    /// 每帧调用（在 Update 中）
    /// </summary>
    public static void UpdateAimbot()
    {
        if (!isEnabled) return;
        if (!Input.GetKey(aimKey)) { currentTarget = null; return; }

        // Đang ngã lộn thì ngừng điều khiển camera - tránh chống lại ragdoll physics
        if (IsLocalTumbling()) { currentTarget = null; return; }

        // 检查是否持枪
        if (!IsHoldingGun()) return;

        // 找最近敌人
        Enemy nearest = FindNearestEnemy();
        if (nearest == null) { currentTarget = null; return; }
        currentTarget = nearest;

        // 瞄准
        AimAt(nearest);
    }

    private static Component _localTumble;
    private static FieldInfo _tumbleStateField;

    /// <summary>
    /// Phát hiện người chơi cục bộ đang ngã lộn, hỗ trợ cả schema cũ (tumbleActive)
    /// lẫn v0.5.2+ (tumbleRequestIsTumbling / tumbleOverride). Không tìm thấy field nào
    /// thì coi như không tumble.
    /// </summary>
    private static bool IsLocalTumbling()
    {
        try
        {
            GameObject localPlayer = DebugCheats.GetLocalPlayer();
            if (localPlayer == null) return false;

            if (_localTumble == null)
            {
                _localTumble = localPlayer.GetComponent("PlayerTumble") as Component;
            }
            else if ((Object)(object)_localTumble.transform.root != (Object)(object)localPlayer.transform.root)
            {
                _localTumble = localPlayer.GetComponent("PlayerTumble") as Component;
                _tumbleStateField = null;
            }
            if (_localTumble == null) return false;

            Type tumbleType = _localTumble.GetType();
            if (_tumbleStateField == null)
            {
                foreach (string name in new[] { "tumbleActive", "tumbleRequestIsTumbling", "tumbleOverride" })
                {
                    _tumbleStateField = tumbleType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_tumbleStateField != null) break;
                }
                if (_tumbleStateField == null) return false;
            }
            return _tumbleStateField.GetValue(_localTumble) is bool tumbling && tumbling;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsHoldingGun()
    {
        try
        {
            // 使用 PlayerReflectionCache 获取本地 PlayerController 实例
            object pc = PlayerReflectionCache.PlayerControllerInstance;
            if (pc == null) return false;

            Type pcType = pc.GetType();
            if (_aimAvatarField == null || pcType != _aimPcType)
            {
                _aimAvatarField = pcType.GetField("playerAvatarScript",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _aimPcType = pcType;
            }
            if (_aimAvatarField == null) return false;
            object avatar = _aimAvatarField.GetValue(pc);
            if (avatar == null) return false;

            Type avatarType = avatar.GetType();
            if (_aimGrabberField == null || avatarType != _aimAvatarType)
            {
                _aimGrabberField = avatarType.GetField("physGrabber",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _aimAvatarType = avatarType;
            }
            if (_aimGrabberField == null) return false;
            object grabber = _aimGrabberField.GetValue(avatar);
            if (grabber == null) return false;

            // Check if grabbed
            Type grabberType = grabber.GetType();
            if (_aimGrabbedField == null || grabberType != _aimGrabberType)
            {
                _aimGrabbedField = grabberType.GetField("grabbed",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _aimGrabbedTransformField = grabberType.GetField("grabbedObjectTransform",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _aimGrabberType = grabberType;
            }
            if (_aimGrabbedField == null) return false;
            bool grabbed = (bool)_aimGrabbedField.GetValue(grabber);
            if (!grabbed) return false;

            // Get grabbed object transform
            if (_aimGrabbedTransformField == null) return false;
            Transform grabbedTransform = _aimGrabbedTransformField.GetValue(grabber) as Transform;
            if (grabbedTransform == null) return false;

            // Check if it's a gun
            return grabbedTransform.GetComponent<ItemGun>() != null;
        }
        catch
        {
            return false;
        }
    }

    private static Enemy FindNearestEnemy()
    {
        var enemies = DebugCheats.enemyList;
        if (enemies == null || enemies.Count == 0) return null;

        Camera cam = Camera.main;
        if (cam == null) return null;

        GameObject localPlayer = DebugCheats.GetLocalPlayer();
        if (localPlayer == null) return null;

        Vector3 playerPos = localPlayer.transform.position;
        Enemy nearest = null;
        float nearestDist = maxDistance;

        foreach (var enemy in enemies)
        {
            if (enemy == null || !((Component)enemy).gameObject.activeInHierarchy) continue;

            // Check if alive
            int hp = Enemies.GetEnemyHealth(enemy);
            if (hp <= 0) continue;

            Vector3 enemyPos = ((Component)enemy).transform.position;
            float dist = Vector3.Distance(playerPos, enemyPos);
            if (dist < nearestDist)
            {
                // Check if on screen
                Vector3 screenPos = cam.WorldToScreenPoint(enemyPos);
                if (screenPos.z > 0)
                {
                    nearestDist = dist;
                    nearest = enemy;
                }
            }
        }

        return nearest;
    }

    private static void AimAt(Enemy enemy)
    {
        Camera cam = GameHelper.GetActiveCamera();
        if (cam == null) return;

        Vector3 targetPos = ((Component)enemy).transform.position;

        // Try to get CenterTransform for more accurate aim
        try
        {
            Type enemyType = enemy.GetType();
            if (_centerField == null || enemyType != _centerFieldType)
            {
                _centerField = enemyType.GetField("CenterTransform",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _centerFieldType = enemyType;
            }
            if (_centerField != null)
            {
                Transform center = _centerField.GetValue(enemy) as Transform;
                if (center != null)
                    targetPos = center.position;
            }
        }
        catch { }

        Vector3 direction = targetPos - cam.transform.position;
        if (direction.sqrMagnitude < 0.01f) return;

        // Giới hạn tốc độ quay (°/s) - quay quá nhanh làm khẩu súng (physics spring)
        // văng theo và đập vào chính người chơi -> game trigger tumble.
        float degreesPerSecond = Mathf.Clamp(60f + smoothness * 30f, 60f, 960f);
        float maxDegrees = degreesPerSecond * Time.deltaTime;
        Quaternion targetRot = Quaternion.LookRotation(direction);
        cam.transform.rotation = Quaternion.RotateTowards(cam.transform.rotation, targetRot, maxDegrees);
    }

    public static bool HasTarget()
    {
        return currentTarget != null && isEnabled && Input.GetKey(aimKey);
    }

    public static Vector3 GetTargetPosition()
    {
        if (currentTarget == null) return Vector3.zero;
        return ((Component)currentTarget).transform.position;
    }
}
