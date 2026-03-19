using System;
using CommandSystem;

namespace MEROptimizer.MEROptimizer.Application.Commands;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
public class DisablePluginCmd : ICommand
{
    public string Command { get; } = "mero.disable";

    public string[] Aliases { get; } = ["mero.d"];

    public string Description { get; } = "Disable or enable the optimisation of newly created schematics";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        MerOptimizer.IsDynamiclyDisabled =
            !MerOptimizer.IsDynamiclyDisabled;

        response = $"New spawned schematics {(MerOptimizer.IsDynamiclyDisabled 
            ? "<color=red>will not" : "<color=green>will")}</color> be optimized !";

        return true;
    }
}