// #DivaDevs (ﾉ>ω<)ﾉ*✲ﾟ*｡✲ﾟ 

using Exiled.API.Enums;
using Exiled.API.Features;
using MEROptimizer.MEROptimizer.Application;

namespace MEROptimizer.MEROptimizer;

public class Plugin : Plugin<Config>
{
    public override string Name => "MEROptimizer";
    public override string Author => "Math";
    public override string Prefix => "mero";
    public override PluginPriority Priority { get; } = PluginPriority.Low;

    public static MerOptimizer MerOptimizer;

    public override void OnEnabled()
    {
        MerOptimizer = new();
        MerOptimizer.Load(Config);

        base.OnEnabled();
    }

    public override void OnDisabled()
    {
        MerOptimizer?.Unload();
        MerOptimizer = null;

        base.OnDisabled();
    }
}