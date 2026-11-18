namespace r.e.p.o_cheat;

/// <summary>
/// Thông tin phiên bản của fork REPO Beater.
/// ForkInfo.Version phải khớp với release tag (v{Version});
/// workflow .github/workflows/release.yml sẽ fail build nếu lệch.
/// </summary>
public static class ForkInfo
{
	/// <summary>Phiên bản fork, không có tiền tố 'v' (tag release tương ứng: v2.6.2).</summary>
	public const string Version = "2.6.2";

	/// <summary>Bản game mà fork này tương thích.</summary>
	public const string GameTarget = "v0.4.4.3";
}
