using Logger = LabApi.Features.Console.Logger;
using LabApi.Features.Wrappers;
using MEC;
using PlayerRoles;
using ProjectMER.Features.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MEROptimizer.MEROptimizer.Application.Components;
using UnityEngine;
using static PlayerList;

namespace MEROptimizer.Application.Components;

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

        NonClusteredPrimitives = new();
        PrimitiveClusters = new();

        GenerateClustersAndSpawn(doClusters, primitives, distance, excludedUnspawnObjects,
            maxDistanceForPrimitiveCluster, maxPrimitivesPerCluster);
    }

    private void GenerateClustersAndSpawn(bool doClusters, Dictionary<ClientSidePrimitive, bool> primitives,
        float distance, List<string> excludedUnspawnObjects, float maxDistanceForPrimitiveCluster,
        int maxPrimitivesPerCluster)
    {
        if (!doClusters)
        {
            foreach (ClientSidePrimitive primitive in primitives.Keys)
                NonClusteredPrimitives.Add(primitive);
        }
        else
        {
            // Remove non clustered primitives and big objects
            foreach (ClientSidePrimitive primitive in primitives.Keys.ToList())
            {
                if (!primitives[primitive])
                {
                    NonClusteredPrimitives.Add(primitive);
                    primitives.Remove(primitive);
                }
                else
                {
                    if (MEROptimizer.Application.MerOptimizer.MinimumSizeBeforeBeingBigPrimitive > 0)
                    {
                        Vector3 size = primitive.Scale;

                        if (Math.Abs(size.x) + Math.Abs(size.y) + Math.Abs(size.z) > MEROptimizer.Application
                                .MerOptimizer.MinimumSizeBeforeBeingBigPrimitive)
                        {
                            NonClusteredPrimitives.Add(primitive);
                            primitives.Remove(primitive);
                        }
                    }
                }
            }

            if (!primitives.IsEmpty())
            {
                // Calculate the center of the schematic, where the first cluster will spawn
                Vector3 center3D = Vector3.zero;
                foreach (ClientSidePrimitive p in primitives.Keys)
                    center3D += p.Position;

                center3D /= primitives.Count;

                // Sort the primitives by their distance with the center
                List<ClientSidePrimitive> sortedPrimitives = primitives.Keys.ToList();
                sortedPrimitives = sortedPrimitives.OrderBy(s => Vector3.Distance(s.Position, center3D)).ToList();

                Dictionary<int, List<ClientSidePrimitive>> clusters = new();

                int clusterNumber = 1;

                // Creates clusters, add the primitives to the clusters until all clusters are generated
                while (sortedPrimitives.Count > 0)
                {
                    ClientSidePrimitive closestFromCenterPrimitive = sortedPrimitives.First();

                    List<ClientSidePrimitive> clusterPrimitives = new() { closestFromCenterPrimitive };

                    List<ClientSidePrimitive> sortedPrimitiveByCluster = sortedPrimitives.ToList();

                    Vector3 centerPos = closestFromCenterPrimitive.Position;

                    // Keep all of the primitives where their distance correspond
                    sortedPrimitiveByCluster.RemoveAll(p =>
                        Vector3.Distance(p.Position, centerPos) > maxDistanceForPrimitiveCluster);

                    // Remove excess primitives based on config
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

                    // sort the primitives on their y value, so that the first to spawn will be the bottom ones

                    clusterPrimitives = clusterPrimitives.OrderBy(p => p.Position.y).ToList();

                    clusters.Add(clusterNumber++, clusterPrimitives);
                }

                //Creates the Gameobjects for the clusters
                foreach (KeyValuePair<int, List<ClientSidePrimitive>> cluster in clusters)
                {
                    // Get the center of the cluster

                    Vector3 center = Vector3.zero;
                    foreach (ClientSidePrimitive primitive in cluster.Value)
                        center += primitive.Position;

                    center /= cluster.Value.Count;

                    // Creates the GameObject

                    GameObject gameObject = new($"[MERO] PrimitiveCluster_{Schematic.name}_{cluster.Key}");

                    gameObject.transform.position = center + new Vector3(0, 2000, 0);
                    gameObject.transform.rotation = Quaternion.identity;
                    gameObject.transform.localScale = Vector3.one;

                    SphereCollider collider = gameObject.AddComponent<SphereCollider>();
                    collider.radius = distance;
                    collider.isTrigger = true;

                    PrimitiveCluster primitiveCluster = gameObject.AddComponent<PrimitiveCluster>();
                    primitiveCluster.ID = cluster.Key;
                    primitiveCluster.Primitives = cluster.Value;

                    PrimitiveClusters.Add(primitiveCluster);
                }
            }
        }

        // Spawn of primitives

        foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
            primitive.SpawnForEveryone();


        // Spawn clusters for custom chiantos roles

        Timing.CallDelayed(.5f, () =>
        {
            if (this == null) return;

            foreach (Player player in Player.List.Where(p => p != null && !p.IsNpc))
            {
                bool shouldSpawn = false;

                // Tutorials if config is enabled
                if (!MEROptimizer.Application.MerOptimizer.ShouldTutorialsBeAffectedByDistanceSpawning &&
                    player.Role == RoleTypeId.Tutorial)
                    shouldSpawn = true;

                // Spectators if config is enabled
                if (!MEROptimizer.Application.MerOptimizer.ShouldSpectatorsBeAffectedByPds &&
                    (player.Role == RoleTypeId.Spectator || player.Role == RoleTypeId.Overwatch))
                    shouldSpawn = true;

                // Theses role always see all of the maps
                if (player.Role == RoleTypeId.Filmmaker || player.Role == RoleTypeId.Scp079)
                    shouldSpawn = true;


                if (shouldSpawn)
                {
                    foreach (PrimitiveCluster cluster in PrimitiveClusters)
                    {
                        if (cluster.instantSpawn)
                            cluster.SpawnFor(player);
                        else
                        {
                            cluster.AwaitingSpawn.Remove(player);
                            cluster.AwaitingSpawn.Add(player, cluster.Primitives.ToList());
                            cluster.spawning = true;
                        }
                    }
                }
            }
        });
    }

    public void RefreshFor(Player player)
    {
        HideFor(player, false);

        foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
            primitive.SpawnClientPrimitive(player);

        MEROptimizer.Application.MerOptimizer.Debug(
            $"Refresh the schematic {_schematicName} for {player.DisplayName} !");
    }

    public void HideFor(Player player, bool showDebug = true)
    {
        if (player == null) return;
        if (showDebug)
            MEROptimizer.Application.MerOptimizer.Debug($"Hiding client side primitives of {_schematicName} to {
                player.DisplayName}");

        foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
            primitive.DestroyClientPrimitive(player);
    }


    public void SpawnClientPrimitivesToAll()
    {
        MEROptimizer.Application.MerOptimizer.Debug($"Displaying {_schematicName}'s client side primitives !");
        foreach (Player player in Player.List.Where(p => p != null && !p.IsNpc))
            SpawnClientPrimitives(player);
    }

    public void SpawnClientPrimitives(Player player)
    {
        if (player == null) return;

        MEROptimizer.Application.MerOptimizer.Debug($"Displaying client side primitives of {_schematicName} to {
            player.DisplayName}");

        foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
            primitive.SpawnClientPrimitive(player);
    }

    public void Destroy()
    {
        foreach (Collider collider in Colliders.Where(c => c != null && c.gameObject != null))
            UnityEngine.Object.Destroy(collider);

        foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
            primitive.DestroyForEveryone();

        foreach (PrimitiveCluster cluster in PrimitiveClusters.Where(c => c != null && c.gameObject != null))
            UnityEngine.Object.Destroy(cluster);

        MEROptimizer.Application.MerOptimizer.Debug($"Destroyed client side schematic of {_schematicName} !");
    }
}