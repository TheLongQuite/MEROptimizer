using CommandSystem;
using LabApi.Features.Wrappers;
using MEROptimizer.Application.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MEROptimizer.MEROptimizer.Application.Components;

namespace MEROptimizer.Application.Commands;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
public class ClusterInfoCmd : ICommand
{
    public string Command { get; } = "mero.clusters";

    public string[] Aliases { get; }

    public string Description { get; } = "Displays information about all of the clusters in schematics.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!Player.TryGet(sender, out Player player))
        {
            response = $"You must be an active player to execute this command !";
            return false;
        }

        string message = "";

        foreach (OptimizedSchematic os in Plugin.MerOptimizer.OptimizedSchematics)
        {
            message +=
                $"Schematic : {os.Schematic.name}\n" +
                $"Number of clusters : {os.PrimitiveClusters.Count}";

            foreach (PrimitiveCluster cluster in os.PrimitiveClusters)
                message += $"\nId : {cluster.ID} | Pos : {cluster.transform.position} | Number of primitives : {
                    cluster.Primitives.Count}";
        }

        message += $"\n----------------\n";

        response = message != "" ? message : "No information to display";

        return true;
    }
}