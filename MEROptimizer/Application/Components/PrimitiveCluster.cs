using System;
using System.Collections.Generic;
using AdminToys;
using LabApi.Features.Wrappers;
using Mirror;
using UnityEngine;

namespace MEROptimizer.MEROptimizer.Application.Components;

public class PrimitiveCluster : MonoBehaviour
{
    public int ID { get; set; }
    public List<ClientSidePrimitive> Primitives { get; set; }
    public ClientSidePrimitive DisplayClusterPrimitive { get; set; }
    public Vector3 CenterPosition { get; set; }
    public float SpawnDistance { get; set; }
    
    public Dictionary<Player, int> AwaitingSpawnRemaining = new();
    public Dictionary<Player, int> SlowAwaitingSpawn = new();
    public Dictionary<Player, HashSet<ClientSidePrimitive>> PriorityAwaitingSpawn = new();
    public bool instantSpawn;

    private const int PrioritySpawnPerUpdate = 25;
    private const int SlowSpawnInterval = 30;
    private float _framesPerSpawn;
    private int _primitivesPerSpawn;
    private float _updatePassed;
    private int _slowTickCounter;
    private bool _multiFrameSpawn;
    public bool spawning;

    private readonly List<Player> _keysBuffer = new(32);
    private readonly List<NetworkConnectionToClient> _connectionsBuffer = new(32);
    private readonly List<ClientSidePrimitive> _priorityToRemoveBuffer = new(32);
    private readonly List<byte[]> _batchBuffer = new(256);

    public void Start()
    {
        instantSpawn = MerOptimizer.NumberOfPrimitivePerSpawn == 0;

        if (MerOptimizer.NumberOfPrimitivePerSpawn > 0 && MerOptimizer.NumberOfPrimitivePerSpawn < 1)
        {
            _framesPerSpawn = 1f / MerOptimizer.NumberOfPrimitivePerSpawn;
            _multiFrameSpawn = true;
        }
        else
        {
            _primitivesPerSpawn = Math.Max(1, (int)MerOptimizer.NumberOfPrimitivePerSpawn);
            _multiFrameSpawn = false;
        }

        DisplayClusterPrimitive = new(CenterPosition,
            Quaternion.identity,
            Vector3.one * SpawnDistance,
            PrimitiveType.Sphere,
            new(1, 0, 1, .4f),
            PrimitiveFlags.Visible);
        
        enabled = false;
    }

    public void OnDestroy()
    {
        DisplayClusterPrimitive?.DestroyForEveryone();
        AwaitingSpawnRemaining.Clear();
        SlowAwaitingSpawn.Clear();
        PriorityAwaitingSpawn.Clear();
    }

    public void EnqueueSpawn(Player player)
    {
        AwaitingSpawnRemaining[player] = 0;
        spawning = true;
        enabled = true;
    }

    public void EnqueueSlowSpawn(Player player)
    {
        if (!SlowAwaitingSpawn.ContainsKey(player))
        {
            SlowAwaitingSpawn[player] = Primitives.Count;
            spawning = true;
            enabled = true;
        }
    }

    public void EnqueuePrioritySpawn(Player player, IEnumerable<ClientSidePrimitive> primitives)
    {
        if (player == null || player.IsDestroyed || player.IsHost || player.IsNpc || player.IsDummy)
            return;

        if (!PriorityAwaitingSpawn.TryGetValue(player, out HashSet<ClientSidePrimitive> prioritySet))
        {
            prioritySet = new();
            PriorityAwaitingSpawn[player] = prioritySet;
        }

        foreach (ClientSidePrimitive primitive in primitives)
        {
            if (primitive == null) continue;
            prioritySet.Add(primitive);
        }

        spawning = true;
        enabled = true;
    }

    public void Update()
    {
        if (!spawning || (AwaitingSpawnRemaining.Count == 0 && PriorityAwaitingSpawn.Count == 0 && SlowAwaitingSpawn.Count == 0))
        {
            spawning = false;
            enabled = false;
            return;
        }

        ProcessPriorityQueue(PrioritySpawnPerUpdate);

        if (AwaitingSpawnRemaining.Count > 0)
        {
            if (_multiFrameSpawn)
            {
                _updatePassed += 1f;
                if (_updatePassed < _framesPerSpawn)
                    return;

                _updatePassed = 0f;
                ProcessNormalQueue(1);
            }
            else
            {
                ProcessNormalQueue(_primitivesPerSpawn);
            }
        }

        _slowTickCounter++;
        if (_slowTickCounter >= SlowSpawnInterval)
        {
            _slowTickCounter = 0;
            ProcessSlowQueue(1);
        }

        if (AwaitingSpawnRemaining.Count != 0 || PriorityAwaitingSpawn.Count != 0 || SlowAwaitingSpawn.Count != 0)
            return;

        spawning = false;
        enabled = false;
    }

    private void CollectConnections(Player player, bool includeSpectators = true)
    {
        _connectionsBuffer.Clear();
        if (player.Connection is NetworkConnectionToClient playerConn)
            _connectionsBuffer.Add(playerConn);

        if (includeSpectators)
        {
            foreach (Player spectator in player.CurrentSpectators)
            {
                if (spectator.Connection is NetworkConnectionToClient specConn)
                    _connectionsBuffer.Add(specConn);
            }
        }
    }

