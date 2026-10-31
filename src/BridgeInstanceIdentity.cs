using Godot;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Saves;

namespace SpireLink;

public static partial class SpireLinkMod
{
    // Authenticated diagnostics, sampled on the game thread after startup.
    // These are live engine/store paths, not inferred from launch arguments.
    private static object ReadInstanceIdentity()
    {
        var result = new Dictionary<string, object?>
        {
            ["process_id"] = System.Environment.ProcessId,
            ["executable_path"] = OS.GetExecutablePath(),
            ["godot_user_data_directory"] = OS.GetUserDataDir(),
            ["godot_user_root_absolute"] = ProjectSettings.GlobalizePath("user://"),
            ["configured_port"] = _configuration.Port,
            ["evidence_directory"] = _configuration.DataDirectory,
            ["process_appdata"] = System.Environment.GetEnvironmentVariable("APPDATA"),
            ["process_localappdata"] = System.Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            ["session_id"] = SessionId,
            ["build_sha256"] = _buildHash,
            ["is_running_modded"] = UserDataPathProvider.IsRunningModded,
            ["game_mutation"] = false
        };
        try
        {
            var platform = PlatformUtil.PrimaryPlatform;
            result["platform"] = platform.ToString();
            result["local_player_id"] = PlatformUtil.GetLocalPlayerId(platform).ToString();
            string account = UserDataPathProvider.GetAccountScopedBasePath(null);
            result["account_path"] = account;
            result["account_absolute_path"] = ProjectSettings.GlobalizePath(account);
            var saves = SaveManager.Instance;
            result["profile_initialized"] = saves.IsProfileInitialized;
            if (saves.IsProfileInitialized)
            {
                result["profile_id"] = saves.CurrentProfileId;
                string profile = saves.GetProfileScopedPath("");
                result["save_backend_profile_path"] = profile;
                result["save_backend_profile_absolute_path"] = ProjectSettings.GlobalizePath(profile);
            }
            result["identity_status"] = saves.IsProfileInitialized ? "ready" : "profile_not_initialized";
        }
        catch (Exception ex)
        {
            // Fail closed: callers must not mistake a launch-plan path for proof.
            result["identity_status"] = "unavailable";
            result["identity_error_type"] = ex.GetType().Name;
        }
        return result;
    }
}
