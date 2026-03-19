using LabApi.Features.Wrappers;
using UnityEngine;

namespace MEROptimizer.MEROptimizer.Application.Components;

public class PlayerDisplayHint : MonoBehaviour
{
    public Player Player;

    private float _timePassed;

    public void RemoveComponent() => Destroy(this);

    public void Update()
    {
        _timePassed += Time.deltaTime;
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
                    if (DistanceCullingManager.Instance &&
                        DistanceCullingManager.Instance.IsPlayerInsideCluster(Player, cluster))
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