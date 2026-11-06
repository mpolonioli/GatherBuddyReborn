using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GatherBuddy.AutoHookIntegration.Models;

namespace GatherBuddy.AutoHookIntegration;

public class BiteTimerService
{
    private const string FishListFile = "fish_list.json";

    private readonly Dictionary<uint, BiteTimerData> _biteTimers = new();
    private bool _isLoaded;

    public BiteTimerService(string pluginConfigDirectory)
    {
        LoadBiteTimers(pluginConfigDirectory);
    }

    private sealed class FishListEntry
    {
        public uint    ItemId       { get; set; }
        public double? BiteTimeMin  { get; set; }
        public double? BiteTimeMax  { get; set; }
    }

    private void LoadBiteTimers(string pluginConfigDirectory)
    {
        try
        {
            var fishListPath = FindAutoHookFishList(pluginConfigDirectory);
            if (fishListPath == null)
            {
                GatherBuddy.Log.Warning($"[BiteTimerService] AutoHook {FishListFile} not found. Bite timers will not be available for preset generation.");
                return;
            }

            using var stream = File.OpenRead(fishListPath);
            var entries = JsonSerializer.Deserialize<List<FishListEntry>>(stream);
            if (entries == null)
            {
                GatherBuddy.Log.Error($"[BiteTimerService] Failed to deserialize {FishListFile}");
                return;
            }

            foreach (var entry in entries)
            {
                if (entry.BiteTimeMax is not > 0 || _biteTimers.ContainsKey(entry.ItemId))
                    continue;

                var min = entry.BiteTimeMin ?? 0;
                var max = entry.BiteTimeMax.Value;
                _biteTimers[entry.ItemId] = new BiteTimerData
                {
                    ItemId     = entry.ItemId,
                    Min        = min,
                    Max        = max,
                    WhiskerMin = min,
                    WhiskerMax = max,
                };
            }

            _isLoaded = true;
            GatherBuddy.Log.Debug($"[BiteTimerService] Loaded bite timers for {_biteTimers.Count} fish from {fishListPath}");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[BiteTimerService] Error loading bite timers: {ex.Message}");
        }
    }

    private string? FindAutoHookFishList(string pluginConfigDirectory)
    {
        try
        {
            var configDir = new DirectoryInfo(pluginConfigDirectory);
            var xivLauncherRoot = configDir.Parent?.Parent;
            
            if (xivLauncherRoot == null || !xivLauncherRoot.Exists)
            {
                GatherBuddy.Log.Debug("[BiteTimerService] Could not find XIVLauncher root directory");
                return null;
            }

            var installedPluginsDir = Path.Combine(xivLauncherRoot.FullName, "installedPlugins", "AutoHook");
            
            if (!Directory.Exists(installedPluginsDir))
            {
                GatherBuddy.Log.Debug("[BiteTimerService] AutoHook plugin directory not found");
                return null;
            }

            var versionDirs = Directory.GetDirectories(installedPluginsDir)
                .OrderByDescending(d => d)
                .ToArray();

            foreach (var versionDir in versionDirs)
            {
                var fishListPath = Path.Combine(versionDir, "Data", "FishData", FishListFile);
                
                if (File.Exists(fishListPath))
                {
                    return fishListPath;
                }
            }

            GatherBuddy.Log.Debug($"[BiteTimerService] {FishListFile} not found in any AutoHook version directory");
            return null;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[BiteTimerService] Error finding AutoHook directory: {ex.Message}");
            return null;
        }
    }

    public BiteTimerData? GetBiteTimers(uint fishItemId)
    {
        if (!_isLoaded)
            return null;

        return _biteTimers.GetValueOrDefault(fishItemId);
    }

    public bool IsLoaded => _isLoaded;
}
