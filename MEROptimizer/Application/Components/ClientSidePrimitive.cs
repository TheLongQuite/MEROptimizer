using System;
using System.Linq;
using AdminToys;
using LabApi.Features.Wrappers;
using Mirror;
using UnityEngine;

namespace MEROptimizer.MEROptimizer.Application.Components;

public class ClientSidePrimitive
{
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Scale { get; set; }
    public PrimitiveType PrimitiveType { get; set; }
    public Color Color { get; set; }
    public PrimitiveFlags PrimitiveFlags { get; set; }

    public SpawnMessage SpawnMessage { get; set; }
    public ObjectDestroyMessage DestroyMessage { get; set; }
    public uint NetId { get; set; }

    public ClientSidePrimitive(Vector3 position, Quaternion rotation, Vector3 scale, PrimitiveType primitiveType, Color color, PrimitiveFlags primitiveFlags)
    {
        this.Position = position;
        this.Rotation = rotation;
        this.Scale = scale;
        this.PrimitiveType = primitiveType;
        this.Color = color;
        this.PrimitiveFlags = primitiveFlags;
        this.NetId = NetworkIdentity.GetNextNetworkId();
        GenerateNetworkMessages();
    }

    private void GenerateNetworkMessages()
    {
        NetworkWriterPooled writer = NetworkWriterPool.Get();
        try
        {
            writer.Write<byte>(1);
            writer.Write<byte>(67);
            writer.Write<Vector3>(Position);
            writer.Write<Quaternion>(Rotation);
            writer.Write<Vector3>(Scale);
            writer.Write<byte>(0);
            writer.Write<bool>(false);
            writer.Write<int>((int)PrimitiveType);
            writer.Write<Color>(Color);
            writer.Write<byte>((byte)PrimitiveFlags);
            writer.Write<uint>(0);

            ArraySegment<byte> segment = writer.ToArraySegment();
            byte[] payloadCopy = new byte[segment.Count];
            Buffer.BlockCopy(segment.Array!, segment.Offset, payloadCopy, 0, segment.Count);

            SpawnMessage = new()
            {
                netId = NetId,
                isLocalPlayer = false,
                isOwner = false,
                sceneId = 0,
                assetId = global::MEROptimizer.MEROptimizer.Application.MerOptimizer.PrimitiveAssetId,
                position = Position,
                rotation = Rotation,
                scale = Scale,
                payload = new(payloadCopy)
            };

            DestroyMessage = new()
            {
                netId = NetId,
            };
        }
        finally
        {
            NetworkWriterPool.Return(writer);
        }
    }

    public void DestroyForEveryone()
    {
        foreach (Player player in Player.List.Where(p => !p.IsDestroyed && !p.IsNpc && !p.IsDummy))
            DestroyClientPrimitive(player);
    }

    public void DestroyClientPrimitive(Player target)
    {
        if (target == null || target.IsHost) 
            return;
            
        target.Connection?.Send(DestroyMessage);
    }

    public void SpawnForEveryone()
    {
        foreach (Player player in Player.List.Where(p => !p.IsDestroyed && !p.IsNpc && !p.IsDummy))
            SpawnClientPrimitive(player);
    }

    public void SpawnClientPrimitive(Player target)
    {
        if (target == null || target.IsHost) return;
        target.Connection?.Send(SpawnMessage);
    }
}