using LabApi.Features.Wrappers;
using UnityEngine;

namespace MEROptimizer.MEROptimizer.Application.Components;

public class PlayerTrigger : MonoBehaviour
{
    public Player Player { get; set; }

    private Vector3 _offset;
    private Vector3 _lastPosition;
    private float _timer;

    private const float UpdateInterval = 0.25f;
    private const float MinMoveSqr = 1.5f;

    void Start()
    {
        _offset = new(0, 2000, 0);
        _lastPosition = Vector3.positiveInfinity;
    }

    public void Update()
    {
        if (Player == null || !Player.ReferenceHub.transform)
        {
            Destroy(gameObject);
            return;
        }

        _timer += Time.deltaTime;
        if (_timer < UpdateInterval)
            return;
        _timer = 0f;

        Vector3 targetPos = Player.ReferenceHub.transform.position + _offset;

        if ((targetPos - _lastPosition).sqrMagnitude < MinMoveSqr)
            return;

        transform.position = targetPos;
        _lastPosition = targetPos;
    }
}