    private void ProcessPriorityQueue(int spawnCount)
    {
        if (PriorityAwaitingSpawn.Count == 0) return;

        _keysBuffer.Clear();
        _keysBuffer.AddRange(PriorityAwaitingSpawn.Keys);

        foreach (Player player in _keysBuffer)
        {
            if (!PriorityAwaitingSpawn.TryGetValue(player, out HashSet<ClientSidePrimitive> set) || set.Count == 0)
            {
                PriorityAwaitingSpawn.Remove(player);
                continue;
            }

            CollectConnections(player, true);

            int allowed = Math.Min(spawnCount, set.Count);
            _priorityToRemoveBuffer.Clear();
            _batchBuffer.Clear();

            int spawnedCount = 0;
            foreach (ClientSidePrimitive prim in set)
            {
                if (spawnedCount >= allowed) break;
                _batchBuffer.Add(prim.SerializedSpawnMessage);
                _priorityToRemoveBuffer.Add(prim);
                spawnedCount++;
            }

            foreach (NetworkConnectionToClient conn in _connectionsBuffer)
                NetworkBatcher.EnqueueBatch(conn, _batchBuffer);

            foreach (ClientSidePrimitive prim in _priorityToRemoveBuffer)
                set.Remove(prim);

            if (set.Count == 0)
                PriorityAwaitingSpawn.Remove(player);
        }
    }

    private void ProcessNormalQueue(int spawnCount)
    {
        if (AwaitingSpawnRemaining.Count == 0) return;

        _keysBuffer.Clear();
        _keysBuffer.AddRange(AwaitingSpawnRemaining.Keys);

        foreach (Player player in _keysBuffer)
        {
            if (!AwaitingSpawnRemaining.TryGetValue(player, out int nextIdx) || nextIdx < 0 || nextIdx >= Primitives.Count)
            {
                AwaitingSpawnRemaining.Remove(player);
                continue;
            }

            CollectConnections(player, true);

            int toSpawn = Math.Min(spawnCount, Primitives.Count - nextIdx);
            _batchBuffer.Clear();
        
            for (int i = 0; i < toSpawn; i++)
            {
                _batchBuffer.Add(Primitives[nextIdx + i].SerializedSpawnMessage);
            }

            foreach (NetworkConnectionToClient conn in _connectionsBuffer)
                NetworkBatcher.EnqueueBatch(conn, _batchBuffer);

            nextIdx += toSpawn;

            if (nextIdx >= Primitives.Count)
                AwaitingSpawnRemaining.Remove(player);
            else
                AwaitingSpawnRemaining[player] = nextIdx;
        }
    }

    private void ProcessSlowQueue(int spawnCount)
    {
        if (SlowAwaitingSpawn.Count == 0) return;

        _keysBuffer.Clear();
        _keysBuffer.AddRange(SlowAwaitingSpawn.Keys);

        foreach (Player player in _keysBuffer)
        {
            if (!SlowAwaitingSpawn.TryGetValue(player, out int nextIdx) || nextIdx < 0 || nextIdx >= Primitives.Count)
            {
                SlowAwaitingSpawn.Remove(player);
                continue;
            }

            CollectConnections(player, false);

            int toSpawn = Math.Min(spawnCount, Primitives.Count - nextIdx);
            _batchBuffer.Clear();

            for (int i = 0; i < toSpawn; i++)
            {
                _batchBuffer.Add(Primitives[nextIdx + i].SerializedSpawnMessage);
            }

            foreach (NetworkConnectionToClient conn in _connectionsBuffer)
                NetworkBatcher.EnqueueBatch(conn, _batchBuffer);

            nextIdx += toSpawn;

            if (nextIdx >= Primitives.Count)
                SlowAwaitingSpawn.Remove(player);
            else
                SlowAwaitingSpawn[player] = nextIdx;
        }
    }

    public void SpawnFor(Player player)
    {
        if (player.IsDestroyed || player.IsHost || player.IsNpc || player.IsDummy)
            return;

        CollectConnections(player, true);
        _batchBuffer.Clear();

        foreach (ClientSidePrimitive primitive in Primitives)
            _batchBuffer.Add(primitive.SerializedSpawnMessage);

        foreach (NetworkConnectionToClient conn in _connectionsBuffer)
            NetworkBatcher.EnqueueBatch(conn, _batchBuffer);
    }

    public void UnspawnFor(Player player)
    {
        if (player.IsDestroyed || player.IsHost || player.IsNpc || player.IsDummy)
            return;

        AwaitingSpawnRemaining.Remove(player);
        SlowAwaitingSpawn.Remove(player);
        PriorityAwaitingSpawn.Remove(player);

        CollectConnections(player, true);
        _batchBuffer.Clear();

        foreach (ClientSidePrimitive primitive in Primitives)
            _batchBuffer.Add(primitive.SerializedDestroyMessage);

        foreach (NetworkConnectionToClient conn in _connectionsBuffer)
            NetworkBatcher.EnqueueBatch(conn, _batchBuffer);
    }

    public void DisplayRadius(Player player)
    {
        if (DisplayClusterPrimitive == null) return;
        if (player.Connection is NetworkConnectionToClient conn)
            NetworkBatcher.Enqueue(conn, DisplayClusterPrimitive.SerializedSpawnMessage);
    }

    public void HideRadius(Player player)
    {
        if (DisplayClusterPrimitive == null) return;
        if (player.Connection is NetworkConnectionToClient conn)
            NetworkBatcher.Enqueue(conn, DisplayClusterPrimitive.SerializedDestroyMessage);
    }
}