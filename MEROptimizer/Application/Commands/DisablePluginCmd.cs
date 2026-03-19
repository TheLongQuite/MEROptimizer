using CommandSystem;
using MEROptimizer.Application.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MEROptimizer.Application.Commands;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
public class DisablePluginCmd : ICommand
{
    public string Command { get; } = "mero.disable";

    public string[] Aliases { get; } = new string[] { "mero.d" };

    public string Description { get; } = "Disable or enable the optimisation of newly created schematics";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        MEROptimizer.Application.MerOptimizer.IsDynamiclyDisabled =
            !MEROptimizer.Application.MerOptimizer.IsDynamiclyDisabled;

        response = $"New spawned schematics {
            (MEROptimizer.Application.MerOptimizer.IsDynamiclyDisabled ? "<color=red>will not" : "<color=green>will")
        }</color> be optimized !";

        return true;
    }
}