using System;
using Exiled.API.Enums;
using Exiled.API.Features;
using HarmonyLib;
using MEROptimizer.MEROptimizer.Application;

namespace MEROptimizer.MEROptimizer;

public class Plugin : Plugin<Config>
{
    public override string Name => "MEROptimizer";
    public override string Author => "Math";
    public override string Prefix => "mero";
    public override PluginPriority Priority { get; } = PluginPriority.Low;

    public static MerOptimizer MerOptimizer;
    private Harmony _harmony;
    
    public override void OnEnabled()
    {
        MerOptimizer = new();
        MerOptimizer.Load(Config);
        _harmony = new($"Math.merOptimizer-{DateTime.Now.Ticks}");
        
        _harmony.PatchAll();

        base.OnEnabled();
    }

    public override void OnDisabled()
    {
        MerOptimizer?.Unload();
        MerOptimizer = null;
        _harmony.UnpatchAll();
        _harmony = null;

        base.OnDisabled();
    }
}