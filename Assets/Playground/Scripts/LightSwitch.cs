using System;
using UnityEngine;

namespace MoodyBlues.Playground
{
    /// <summary>Toggles a set of lights (and emissive visuals) on interact.</summary>
    public class LightSwitch : MonoBehaviour, IInteractable
    {
        [SerializeField] private string label = "lamp";
        [SerializeField] private Light[] lights;
        [SerializeField] private bool isOn = true;

        public bool IsOn => isOn;
        public event Action<LightSwitch, bool> Toggled;

        private void Start() => Apply();

        public string GetPrompt(Interactor i) => isOn ? $"[E] Turn off {label}" : $"[E] Turn on {label}";

        public void Interact(Interactor i)
        {
            isOn = !isOn;
            Apply();
            Toggled?.Invoke(this, isOn);
        }

        private void Apply()
        {
            foreach (Light l in lights) if (l != null) l.enabled = isOn;
        }
    }
}
