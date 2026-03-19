using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using MEC;
using PlayerRoles;
using ProjectMER.Features.Objects;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MEROptimizer.MEROptimizer.Application.Components;

public class OptimizedSchematic
{
    public SchematicObject Schematic { get; set; }

    private string _schematicName;

    public List<Collider> Colliders { get; set; }

    public List<ClientSidePrimitive> NonClusteredPrimitives { get; set; }

    public List<PrimitiveCluster> PrimitiveClusters { get; set; }

    public DateTime SpawnTime { get; set; }

    public int SchematicServerSidePrimitiveEmptiesCount = -1;

    public int SchematicServerSidePrimitiveCount { get; set; } = -1;

    public int GetTotalPrimitiveCount()
    {
        int count = NonClusteredPrimitives.Count;

        foreach (PrimitiveCluster cluster in PrimitiveClusters)
            count += cluster.Primitives.Count;

        return count;
    }

    public OptimizedSchematic(SchematicObject schematic, List<Collider> colliders,
        Dictionary<ClientSidePrimitive, bool> primitives,
        bool doClusters = false, float distance = 50, List<string> excludedUnspawnObjects = null,
        float maxDistanceForPrimitiveCluster = 2.5f,
        int maxPrimitivesPerCluster = 100)
    {
        Schematic = schematic;
        Colliders = colliders;
        SpawnTime = DateTime.Now;

        _schematicName = schematic.name;

        NonClusteredPrimitives = [];
        PrimitiveClusters = [];

        GenerateClustersAndSpawn(doClusters, primitives, distance, excludedUnspawnObjects,
            maxDistanceForPrimitiveCluster, maxPrimitivesPerCluster);
    }

