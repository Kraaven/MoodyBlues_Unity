using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoodyBlues.Playground
{
    /// <summary>Tracks what the player has picked up, what has been deposited, and the score.</summary>
    public class Inventory : MonoBehaviour
    {
        [SerializeField] private Interactor interactor;
        [SerializeField] private int totalTargets;

        private readonly HashSet<string> _collected = new();

        public int Score { get; private set; }
        public int Deposited { get; private set; }
        public int TotalTargets => totalTargets;
        public int CollectedCount => _collected.Count;
        public IReadOnlyCollection<string> Collected => _collected;

        public event Action Changed;

        private void OnEnable()
        {
            if (interactor != null) interactor.Picked += OnPicked;
        }

        private void OnDisable()
        {
            if (interactor != null) interactor.Picked -= OnPicked;
        }

        private void OnPicked(Pickable p)
        {
            if (_collected.Add(p.ItemId)) Changed?.Invoke();
        }

        public void Deposit(Pickable p)
        {
            Deposited++;
            Score += p.ScoreValue;
            Changed?.Invoke();
        }

        public void SetTotalTargets(int n) { totalTargets = n; Changed?.Invoke(); }
    }
}
