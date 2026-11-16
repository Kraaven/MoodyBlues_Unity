using UnityEngine;

namespace MoodyBlues.Playground
{
    /// <summary>Anything the <see cref="Interactor"/> can aim at and use with the interact key.</summary>
    public interface IInteractable
    {
        /// <summary>Prompt text for the current state, e.g. "Pick up Apple". Null/empty hides the prompt.</summary>
        string GetPrompt(Interactor interactor);

        void Interact(Interactor interactor);
    }
}
