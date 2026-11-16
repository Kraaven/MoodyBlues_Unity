using UnityEngine;
using UnityEngine.UI;

namespace MoodyBlues.Playground
{
    /// <summary>Updates the prompt, inventory/score and goal text.</summary>
    public class PlaygroundHud : MonoBehaviour
    {
        [SerializeField] private Interactor interactor;
        [SerializeField] private Inventory inventory;
        [SerializeField] private Text promptText;
        [SerializeField] private Text inventoryText;
        [SerializeField] private Text hintText;

        private void OnEnable()
        {
            if (inventory != null) inventory.Changed += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            if (inventory != null) inventory.Changed -= Refresh;
        }

        private void Update()
        {
            if (promptText == null || interactor == null) return;
            string p = interactor.CurrentPrompt();
            promptText.text = p ?? string.Empty;
        }

        private void Refresh()
        {
            if (inventoryText == null || inventory == null) return;
            inventoryText.text = $"Collected: {inventory.CollectedCount}   Deposited: {inventory.Deposited}/{inventory.TotalTargets}   Score: {inventory.Score}";
        }
    }
}
