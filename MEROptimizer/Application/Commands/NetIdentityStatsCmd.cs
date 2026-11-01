using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CommandSystem;
using Exiled.API.Features;
using Mirror;

namespace MEROptimizer.MEROptimizer.Application.Commands;

/// <summary>
/// Восстановлена по декомпиляции боевой MEROptimizer.dll (05.08.2026): в репозитории исходника не было.
/// </summary>
[CommandHandler(typeof(RemoteAdminCommandHandler))]
public class NerIdentityStats : ICommand
{
    public string Command { get; } = "mero.netstats";

    public string[] Aliases { get; } = ["mero.ns"];

    public string Description { get; } =
        "Displays a breakdown of currently spawned NetworkIdentity objects by type, with their total observer count (the actual per-tick broadcast cost), to diagnose server tick/broadcast load.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        Dictionary<string, int> countByType = new();
        Dictionary<string, long> observersByType = new();
        int total = 0;
        long totalObservers = 0;

        foreach (NetworkIdentity identity in NetworkServer.spawned.Values)
        {
            if (identity == null)
                continue;

            total++;
            string typeName = GetTypeName(identity);
            int observers = identity.observers?.Count ?? 0;

            countByType.TryGetValue(typeName, out int count);
            countByType[typeName] = count + 1;

            observersByType.TryGetValue(typeName, out long typeObservers);
            observersByType[typeName] = typeObservers + observers;

            totalObservers += observers;
        }

        int readyConnections = NetworkServer.connections.Values.Count(c => c?.isReady ?? false);
        double avgPerPlayer = readyConnections > 0 ? (double)totalObservers / readyConnections : 0.0;

        StringBuilder sb = new();
        sb.AppendLine($"Ready connections: {readyConnections}");
        sb.AppendLine($"Total spawned NetworkIdentity: {total}");
        sb.AppendLine($"Total observer entries (sum across all identities): {totalObservers}");
        sb.AppendLine($"Avg objects iterated per player per broadcast tick: {avgPerPlayer:F1}");
        sb.AppendLine("-------------------------------");
        sb.AppendLine(string.Format("{0,-32} | {1,7} | {2,7} | {3,9}", "Type", "Count", "AvgObs", "TotalCost"));

        foreach (KeyValuePair<string, int> pair in countByType.OrderByDescending(x =>
                     observersByType.TryGetValue(x.Key, out long cost) ? cost : 0))
        {
            string type = pair.Key;
            int count = pair.Value;
            if (!observersByType.TryGetValue(type, out long cost))
                cost = 0;

            double avgObservers = count > 0 ? (double)cost / count : 0.0;
            sb.AppendLine($"{type,-32} | {count,7} | {avgObservers,7:F1} | {cost,9}");
        }

        Log.Info(sb.ToString());
        response = string.Empty;
        return true;
    }

    private static string GetTypeName(NetworkIdentity identity)
    {
        if (identity.NetworkBehaviours == null || identity.NetworkBehaviours.Length == 0)
            return identity.gameObject != null ? "[NoBehaviour] " + identity.gameObject.name : "[NoBehaviour]";

        NetworkBehaviour behaviour = identity.NetworkBehaviours.FirstOrDefault(b => b != null);
        return behaviour != null ? behaviour.GetType().Name : "Unknown";
    }
}
