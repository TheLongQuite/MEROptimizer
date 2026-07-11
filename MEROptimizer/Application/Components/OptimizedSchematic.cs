using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using MEC;
using Mirror;
using PlayerRoles;
using ProjectMER.Features.Objects;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MEROptimizer.MEROptimizer.Application.Components;

public class OptimizedSchematic
{
    private const float TeleportMatchRadius = 3f;
    private const float TeleportPriorityRadius = 3f;
    private const int MaxTeleportPriorityPrimitives = 25;

    public sealed class TeleportPriorityEntry
    {
        public Vector3 TeleportPosition;
        public readonly List<ClientSidePrimitive> NonClustered = new();
        public readonly Dictionary<PrimitiveCluster, List<ClientSidePrimitive>> Clustered = new();
    }

    public SchematicObject Schematic { get; set; }
    private string _schematicName;
    public List<Collider> Colliders { get; set; }
    public List<ClientSidePrimitive> NonClusteredPrimitives { get; set; }
    public List<PrimitiveCluster> PrimitiveClusters { get; set; }
    public List<TeleportPriorityEntry> TeleportPriorityEntries { get; } = new();
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
        BuildTeleportPriorityCache();
    }

    public void RemovePrimitivesByTransforms(List<Transform> targetRoots)
    {
        if (targetRoots == null || targetRoots.Count == 0) return;

        List<ClientSidePrimitive> toRemove = new();

        foreach (ClientSidePrimitive p in NonClusteredPrimitives)
        {
            if (p.SourceTransform == null) continue;
            foreach (Transform root in targetRoots)
            {
                if (p.SourceTransform == root || p.SourceTransform.IsChildOf(root))
                {
                    toRemove.Add(p);
                    break;
                }
            }
        }
        foreach (ClientSidePrimitive p in toRemove)
        {
            p.DestroyForEveryone();
            NonClusteredPrimitives.Remove(p);
        }
        toRemove.Clear();

        foreach (PrimitiveCluster cluster in PrimitiveClusters)
        {
            foreach (ClientSidePrimitive p in cluster.Primitives)
            {
                if (p.SourceTransform == null) continue;
                foreach (Transform root in targetRoots)
                {
                    if (p.SourceTransform == root || p.SourceTransform.IsChildOf(root))
                    {
                        toRemove.Add(p);
                        break;
                    }
                }
            }
            foreach (ClientSidePrimitive p in toRemove)
            {
                p.DestroyForEveryone();
                cluster.Primitives.Remove(p);
            }
            toRemove.Clear();
        }
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
            
            NonClusteredPrimitives = NonClusteredPrimitives
                .OrderByDescending(p => p.PrimitiveFlags.HasFlag(AdminToys.PrimitiveFlags.Collidable))
                .ToList();
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

                List<ClientSidePrimitive> availablePrimitives = primitives.Keys
                    .OrderBy(s => Vector3.Distance(s.Position, center3D))
                    .ToList();

                Dictionary<int, List<ClientSidePrimitive>> clusters = new();
                int clusterNumber = 1;

                while (availablePrimitives.Count > 0)
                {
                    ClientSidePrimitive closestFromCenterPrimitive = availablePrimitives[0];
                    Vector3 centerPos = closestFromCenterPrimitive.Position;

                    List<ClientSidePrimitive> clusterPrimitives = new() { closestFromCenterPrimitive };
                    HashSet<ClientSidePrimitive> clusterSet = new() { closestFromCenterPrimitive };

                    List<ClientSidePrimitive> candidates = new();
                    foreach (ClientSidePrimitive p in availablePrimitives)
                    {
                        if (p == closestFromCenterPrimitive) continue;
                        if (Vector3.Distance(p.Position, centerPos) <= maxDistanceForPrimitiveCluster)
                            candidates.Add(p);
                    }

                    IEnumerable<ClientSidePrimitive> selectedCandidates = candidates
                        .OrderBy(s => Vector3.Distance(s.Position, centerPos))
                        .Take(maxPrimitivesPerCluster - 1);

                    foreach (ClientSidePrimitive p in selectedCandidates)
                    {
                        clusterPrimitives.Add(p);
                        clusterSet.Add(p);
                    }

                    clusterPrimitives = clusterPrimitives.OrderBy(p => p.Position.y).ToList();
                    clusterPrimitives = clusterPrimitives
                        .OrderByDescending(p => p.PrimitiveFlags.HasFlag(AdminToys.PrimitiveFlags.Collidable))
                        .ThenBy(p => p.Position.y)
                        .ToList();
                    
                    clusters.Add(clusterNumber++, clusterPrimitives);
                    
                    availablePrimitives.RemoveAll(p => clusterSet.Contains(p));
                }

                foreach (KeyValuePair<int, List<ClientSidePrimitive>> cluster in clusters)
                {
                    Vector3 center = cluster.Value.Aggregate(Vector3.zero, (current, primitive) => current + primitive.Position);
                    center /= cluster.Value.Count;

                    GameObject gameObject = new($"[MERO] PrimitiveCluster_{Schematic.name}_{cluster.Key}")
                    {
                        transform = { position = center, rotation = Quaternion.identity, localScale = Vector3.one }
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

        if (NonClusteredPrimitives.Count > 0)
        {
            List<byte[]> spawnBatch = new List<byte[]>(NonClusteredPrimitives.Count);
            foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
                spawnBatch.Add(primitive.SerializedSpawnMessage);

            foreach (Player player in Player.List.Where(p => !p.IsDestroyed && !p.IsNpc && !p.IsDummy))
            {
                if (player.Connection is NetworkConnectionToClient conn)
                    NetworkBatcher.EnqueueBatch(conn, spawnBatch);
            }
        }

        if (DistanceCullingManager.Instance != null)
            DistanceCullingManager.Instance.RegisterSchematic(this);

        Timing.CallDelayed(.5f, () =>
        {
            foreach (Player player in Player.List.Where(p => !p.IsDestroyed && !p.IsNpc))
            {
                bool shouldSpawn = false;

                if (!MerOptimizer.ShouldTutorialsBeAffectedByDistanceSpawning && player.Role == RoleTypeId.Tutorial)
                    shouldSpawn = true;

                if (!MerOptimizer.ShouldSpectatorsSeeNothing &&
                    !MerOptimizer.ShouldSpectatorsBeAffectedByPds &&
                    player.Role is RoleTypeId.Spectator or RoleTypeId.Overwatch)
                    shouldSpawn = true;

                if (player.Role is RoleTypeId.Filmmaker or RoleTypeId.Scp079)
                    shouldSpawn = true;

                if (shouldSpawn && DistanceCullingManager.Instance != null)
                    DistanceCullingManager.Instance.ForceSpawnAllClusters(player);
            }
        });
    }

    private void BuildTeleportPriorityCache()
    {
        TeleportPriorityEntries.Clear();
        if (Schematic == null) return;

        TeleportObject[] teleports = Schematic.GetComponentsInChildren<TeleportObject>(true);
        if (teleports == null || teleports.Length == 0) return;

        List<ClientSidePrimitive> allPrimitives = NonClusteredPrimitives.ToList();
        foreach (PrimitiveCluster cluster in PrimitiveClusters)
            allPrimitives.AddRange(cluster.Primitives);

        foreach (TeleportObject teleport in teleports)
        {
            if (teleport == null) continue;
            Vector3 teleportPos = teleport.transform.position;

            List<ClientSidePrimitive> selected = allPrimitives
                .Where(p => IsTeleportCriticalPrimitive(p, teleportPos))
                .OrderBy(p => GetHorizontalDistanceSqr(p.Position, teleportPos))
                .ThenByDescending(p => p.Position.y)
                .Take(MaxTeleportPriorityPrimitives)
                .ToList();

            if (selected.Count == 0) continue;

            TeleportPriorityEntry entry = new() { TeleportPosition = teleportPos };

            foreach (ClientSidePrimitive primitive in selected)
            {
                if (NonClusteredPrimitives.Contains(primitive))
                {
                    entry.NonClustered.Add(primitive);
                    continue;
                }

                PrimitiveCluster owner = PrimitiveClusters.FirstOrDefault(c => c.Primitives.Contains(primitive));
                if (owner == null) continue;

                if (!entry.Clustered.TryGetValue(owner, out List<ClientSidePrimitive> list))
                {
                    list = new();
                    entry.Clustered[owner] = list;
                }
                list.Add(primitive);
            }

            if (entry.NonClustered.Count > 0 || entry.Clustered.Count > 0)
                TeleportPriorityEntries.Add(entry);
        }
    }

    private static bool IsTeleportCriticalPrimitive(ClientSidePrimitive primitive, Vector3 teleportPos)
    {
        if (!primitive.PrimitiveFlags.HasFlag(AdminToys.PrimitiveFlags.Collidable))
            return false;

        Vector3 offset = primitive.Position - teleportPos;
        if (offset.y > 0f) return false;

        return offset.sqrMagnitude <= TeleportPriorityRadius * TeleportPriorityRadius;
    }

    private static float GetHorizontalDistanceSqr(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    public TeleportPriorityEntry GetClosestTeleportEntry(Vector3 position)
    {
        TeleportPriorityEntry best = null;
        float bestSqr = TeleportMatchRadius * TeleportMatchRadius;

        foreach (TeleportPriorityEntry entry in TeleportPriorityEntries)
        {
            float sqr = (entry.TeleportPosition - position).sqrMagnitude;
            if (sqr > bestSqr) continue;
            best = entry;
            bestSqr = sqr;
        }
        return best;
    }

    public void RefreshFor(Player player)
    {
        HideFor(player, false);

        if (player.Connection is NetworkConnectionToClient conn)
        {
            List<byte[]> batch = new List<byte[]>(NonClusteredPrimitives.Count);
            foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
                batch.Add(primitive.SerializedSpawnMessage);
            NetworkBatcher.EnqueueBatch(conn, batch);
        }

        MerOptimizer.Debug($"Refresh the schematic {_schematicName} for {player.DisplayName} !");
    }

    public void HideFor(Player player, bool showDebug = true)
    {
        if (player == null) return;
        if (showDebug)
            MerOptimizer.Debug($"Hiding client side primitives of {_schematicName} to {player.DisplayName}");

        if (player.Connection is NetworkConnectionToClient conn)
        {
            List<byte[]> batch = new List<byte[]>(NonClusteredPrimitives.Count);
            foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
                batch.Add(primitive.SerializedDestroyMessage);
            NetworkBatcher.EnqueueBatch(conn, batch);
        }
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
        
        if (player.Connection is NetworkConnectionToClient conn)
        {
            List<byte[]> batch = new List<byte[]>(NonClusteredPrimitives.Count);
            foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
                batch.Add(primitive.SerializedSpawnMessage);
            NetworkBatcher.EnqueueBatch(conn, batch);
        }
    }

    public void Destroy()
    {
        if (DistanceCullingManager.Instance != null)
            DistanceCullingManager.Instance.UnregisterSchematic(this);

        int totalPrimitives = GetTotalPrimitiveCount() + 1;
        List<byte[]> destroyBatch = new List<byte[]>(totalPrimitives);

        foreach (ClientSidePrimitive primitive in NonClusteredPrimitives)
            destroyBatch.Add(primitive.SerializedDestroyMessage);

        foreach (PrimitiveCluster cluster in PrimitiveClusters)
        {
            if (cluster.DisplayClusterPrimitive != null)
                destroyBatch.Add(cluster.DisplayClusterPrimitive.SerializedDestroyMessage);
            
            foreach (ClientSidePrimitive primitive in cluster.Primitives)
                destroyBatch.Add(primitive.SerializedDestroyMessage);
        }

        foreach (Player player in Player.List.Where(p => !p.IsDestroyed && !p.IsNpc && !p.IsDummy))
        {
            if (player.Connection is NetworkConnectionToClient conn)
                NetworkBatcher.EnqueueBatch(conn, destroyBatch);
        }

        foreach (Collider collider in Colliders.Where(c => c != null && c.gameObject != null))
            Object.Destroy(collider);

        foreach (PrimitiveCluster cluster in PrimitiveClusters.Where(c => c != null && c.gameObject != null))
            Object.Destroy(cluster.gameObject);

        MerOptimizer.Debug($"Destroyed client side schematic of {_schematicName} !");
    }
}