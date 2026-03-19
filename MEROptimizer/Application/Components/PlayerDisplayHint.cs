using LabApi.Features.Wrappers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MEROptimizer.MEROptimizer.Application.Components;
using UnityEngine;

namespace MEROptimizer.Application.Components;

public class PlayerDisplayHint : MonoBehaviour
{
    public Player Player;

    private float _timePassed = 0;

    public void RemoveComponent() => Destroy(this);

    public void Update()
    {
        _timePassed += Time.deltaTime; // mrc serious
        if (_timePassed > .3f)
        {
            _timePassed = 0;


            int count = 0;
            int totalPrimitiveCount = 0;

            foreach (OptimizedSchematic schematic in Plugin.MerOptimizer.OptimizedSchematics)
            {
                count += schematic.NonClusteredPrimitives.Count;
                totalPrimitiveCount +=
                    schematic.SchematicServerSidePrimitiveCount + schematic.NonClusteredPrimitives.Count;

                foreach (PrimitiveCluster cluster in schematic.PrimitiveClusters)
                {
                    if (cluster.InsidePlayers.Contains(Player))
                        count += cluster.Primitives.Count;

                    totalPrimitiveCount += cluster.Primitives.Count;
                }
            }

            if (Player == null)
            {
                Destroy(this);
                return;
            }

            Player.SendHint(
                $"Loaded <color=green>{count}</color> out of a total of <color=red>{totalPrimitiveCount
                }</color> primitives", .5f);
        }
    }
}