    private void GenerateClustersAndSpawn(bool doClusters, Dictionary<ClientSidePrimitive, bool> primitives,
        float distance, List<string> excludedUnspawnObjects, float maxDistanceForPrimitiveCluster,
        int maxPrimitivesPerCluster)
    {
        excludedUnspawnObjects ??= [];
        
        if (!doClusters)
        {
            foreach (ClientSidePrimitive primitive in primitives.Keys)
                NonClusteredPrimitives.Add(primitive);
        }
        else
        {
            foreach (ClientSidePrimitive primitive in primitives.Keys.ToList())
            {
                bool shouldExcludeFromClusters = !primitives[primitive];
                
                if (!shouldExcludeFromClusters && excludedUnspawnObjects.Count > 0)
                {
                    foreach (string excludedName in excludedUnspawnObjects)
                    {
                        if (string.IsNullOrEmpty(primitive.SourceName) ||
                            primitive.SourceName.IndexOf(excludedName, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        shouldExcludeFromClusters = true;
                        break;
                    }
                }
                
                if (!shouldExcludeFromClusters && MerOptimizer.MinimumSizeBeforeBeingBigPrimitive > 0)
                {
                    Vector3 size = primitive.Scale;

                    if (Math.Abs(size.x) + Math.Abs(size.y) + Math.Abs(size.z) >
                        MerOptimizer.MinimumSizeBeforeBeingBigPrimitive)
                    {
                        shouldExcludeFromClusters = true;
                    }
                }

                if (shouldExcludeFromClusters)
                {
                    NonClusteredPrimitives.Add(primitive);
                    primitives.Remove(primitive);
                }
            }

            if (!primitives.IsEmpty())
            {
                Vector3 center3D = primitives.Keys.Aggregate(Vector3.zero, (current, p) => current + p.Position);

                center3D /= primitives.Count;

                List<ClientSidePrimitive> sortedPrimitives = primitives.Keys.ToList();
                sortedPrimitives = sortedPrimitives.OrderBy(s => Vector3.Distance(s.Position, center3D)).ToList();

                Dictionary<int, List<ClientSidePrimitive>> clusters = new();

                int clusterNumber = 1;

                while (sortedPrimitives.Count > 0)
                {
                    ClientSidePrimitive closestFromCenterPrimitive = sortedPrimitives.First();

                    List<ClientSidePrimitive> clusterPrimitives = [closestFromCenterPrimitive];

                    List<ClientSidePrimitive> sortedPrimitiveByCluster = sortedPrimitives.ToList();

                    Vector3 centerPos = closestFromCenterPrimitive.Position;

                    sortedPrimitiveByCluster.RemoveAll(p =>
                        Vector3.Distance(p.Position, centerPos) > maxDistanceForPrimitiveCluster);

                    if (sortedPrimitiveByCluster.Count > maxPrimitivesPerCluster)
                    {
                        sortedPrimitiveByCluster = sortedPrimitiveByCluster
                            .OrderBy(s => Vector3.Distance(s.Position, centerPos))
                            .ToList();

                        sortedPrimitiveByCluster.RemoveRange(maxPrimitivesPerCluster,
                            sortedPrimitiveByCluster.Count - maxPrimitivesPerCluster);
                    }

                    clusterPrimitives.AddRange(sortedPrimitiveByCluster);

                    sortedPrimitives.RemoveAll(p => clusterPrimitives.Contains(p));

                    clusterPrimitives = clusterPrimitives.OrderBy(p => p.Position.y).ToList();

                    clusters.Add(clusterNumber++, clusterPrimitives);
                }

                foreach (KeyValuePair<int, List<ClientSidePrimitive>> cluster in clusters)
                {
                    Vector3 center = cluster.Value.Aggregate(Vector3.zero, (current, primitive) => current + primitive.Position);

                    center /= cluster.Value.Count;

                    GameObject gameObject = new($"[MERO] PrimitiveCluster_{Schematic.name}_{cluster.Key}")
                    {
                        transform =
                        {
                            position = center,
                            rotation = Quaternion.identity,
                            localScale = Vector3.one
                        }
                    };

                    PrimitiveCluster primitiveCluster = gameObject.AddComponent<PrimitiveCluster>();
                    primitiveCluster.ID = cluster.Key;
                    primitiveCluster.Primitives = cluster.Value;
                    primitiveCluster.CenterPosition = center;
                    primitiveCluster.SpawnDistance = distance;

                    PrimitiveClusters.Add(primitiveCluster);
                }
            }
        }

        foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
            primitive.SpawnForEveryone();

        if (DistanceCullingManager.Instance != null)
            DistanceCullingManager.Instance.RegisterSchematic(this);

        Timing.CallDelayed(.5f, () =>
        {
            foreach (Player player in Player.List.Where(p => !p.IsDestroyed && !p.IsNpc))
            {
                bool shouldSpawn = false;

                if (!MerOptimizer.ShouldTutorialsBeAffectedByDistanceSpawning && player.Role == RoleTypeId.Tutorial)
                    shouldSpawn = true;

                if (!MerOptimizer.ShouldSpectatorsBeAffectedByPds &&
                    player.Role is RoleTypeId.Spectator or RoleTypeId.Overwatch)
                    shouldSpawn = true;

                if (player.Role is RoleTypeId.Filmmaker or RoleTypeId.Scp079)
                    shouldSpawn = true;

                if (shouldSpawn && DistanceCullingManager.Instance != null)
                    DistanceCullingManager.Instance.ForceSpawnAllClusters(player);
            }
        });
    }

    public void RefreshFor(Player player)
    {
        HideFor(player, false);

        foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
            primitive.SpawnClientPrimitive(player);

        MerOptimizer.Debug($"Refresh the schematic {_schematicName} for {player.DisplayName} !");
    }

    public void HideFor(Player player, bool showDebug = true)
    {
        if (player == null) return;
        if (showDebug)
            MerOptimizer.Debug($"Hiding client side primitives of {_schematicName} to {player.DisplayName}");

        foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
            primitive.DestroyClientPrimitive(player);
    }

    public void SpawnClientPrimitivesToAll()
    {
        MerOptimizer.Debug($"Displaying {_schematicName}'s client side primitives !");
        foreach (Player player in Player.List.Where(p => !p.IsDestroyed && !p.IsNpc))
            SpawnClientPrimitives(player);
    }

    public void SpawnClientPrimitives(Player player)
    {
        if (player == null) return;

        MerOptimizer.Debug($"Displaying client side primitives of {_schematicName} to {player.DisplayName}");
        foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
            primitive.SpawnClientPrimitive(player);
    }

    public void Destroy()
    {
        if (DistanceCullingManager.Instance != null)
            DistanceCullingManager.Instance.UnregisterSchematic(this);

        foreach (Collider collider in Colliders.Where(c => c != null && c.gameObject != null))
            Object.Destroy(collider);

        foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
            primitive.DestroyForEveryone();

        foreach (PrimitiveCluster cluster in PrimitiveClusters.Where(c => c != null && c.gameObject != null))
            Object.Destroy(cluster.gameObject);

        MerOptimizer.Debug($"Destroyed client side schematic of {_schematicName} !");
    }
}