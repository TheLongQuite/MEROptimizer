﻿using System;
using System.Collections.Generic;
using AdminToys;
using LabApi.Features.Wrappers;
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
    public Dictionary<Player, HashSet<ClientSidePrimitive>> PriorityAwaitingSpawn = new();
    public bool instantSpawn;

    private const int PrioritySpawnPerUpdate = 25;
    private float _numberOfPrimitivePerSpawn;
    private int _updatePassed;
    private bool _multiFrameSpawn;
    public bool spawning;

    private readonly List<Player> _keysBuffer = new(32);
    private readonly List<Player> _spectatorsBuffer = new(16);
    private readonly List<ClientSidePrimitive> _priorityToRemoveBuffer = new(32);

    public void Start()
    {
        instantSpawn = MerOptimizer.NumberOfPrimitivePerSpawn == 0;

        if (MerOptimizer.NumberOfPrimitivePerSpawn < 1 && MerOptimizer.NumberOfPrimitivePerSpawn > 0)
        {
            _numberOfPrimitivePerSpawn = MerOptimizer.NumberOfPrimitivePerSpawn * 10;
            _multiFrameSpawn = true;
        }
        else
            _numberOfPrimitivePerSpawn = MerOptimizer.NumberOfPrimitivePerSpawn;

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
        foreach (ClientSidePrimitive primitive in Primitives)
            primitive.DestroyForEveryone();

        DisplayClusterPrimitive?.DestroyForEveryone();
    }

    public void EnqueueSpawn(Player player)
    {
        AwaitingSpawnRemaining[player] = Primitives.Count;
        spawning = true;
        enabled = true;
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
            if (primitive == null)
                continue;

            prioritySet.Add(primitive);
        }

        spawning = true;
        enabled = true;
    }

    public void Update()
    {
        if (!spawning || (AwaitingSpawnRemaining.Count == 0 && PriorityAwaitingSpawn.Count == 0))
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
                _updatePassed++;
                if (_updatePassed < _numberOfPrimitivePerSpawn)
                    return;

                _updatePassed = 0;
            }

            int normalSpawnCount = _multiFrameSpawn ? 1 : (int)_numberOfPrimitivePerSpawn;
            ProcessNormalQueue(Math.Max(1, normalSpawnCount));
        }

        if (AwaitingSpawnRemaining.Count != 0 || PriorityAwaitingSpawn.Count != 0)
            return;

        spawning = false;
        enabled = false;
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

            _spectatorsBuffer.Clear();
            _spectatorsBuffer.AddRange(player.CurrentSpectators);

            int allowed = Math.Min(spawnCount, set.Count);
            _priorityToRemoveBuffer.Clear();

            int spawnedCount = 0;
            foreach (ClientSidePrimitive prim in set)
            {
                if (spawnedCount >= allowed) break;

                prim.SpawnClientPrimitive(player);
                foreach (Player spectator in _spectatorsBuffer)
                    prim.SpawnClientPrimitive(spectator);

                _priorityToRemoveBuffer.Add(prim);
                spawnedCount++;
            }

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
            if (!AwaitingSpawnRemaining.TryGetValue(player, out int remaining) || remaining <= 0)
            {
                AwaitingSpawnRemaining.Remove(player);
                continue;
            }

            _spectatorsBuffer.Clear();
            _spectatorsBuffer.AddRange(player.CurrentSpectators);

            int toSpawn = Math.Min(spawnCount, remaining);
            for (int i = 0; i < toSpawn; i++)
            {
                remaining--;
                ClientSidePrimitive prim = Primitives[remaining];
                prim.SpawnClientPrimitive(player);

                foreach (Player spectator in _spectatorsBuffer)
                    prim.SpawnClientPrimitive(spectator);
            }

            if (remaining <= 0)
                AwaitingSpawnRemaining.Remove(player);
            else
                AwaitingSpawnRemaining[player] = remaining;
        }
    }

    public void SpawnFor(Player player)
    {
        if (player.IsDestroyed || player.IsHost || player.IsNpc || player.IsDummy)
            return;

        foreach (ClientSidePrimitive primitive in Primitives)
            primitive.SpawnClientPrimitive(player);
    }

    public void UnspawnFor(Player player)
    {
        if (player.IsDestroyed || player.IsHost || player.IsNpc || player.IsDummy)
            return;

        AwaitingSpawnRemaining.Remove(player);
        PriorityAwaitingSpawn.Remove(player);

        _spectatorsBuffer.Clear();
        _spectatorsBuffer.AddRange(player.CurrentSpectators);

        foreach (ClientSidePrimitive primitive in Primitives)
        {
            primitive.DestroyClientPrimitive(player);

            foreach (Player pl in _spectatorsBuffer)
                primitive.DestroyClientPrimitive(pl);
        }
    }

    public void DisplayRadius(Player player) => DisplayClusterPrimitive?.SpawnClientPrimitive(player);

    public void HideRadius(Player player) => DisplayClusterPrimitive?.DestroyClientPrimitive(player);
}