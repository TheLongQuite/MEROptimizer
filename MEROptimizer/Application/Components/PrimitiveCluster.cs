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

    public Dictionary<Player, List<ClientSidePrimitive>> AwaitingSpawn = new();

    public List<Player> InsidePlayers = new();

    public bool instantSpawn;

    private float _numberOfPrimitivePerSpawn;

    private int _updatePassed = 0;

    private bool _multiFrameSpawn = false;

    public bool spawning = false;

    private readonly List<Player> _keysBuffer = new(32);
    private readonly List<Player> _spectatorsBuffer = new(16);

    public void Start()
    {
        instantSpawn = global::MEROptimizer.MEROptimizer.Application.MerOptimizer.NumberOfPrimitivePerSpawn == 0;

        if (global::MEROptimizer.MEROptimizer.Application.MerOptimizer.NumberOfPrimitivePerSpawn < 1 && global::MEROptimizer.MEROptimizer.Application.MerOptimizer.NumberOfPrimitivePerSpawn > 0)
        {
            _numberOfPrimitivePerSpawn = global::MEROptimizer.MEROptimizer.Application.MerOptimizer.NumberOfPrimitivePerSpawn * 10;
            _multiFrameSpawn = true;
        }
        else
        {
            _numberOfPrimitivePerSpawn = global::MEROptimizer.MEROptimizer.Application.MerOptimizer.NumberOfPrimitivePerSpawn;
        }

        float radius = GetComponent<SphereCollider>().radius;
        DisplayClusterPrimitive = new(this.transform.position - new Vector3(0, 2000, 0), 
            this.transform.rotation, Vector3.one * radius, PrimitiveType.Sphere, new(1, 0, 1, .4f),
            PrimitiveFlags.Visible);
    }

    public void OnDestroy()
    {
        foreach (ClientSidePrimitive primitive in Primitives)
            primitive.DestroyForEveryone();

        DisplayClusterPrimitive?.DestroyForEveryone();
    }

    public void OnTriggerEnter(Collider collider)
    {
        if (collider == null || collider.transform.parent != null) return;
        if (!collider.CompareTag("Player") || !collider.gameObject.TryGetComponent(out PlayerTrigger playerTrigger)) return;

        Player player = playerTrigger.Player;
        if (player == null) return;

        if (!global::MEROptimizer.MEROptimizer.Application.MerOptimizer.ShouldTutorialsBeAffectedByDistanceSpawning && player.Role == PlayerRoles.RoleTypeId.Tutorial) return;
        if (player.Role == PlayerRoles.RoleTypeId.Filmmaker) return;

        if (!player.IsNpc)
        {
            if (instantSpawn)
                SpawnFor(player);
            else
            {
                AwaitingSpawn.Remove(player);
                AwaitingSpawn.Add(player, Primitives.ToList());
                spawning = true;
            }
        }

        if (!InsidePlayers.Contains(player))
            InsidePlayers.Add(player);
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

        for (int k = 0; k < _keysBuffer.Count; k++)
        {
            Player player = _keysBuffer[k];
            if (!AwaitingSpawn.TryGetValue(player, out List<ClientSidePrimitive> list) || list.Count == 0)
            {
                AwaitingSpawn.Remove(player);
                continue;
            }

            _spectatorsBuffer.Clear();
            _spectatorsBuffer.AddRange(player.CurrentSpectators);

            int spawnCount = _multiFrameSpawn ? 1 : (int)_numberOfPrimitivePerSpawn;
            spawnCount = Math.Min(spawnCount, list.Count);

            for (int i = 0; i < spawnCount; i++)
            {
                int lastIndex = list.Count - 1;
                ClientSidePrimitive prim = list[lastIndex];
                list.RemoveAt(lastIndex);

                prim.SpawnClientPrimitive(player);

                for (int s = 0; s < _spectatorsBuffer.Count; s++)
                {
                    prim.SpawnClientPrimitive(_spectatorsBuffer[s]);
                }
            }

            if (list.Count == 0)
            {
                AwaitingSpawn.Remove(player);
            }
        }

        if (AwaitingSpawn.Count == 0)
        {
            spawning = false;
        }
    }

    public void OnTriggerExit(Collider collider)
    {
        if (collider == null || collider.transform.parent != null) return;
        if (!collider.CompareTag("Player") || !collider.gameObject.TryGetComponent(out PlayerTrigger playerTrigger)) return;

        Player player = playerTrigger.Player;
        if (player == null) return;

        if (!global::MEROptimizer.MEROptimizer.Application.MerOptimizer.ShouldTutorialsBeAffectedByDistanceSpawning && player.Role == PlayerRoles.RoleTypeId.Tutorial) return;
        if (player.Role == PlayerRoles.RoleTypeId.Filmmaker) return;

        AwaitingSpawn.Remove(player);
        UnspawnFor(player);
        InsidePlayers.Remove(player);
    }

    public void SpawnFor(Player player)
    {
        if (player == null || player.IsNpc) return;
        foreach (ClientSidePrimitive primitive in Primitives)
        {
            primitive.SpawnClientPrimitive(player);
        }
    }

    public void UnspawnFor(Player player)
    {
        if (player == null || player.IsNpc) return;

        AwaitingSpawn.Remove(player);

        _spectatorsBuffer.Clear();
        _spectatorsBuffer.AddRange(player.CurrentSpectators);

        foreach (ClientSidePrimitive primitive in Primitives)
        {
            primitive.DestroyClientPrimitive(player);

            for (int i = 0; i < _spectatorsBuffer.Count; i++)
            {
                primitive.DestroyClientPrimitive(_spectatorsBuffer[i]);
            }
        }
    }

    public void DisplayRadius(Player player)
    {
        DisplayClusterPrimitive?.SpawnClientPrimitive(player);
    }

    public void HideRadius(Player player)
    {
        DisplayClusterPrimitive?.DestroyClientPrimitive(player);
    }
}