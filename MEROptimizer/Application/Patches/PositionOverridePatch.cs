using System.Collections.Generic;
using System.Linq;
using Exiled.API.Features;
using HarmonyLib;
using MEROptimizer.MEROptimizer.Application.Components;
using PlayerRoles.FirstPersonControl;
using UnityEngine;

namespace MEROptimizer.MEROptimizer.Application.Patches;

[HarmonyPatch(typeof(FirstPersonMovementModule), nameof(FirstPersonMovementModule.ServerOverridePosition))]
public static class PositionOverridePatch
{
    private const float YTolerance = 0.5f;
    private const float RaycastDistance = 5f;
    private const float MinTeleportDistance = 5f;
    
    private static readonly int FloorMask = LayerMask.GetMask("Default");
    
    public static void Prefix(FirstPersonMovementModule __instance, Vector3 position)
    {
        Player player = Player.Get(__instance.Hub);
        float distanceSqr = (position - __instance.Position).sqrMagnitude;
        if (distanceSqr < MinTeleportDistance * MinTeleportDistance)
            return;

        Vector3 rayOrigin = position + Vector3.up * 0.5f;
        if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, RaycastDistance, FloorMask))
            return;

        float floorY = hit.point.y;
        MerOptimizer.Debug($"[PRELOAD] Teleport detected for {player.Nickname}, floor Y={floorY:F2}");
        PreloadFloorPrimitives(player, position, floorY);
    }

    private static void PreloadFloorPrimitives(Player player, Vector3 targetPosition, float floorY)
    {
        if (Plugin.MerOptimizer?.OptimizedSchematics == null)
            return;

        if (!DistanceCullingManager.Instance)
        {
            MerOptimizer.Debug("[PRELOAD] DistanceCullingManager not available");
            return;
        }

        int preloadedCount = 0;
        int enqueuedClusters = 0;
        float minY = floorY - YTolerance;
        float maxY = floorY + YTolerance;
        foreach (OptimizedSchematic schematic in Plugin.MerOptimizer.OptimizedSchematics.Where(schematic => schematic != null))
        {
            foreach (ClientSidePrimitive primitive in schematic.NonClusteredPrimitives
                         .Where(primitive => IsFloorPrimitive(primitive, minY, maxY)))
            {
                primitive.SpawnClientPrimitive(player);
                preloadedCount++;
            }
            
            foreach (PrimitiveCluster cluster in schematic.PrimitiveClusters)
            {
                if (cluster?.Primitives == null)
                    continue;
                
                float distSqr = (cluster.CenterPosition - targetPosition).sqrMagnitude;
                float radiusSqr = cluster.SpawnDistance * cluster.SpawnDistance;
                if (distSqr > radiusSqr)
                    continue;
                
                List<ClientSidePrimitive> floorPrimitives = cluster.Primitives
                    .Where(p => IsFloorPrimitive(p, minY, maxY)).ToList();

                if (floorPrimitives.Count == 0)
                    continue;
                
                bool wasNotInQueue = !cluster.AwaitingSpawn.ContainsKey(player);
                if (wasNotInQueue)
                {
                    cluster.EnqueueSpawn(player);
                    enqueuedClusters++;
                    MerOptimizer.Debug($"[PRELOAD] Enqueued cluster #{cluster.ID} for {player.Nickname}");
                }

                if (!cluster.AwaitingSpawn.TryGetValue(player, out List<ClientSidePrimitive> awaiting))
                    continue;

                foreach (ClientSidePrimitive primitive in floorPrimitives)
                {
                    primitive.SpawnClientPrimitive(player);
                    awaiting.Remove(primitive);
                    preloadedCount++;
                }
            }
        }

        if (preloadedCount > 0 || enqueuedClusters > 0)
        {
            Log.Info($"[PRELOAD] Completed for {player.Nickname}: " +
                              $"spawned={preloadedCount} primitives, " +
                              $"enqueued={enqueuedClusters} clusters");
        }
    }

    private static bool IsFloorPrimitive(ClientSidePrimitive primitive, float minY, float maxY)
    {
        float primitiveY = primitive.Position.y;
        return primitiveY >= minY && primitiveY <= maxY;
    }
}