using System;
using System.Collections.Generic;
using System.Linq;
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
    public Dictionary<Player, List<ClientSidePrimitive>> AwaitingSpawn = new();
    public Dictionary<Player, List<ClientSidePrimitive>> PriorityAwaitingSpawn = new();
    public bool instantSpawn;

    private const int PrioritySpawnPerUpdate = 25;
    private float _numberOfPrimitivePerSpawn;
    private int _updatePassed;
    private bool _multiFrameSpawn;
    public bool spawning;

    private readonly List<Player> _keysBuffer = new(32);
    private readonly List<Player> _spectatorsBuffer = new(16);

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
    }

    public void OnDestroy()
    {
        foreach (ClientSidePrimitive primitive in Primitives)
            primitive.DestroyForEveryone();

        DisplayClusterPrimitive?.DestroyForEveryone();
    }

    public void EnqueueSpawn(Player player)
    {
        AwaitingSpawn.Remove(player);
        AwaitingSpawn.Add(player, Primitives.ToList());
        spawning = true;
    }

    public void EnqueuePrioritySpawn(Player player, IEnumerable<ClientSidePrimitive> primitives)
    {
        if (player == null || player.IsDestroyed || player.IsHost || player.IsNpc || player.IsDummy)
            return;

        if (!PriorityAwaitingSpawn.TryGetValue(player, out List<ClientSidePrimitive> priorityList))
        {
            priorityList = new();
            PriorityAwaitingSpawn[player] = priorityList;
        }

        AwaitingSpawn.TryGetValue(player, out List<ClientSidePrimitive> normalList);

        foreach (ClientSidePrimitive primitive in primitives)
        {
            if (primitive == null)
                continue;

            if (!priorityList.Contains(primitive))
                priorityList.Add(primitive);

            normalList?.Remove(primitive);
        }

        spawning = true;
    }

    public void Update()
    {
        if (!spawning || (AwaitingSpawn.Count == 0 && PriorityAwaitingSpawn.Count == 0))
        {
            spawning = false;
            return;
        }

        ProcessQueue(PriorityAwaitingSpawn, PrioritySpawnPerUpdate);

        if (AwaitingSpawn.Count > 0)
        {
            if (_multiFrameSpawn)
            {
                _updatePassed++;
                if (_updatePassed < _numberOfPrimitivePerSpawn)
                    return;

                _updatePassed = 0;
            }

            int normalSpawnCount = _multiFrameSpawn ? 1 : (int)_numberOfPrimitivePerSpawn;
            ProcessQueue(AwaitingSpawn, Math.Max(1, normalSpawnCount));
        }

        if (AwaitingSpawn.Count == 0 && PriorityAwaitingSpawn.Count == 0)
            spawning = false;
    }

    private void ProcessQueue(Dictionary<Player, List<ClientSidePrimitive>> queue, int spawnCount)
    {
        if (queue.Count == 0)
            return;

        _keysBuffer.Clear();
        _keysBuffer.AddRange(queue.Keys);

        foreach (Player player in _keysBuffer)
        {
            if (!queue.TryGetValue(player, out List<ClientSidePrimitive> list) || list.Count == 0)
            {
                queue.Remove(player);
                continue;
            }

            _spectatorsBuffer.Clear();
            _spectatorsBuffer.AddRange(player.CurrentSpectators);

            int allowed = Math.Min(spawnCount, list.Count);
            for (int i = 0; i < allowed && list.Count > 0; i++)
            {
                int lastIndex = list.Count - 1;
                ClientSidePrimitive prim = list[lastIndex];
                list.RemoveAt(lastIndex);

                prim.SpawnClientPrimitive(player);

                foreach (Player spectator in _spectatorsBuffer)
                    prim.SpawnClientPrimitive(spectator);
            }

            if (list.Count == 0)
                queue.Remove(player);
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

        AwaitingSpawn.Remove(player);
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