using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Opendeck.AiAgentMonitor.Hooks;

/// <summary>
/// Adds/removes the PermissionRequest hooks that forward permission prompts to the deck:
/// an http hook in Claude Code's user settings and a command hook in Codex's user hooks.json.
/// Idempotent (our entries are recognised by URL / script path), other hooks are preserved, backups are written.
/// </summary>
public static class HookInstaller
{
    public const string Tag = "aiagentmonitor";

    public static string ClaudeSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
    public static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public static string CodexHooksPath => Path.Combine(CodexHome, "hooks.json");
    public static string CodexConfigPath => Path.Combine(CodexHome, "config.toml");

    /// <summary>Absolute path of hooks/codex-hook.sh next to the plugin binary (symlinks resolved).</summary>
    public static string CodexHookScript()
    {
        var candidates = new[] { Path.Combine(AppContext.BaseDirectory, "..", "hooks", "codex-hook.sh"), Path.Combine(AppContext.BaseDirectory, "hooks", "codex-hook.sh") };
        foreach (var c in candidates) if (File.Exists(c)) return Path.GetFullPath(c);
        return Path.GetFullPath(candidates[0]);
    }

    public static void Install(int port, int holdSeconds)
    {
        InstallClaude(port, holdSeconds);
        InstallCodex(port, holdSeconds);
    }

    public static void Uninstall()
    {
        UninstallClaude();
        UninstallCodex();
    }

    // ---- Claude: settings.json → hooks.PermissionRequest[].hooks[] { type: http, url: .../hooks/claude } ----

