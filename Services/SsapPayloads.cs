using System.Text.Json;
using System.Text.Json.Nodes;

namespace LGforWin.Services;

/// <summary>
/// Constant SSAP URIs and the standard webOS registration ("hello") payload.
/// The manifest mirrors the well-known one used by lgtv2 / PyWebOSTV / bscpylgtv;
/// it requests the permission set that includes reading and writing settings.
/// </summary>
internal static class SsapPayloads
{
    // Reading settings is allowed directly over SSAP.
    public const string GetSystemSettings = "ssap://settings/getSystemSettings";

    // Writing picture settings (backlight/OLED Light) is blocked for unprivileged SSAP
    // clients on stock TVs. The proven workaround (bscpylgtv/ColorControl) is to run the
    // privileged luna call indirectly via the notification-alert API: create an alert whose
    // button/onclose invokes the luna URI, then close it to fire it.
    public const string CreateAlert = "ssap://system.notifications/createAlert";
    public const string CloseAlert = "ssap://system.notifications/closeAlert";
    public const string LunaSetSystemSettings = "com.webos.settingsservice/setSystemSettings";

    /// <summary>
    /// Builds the registration payload. When a stored client-key is supplied the TV
    /// re-authorizes silently; otherwise it shows the on-screen accept prompt.
    /// </summary>
    public static JsonObject BuildRegisterPayload(string? clientKey)
    {
        var payload = new JsonObject
        {
            ["forcePairing"] = false,
            ["pairingType"] = "PROMPT",
            ["manifest"] = new JsonObject
            {
                ["manifestVersion"] = 1,
                ["appVersion"] = "1.1",
                ["signed"] = new JsonObject
                {
                    ["created"] = "20140509",
                    ["appId"] = "com.lge.test",
                    ["vendorId"] = "com.lge",
                    ["localizedAppNames"] = new JsonObject
                    {
                        [""] = "LG Remote App",
                        ["ko-KR"] = "리모컨 앱",
                        ["zxx-XX"] = "ЛГ Rэмotэ AПП"
                    },
                    ["localizedVendorNames"] = new JsonObject { [""] = "LG Electronics" },
                    ["permissions"] = ToArray(
                        "TEST_SECURE", "CONTROL_INPUT_TEXT", "CONTROL_MOUSE_AND_KEYBOARD",
                        "READ_INSTALLED_APPS", "READ_LGE_SDX", "READ_NOTIFICATIONS",
                        "SEARCH", "WRITE_SETTINGS", "WRITE_NOTIFICATION_ALERT",
                        "CONTROL_POWER", "READ_CURRENT_CHANNEL", "READ_RUNNING_APPS",
                        "READ_UPDATE_INFO", "UPDATE_FROM_REMOTE_APP",
                        "READ_LGE_TV_INPUT_EVENTS", "READ_TV_CURRENT_TIME"),
                    ["serial"] = "2f930e2d2cfe083771f68e4fe7bb07"
                },
                ["permissions"] = ToArray(
                    "LAUNCH", "LAUNCH_WEBAPP", "APP_TO_APP", "CLOSE",
                    "TEST_OPEN", "TEST_PROTECTED", "CONTROL_AUDIO",
                    "CONTROL_DISPLAY", "CONTROL_INPUT_JOYSTICK",
                    "CONTROL_INPUT_MEDIA_RECORDING", "CONTROL_INPUT_MEDIA_PLAYBACK",
                    "CONTROL_INPUT_TV", "CONTROL_POWER", "READ_APP_STATUS",
                    "READ_CURRENT_CHANNEL", "READ_INPUT_DEVICE_LIST",
                    "READ_NETWORK_STATE", "READ_RUNNING_APPS", "READ_TV_CHANNEL_LIST",
                    "WRITE_NOTIFICATION_TOAST", "READ_POWER_STATE",
                    "READ_COUNTRY_INFO", "READ_SETTINGS", "CONTROL_TV_SCREEN",
                    "CONTROL_TV_STANBY", "CONTROL_FAVORITE_GROUP",
                    "CONTROL_USER_INFO", "CHECK_BLUETOOTH_DEVICE",
                    "CONTROL_BLUETOOTH", "CONTROL_TIMER_INFO", "STB_INTERNAL_CONNECTION",
                    "CONTROL_RECORDING", "READ_RECORDING_STATE", "WRITE_RECORDING_LIST",
                    "READ_RECORDING_LIST", "READ_RECORDING_SCHEDULE",
                    "WRITE_RECORDING_SCHEDULE", "READ_STORAGE_DEVICE_LIST",
                    "READ_TV_PROGRAM_INFO", "CONTROL_BOX_CHANNEL",
                    "READ_TV_ACR_AUTH_TOKEN", "READ_TV_CONTENT_STATE",
                    "READ_TV_CURRENT_TIME", "ADD_LAUNCHER_CHANNEL",
                    "SET_CHANNEL_SKIP", "DELETE_SELECT_CHANNEL",
                    "CONTROL_CHANNEL_GROUP", "SCAN_TV_CHANNELS",
                    "CONTROL_TV_POWER", "CONTROL_WOL"),
                ["signatures"] = new JsonArray(
                    new JsonObject
                    {
                        ["signatureVersion"] = 1,
                        ["signature"] =
                            "eyJhbGdvcml0aG0iOiJSU0EtU0hBMjU2Iiwia2V5SWQiOiJ0ZXN0LXNpZ25pbmctY2VydCIsInNpZ25hdHVyZVZlcnNpb24iOjF9.hrVRgjCwXVvE2OOSpDZ58hR+59aFNwYDyjQgKk3auukd7pcegmE2CzPCa0bJ0ZsRAcKkCTJrWo5iDzNhMBWRyaMOv5zWSrthlf7G128qvIlpMT0YNY+n/FaOHE73uLrS/g7swl3/qH/BGFG2Hu4RlL48eb3lLKqTt2xKHdCs6Cd4RMfJPYnzgvI4BNrFUKsjkcu+WD4OO2A27Pq1n50cMchmcaXadJhGrOqH5YmHdOCj5NSHzJYrsW0HPlpuAx/ECMeIZYDh6RMqaFM2DXzdKX9NmmyqzJ3o/0lkk/N97gfVRLW5hA29yeAwaCViZNCP8iC9aO0q9fQojoa7NQ=="
                    }
                )
            }
        };

        if (!string.IsNullOrEmpty(clientKey))
            payload["client-key"] = clientKey;

        return payload;
    }

    private static JsonArray ToArray(params string[] values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }
}
