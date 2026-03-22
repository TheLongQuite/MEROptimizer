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
    public bool instantSpawn;

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

    public void Update()
    {
        if (!spawning || AwaitingSpawn.Count == 0)
        {
            spawning = false;
            return;
        }

        if (_multiFrameSpawn)
        {
            _updatePassed++;
            if (_updatePassed < _numberOfPrimitivePerSpawn) return;
            _updatePassed = 0;
        }

        _keysBuffer.Clear();
        _keysBuffer.AddRange(AwaitingSpawn.Keys);

        foreach (Player player in _keysBuffer)
        {
            if (!AwaitingSpawn.TryGetValue(player, out List<ClientSidePrimitive> list) || list.Count == 0)
            {
                AwaitingSpawn.Remove(player);
                continue;
            }

            _spectatorsBuffer.Clear();
            _spectatorsBuffer.AddRange(player.CurrentSpectators);

            int spawnCount = CalculateSpawnCount(list);

            for (int i = 0; i < spawnCount && list.Count > 0; i++)
            {
                int lastIndex = list.Count - 1;
                ClientSidePrimitive prim = list[lastIndex];
                list.RemoveAt(lastIndex);

                prim.SpawnClientPrimitive(player);

                foreach (Player pl in _spectatorsBuffer)
                    prim.SpawnClientPrimitive(pl);
            }

            if (list.Count == 0)
                AwaitingSpawn.Remove(player);
        }

        if (AwaitingSpawn.Count == 0)
            spawning = false;
    }

    private int CalculateSpawnCount(List<ClientSidePrimitive> list)
    {
        if (list.Count == 0)
            return 0;
     
        int normalCount = _multiFrameSpawn ? 1 : (int)_numberOfPrimitivePerSpawn;
        return Math.Min(normalCount, list.Count);
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