    public static void InstallClaude(int port, int holdSeconds)
    {
        var root = LoadObject(ClaudeSettingsPath);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        var groups = RemoveOurs(hooks, "PermissionRequest", h => (h["url"]?.GetValue<string>() ?? "").Contains("/hooks/claude"));
        groups.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "http",
                ["url"] = $"http://127.0.0.1:{port}/hooks/claude",
                ["timeout"] = holdSeconds + 5,
            }),
        });
        hooks["PermissionRequest"] = groups;
        Save(ClaudeSettingsPath, root);
        Console.WriteLine($"Claude: PermissionRequest http hook → http://127.0.0.1:{port}/hooks/claude (timeout {holdSeconds + 5}s) in {ClaudeSettingsPath}");
    }

    public static void UninstallClaude()
    {
        if (!File.Exists(ClaudeSettingsPath)) return;
        var root = LoadObject(ClaudeSettingsPath);
        if (root["hooks"] is not JsonObject hooks) return;
        var groups = RemoveOurs(hooks, "PermissionRequest", h => (h["url"]?.GetValue<string>() ?? "").Contains("/hooks/claude"));
        if (groups.Count == 0) hooks.Remove("PermissionRequest"); else hooks["PermissionRequest"] = groups;
        if (hooks.Count == 0) root.Remove("hooks");
        Save(ClaudeSettingsPath, root);
        Console.WriteLine($"Claude: hook removed from {ClaudeSettingsPath}");
    }

    // ---- Codex: hooks.json → { hooks: { PermissionRequest: [ { hooks: [ { type: command, command: codex-hook.sh } ] } ] } } ----

    public static void InstallCodex(int port, int holdSeconds)
    {
        var script = CodexHookScript();
        var root = LoadObject(CodexHooksPath);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        var groups = RemoveOurs(hooks, "PermissionRequest", h => (h["command"]?.GetValue<string>() ?? "").Contains(Tag));
        groups.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"AIAGENTMONITOR_PORT={port} AIAGENTMONITOR_HOLD={holdSeconds + 5} '{script}'",
                ["timeout"] = holdSeconds + 10,
                ["statusMessage"] = "Waiting for the deck (approve / deny)…",
            }),
        });
        hooks["PermissionRequest"] = groups;
        Save(CodexHooksPath, root);
        Console.WriteLine($"Codex: PermissionRequest command hook → {script} (port {port}, timeout {holdSeconds + 10}s) in {CodexHooksPath}");

        // Codex only runs hooks it has been told to trust: [hooks.state."<file>:<event>:<group>:<handler>"] trusted_hash
        // in config.toml, where the hash is Codex's fingerprint of the normalised handler (see CodexHookTrustHash).
        var (key, hash) = CodexTrustEntry(root);
        WriteCodexTrustState(key, hash);
        Console.WriteLine($"Codex: hook marked trusted in {CodexConfigPath} ({hash[..19]}…)");
    }

    /// <summary>The config.toml state key and trust hash for our handler in the given hooks.json document.</summary>
    public static (string Key, string Hash) CodexTrustEntry(JsonObject root)
    {
        var groups = root["hooks"]?["PermissionRequest"] as JsonArray ?? throw new InvalidOperationException("no PermissionRequest hooks");
        for (var g = 0; g < groups.Count; g++)
        {
            var list = groups[g]?["hooks"] as JsonArray;
            if (list is null) continue;
            for (var i = 0; i < list.Count; i++)
                if (list[i] is JsonObject h && (h["command"]?.GetValue<string>() ?? "").Contains(Tag))
                    return ($"{CodexHooksPath}:permission_request:{g}:{i}", CodexHookTrustHash("permission_request", h));
        }
        throw new InvalidOperationException("our hook is not in hooks.json");
    }

    /// <summary>
    /// Mirrors codex-rs/hooks/src/engine/discovery.rs::hook_hash + config/src/fingerprint.rs::version_for_toml:
    /// sha256 of the canonical (recursively key-sorted, compact, UTF-8) JSON of
    /// {"event_name": &lt;key label&gt;, "hooks": [ { "type": "command", "command", "timeout", "async", "statusMessage" } ]}.
    /// None-valued optionals (matcher, commandWindows, additionalContextLimit) are omitted.
    /// </summary>
    internal static string CodexHookTrustHash(string eventKeyLabel, JsonObject handler)
    {
        var h = new JsonObject
        {
            ["async"] = handler["async"]?.GetValue<bool>() ?? false,
            ["command"] = handler["command"]?.GetValue<string>() ?? "",
        };
        if (handler["statusMessage"] is JsonNode sm) h["statusMessage"] = sm.GetValue<string>();
        h["timeout"] = handler["timeout"] is JsonNode t ? t.GetValue<long>() : 600;
        h["type"] = "command";
        var identity = new JsonObject { ["event_name"] = eventKeyLabel, ["hooks"] = new JsonArray(h) };
        var json = identity.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>Replaces/adds our [hooks.state."key"] table in config.toml (other content untouched).</summary>
    private static void WriteCodexTrustState(string key, string hash)
    {
        var lines = File.Exists(CodexConfigPath) ? File.ReadAllLines(CodexConfigPath).ToList() : new List<string>();
        RemoveCodexTrustBlocks(lines);
        if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
        lines.Add($"[hooks.state.\"{key}\"]");
        lines.Add($"trusted_hash = \"{hash}\"");
        if (File.Exists(CodexConfigPath)) File.Copy(CodexConfigPath, $"{CodexConfigPath}.bak-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", true);
        File.WriteAllText(CodexConfigPath, string.Join("\n", lines).TrimEnd('\n') + "\n");
    }

    /// <summary>Drops every [hooks.state."&lt;our hooks.json&gt;:permission_request:…"] table from the TOML lines.</summary>
    private static void RemoveCodexTrustBlocks(List<string> lines)
    {
        var prefix = $"[hooks.state.\"{CodexHooksPath}:permission_request:";
        for (var i = 0; i < lines.Count;)
        {
            if (lines[i].TrimStart().StartsWith(prefix, StringComparison.Ordinal))
            {
                var j = i + 1;
                while (j < lines.Count && !lines[j].TrimStart().StartsWith('[')) j++;
                lines.RemoveRange(i, j - i);
            }
            else i++;
        }
    }

    public static void UninstallCodex()
    {
        if (!File.Exists(CodexHooksPath)) return;
        var root = LoadObject(CodexHooksPath);
        if (root["hooks"] is not JsonObject hooks) return;
        var groups = RemoveOurs(hooks, "PermissionRequest", h => (h["command"]?.GetValue<string>() ?? "").Contains(Tag));
        if (groups.Count == 0) hooks.Remove("PermissionRequest"); else hooks["PermissionRequest"] = groups;
        Save(CodexHooksPath, root);
        if (File.Exists(CodexConfigPath))
        {
            var lines = File.ReadAllLines(CodexConfigPath).ToList();
            RemoveCodexTrustBlocks(lines);
            File.Copy(CodexConfigPath, $"{CodexConfigPath}.bak-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", true);
            File.WriteAllText(CodexConfigPath, string.Join("\n", lines).TrimEnd('\n') + "\n");
        }
        Console.WriteLine($"Codex: hook removed from {CodexHooksPath} (and its trust entry from {CodexConfigPath})");
    }

    // ---- helpers ------------------------------------------------------------------------

    private static JsonObject LoadObject(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject();
    }

    /// <summary>Returns the event's matcher groups minus the hook entries matching <paramref name="ours"/> (dropping emptied groups).</summary>
    private static JsonArray RemoveOurs(JsonObject hooks, string eventName, Func<JsonObject, bool> ours)
    {
        var result = new JsonArray();
        if (hooks[eventName] is JsonArray groups)
        {
            foreach (var g in groups.ToList())
            {
                if (g is not JsonObject group) continue;
                var list = group["hooks"] as JsonArray ?? new JsonArray();
                var kept = list.OfType<JsonObject>().Where(h => !ours(h)).Select(h => (JsonNode)JsonNode.Parse(h.ToJsonString())!).ToList();
                if (kept.Count == 0) continue;
                var copy = (JsonObject)JsonNode.Parse(group.ToJsonString())!;
                copy["hooks"] = new JsonArray(kept.ToArray());
                result.Add(copy);
            }
        }
        return result;
    }

    private static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Copy(path, $"{path}.bak-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", true);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
    }
}
